using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Lidarr.Plugin.Bandcamp.CookiePush;

public static class CookiePushCommand
{
    private const string Help = """
        Usage: bandcamp-cookie-push [options] [ < identity-cookie.txt ]

        With no cookie on stdin, opens a visible browser on Bandcamp's login page in a
        fresh temporary profile. Log in and complete any captcha or verification yourself.
        The captured identity cookie updates a Bandcamp download client through Lidarr's
        API, then runs Lidarr's download client Test.

        Options:
          --url <url>          Lidarr base URL (or LIDARR_URL)
          --api-key <key>      Lidarr API key (or LIDARR_API_KEY)
          --client-id <id>     Bandcamp download client id
          --client-name <name> Bandcamp download client name
          --print-cookie       Print identity=<cookie> instead of connecting to Lidarr
          --dry-run            Report selected providers without updating or testing them
          --update-indexer     Also update and test a Bandcamp indexer
          --indexer-id <id>    Bandcamp indexer id
          --indexer-name <name> Bandcamp indexer name
          -h, --help           Show this help

        Pass the cookie on stdin, never as a command-line argument. The cookie is only
        printed with --print-cookie; the API key is never printed.
        """;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<string, string?> getEnvironmentVariable,
        HttpMessageHandler httpHandler,
        IBrowserCookieCapture? browserCookieCapture = null,
        CancellationToken cancellationToken = default)
    {
        if (args.Count == 1 && args[0] is "-h" or "--help")
        {
            output.WriteLine(Help);
            return 0;
        }

        try
        {
            return await RunCoreAsync(args, input, output, getEnvironmentVariable, httpHandler, browserCookieCapture, cancellationToken);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or Microsoft.Playwright.PlaywrightException)
        {
            error.WriteLine("Cancelled. Completed Lidarr writes are not rolled back; check settings before retrying.");
            return 130;
        }
        catch (UsageException ex)
        {
            error.WriteLine("Error: " + ex.Message);
            return 2;
        }
        catch (BrowserWindowClosedException ex)
        {
            error.WriteLine("Error: " + ex.Message);
            return 1;
        }
        catch (LidarrException ex)
        {
            error.WriteLine("Error: " + ex.Message);
            return 1;
        }
        catch (HttpRequestException)
        {
            error.WriteLine("Error: Could not reach Lidarr. Check the URL and network connection.");
            return 1;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("Error: Could not reach Lidarr. The request timed out.");
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(
        IReadOnlyList<string> args,
        TextReader input,
        TextWriter output,
        Func<string, string?> getEnvironmentVariable,
        HttpMessageHandler httpHandler,
        IBrowserCookieCapture? browserCookieCapture,
        CancellationToken cancellationToken)
    {
        var options = Options.Parse(args, getEnvironmentVariable);
        var cookie = (await input.ReadToEndAsync(cancellationToken)).Trim();
        if (cookie.Length == 0)
        {
            if (browserCookieCapture is null)
            {
                throw new UsageException("Paste an identity cookie on stdin.");
            }

            output.WriteLine("Opening a browser. Log in to Bandcamp and complete any verification yourself.");
            cookie = await browserCookieCapture.CaptureAsync(cancellationToken);
            output.WriteLine("Bandcamp identity cookie captured.");
        }

        if (options.PrintCookie)
        {
            output.WriteLine(cookie.StartsWith("identity=", StringComparison.OrdinalIgnoreCase) ? cookie : "identity=" + cookie);
            return 0;
        }

        using var http = new HttpClient(httpHandler)
        {
            BaseAddress = new Uri(options.Url!.TrimEnd('/') + "/"),
        };
        http.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var client = await SelectProviderAsync(http, "downloadclient", "download client", "client", "BandcampDownloadClient", options.ClientId, options.ClientName, cancellationToken);
        var indexer = options.UpdateIndexer
            ? await SelectProviderAsync(http, "indexer", "indexer", "indexer", "BandcampIndexer", options.IndexerId, options.IndexerName, cancellationToken)
            : null;

        await UpdateProviderAsync(http, client, "downloadclient", "download client", cookie, options.DryRun, output, cancellationToken);
        if (indexer is not null)
        {
            await UpdateProviderAsync(http, indexer, "indexer", "indexer", cookie, options.DryRun, output, cancellationToken);
        }

        return 0;
    }

    private static async Task<JsonObject> SelectProviderAsync(HttpClient http, string endpoint, string label, string selector, string implementation, int? id, string? name, CancellationToken cancellationToken)
    {
        var providers = await SendAsync<JsonArray>(http, HttpMethod.Get, $"api/v1/{endpoint}", null, cancellationToken);
        var matches = providers.Where(node => string.Equals(node?["implementation"]?.GetValue<string>(), implementation, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            throw new LidarrException($"No Bandcamp {label} found in Lidarr.");
        }

        var selected = matches.Where(node => id.HasValue
            ? node?["id"]?.GetValue<int>() == id
            : name is null || string.Equals(node?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count != 1)
        {
            throw new UsageException($"Choose one Bandcamp {label} with --{selector}-id <id> or --{selector}-name <name>.");
        }

        return selected[0]!.AsObject();
    }

    private static async Task UpdateProviderAsync(HttpClient http, JsonObject provider, string endpoint, string label, string cookie, bool dryRun, TextWriter output, CancellationToken cancellationToken)
    {
        var id = provider["id"]!.GetValue<int>();
        var name = provider["name"]?.GetValue<string>();
        if (dryRun)
        {
            output.WriteLine($"Dry run: would update Session Cookie for '{name}' (id {id}) and run Lidarr {label} Test. No changes were made.");
            return;
        }

        var cookieField = provider["fields"]!.AsArray().Single(field =>
            string.Equals(field?["name"]?.GetValue<string>(), "cookies", StringComparison.OrdinalIgnoreCase));
        cookieField!["value"] = cookie.StartsWith("identity=", StringComparison.OrdinalIgnoreCase) ? cookie : "identity=" + cookie;
        await SendAsync<JsonObject>(http, HttpMethod.Put, $"api/v1/{endpoint}/{id}", new StringContent(provider.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken);
        output.WriteLine($"Updated Session Cookie for '{name}' (id {id}).");
        await SendAsync<JsonObject>(http, HttpMethod.Post, $"api/v1/{endpoint}/test", new StringContent(provider.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken, testLabel: label);
        output.WriteLine($"Lidarr {label} Test passed.");
    }

    private static async Task<T> SendAsync<T>(HttpClient http, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken, string? testLabel = null)
        where T : JsonNode
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new LidarrException("Lidarr rejected the API key. Check --api-key or LIDARR_API_KEY.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new LidarrException(testLabel is not null
                ? $"Lidarr {testLabel} Test failed with HTTP {(int)response.StatusCode}. Check the cookie and {testLabel} settings."
                : $"Lidarr request failed with HTTP {(int)response.StatusCode}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(json) ? null! : JsonNode.Parse(json) as T ?? throw new InvalidDataException("Lidarr returned an unexpected JSON response.");
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class LidarrException(string message) : Exception(message);

    private sealed record Options(string? Url, string? ApiKey, int? ClientId, string? ClientName, bool PrintCookie, bool DryRun, bool UpdateIndexer, int? IndexerId, string? IndexerName)
    {
        public static Options Parse(IReadOnlyList<string> args, Func<string, string?> environment)
        {
            string? url = null;
            string? apiKey = null;
            int? clientId = null;
            string? clientName = null;
            var printCookie = false;
            var dryRun = false;
            var updateIndexer = false;
            int? indexerId = null;
            string? indexerName = null;
            for (var i = 0; i < args.Count; i++)
            {
                switch (args[i])
                {
                    case "--url":
                        url = ReadValue(args, ref i);
                        break;
                    case "--api-key":
                        apiKey = ReadValue(args, ref i);
                        break;
                    case "--client-id":
                        var idValue = ReadValue(args, ref i);
                        if (!int.TryParse(idValue, out var parsedId) || parsedId <= 0)
                        {
                            throw new UsageException("--client-id must be a positive integer.");
                        }

                        clientId = parsedId;
                        break;
                    case "--client-name":
                        clientName = ReadValue(args, ref i);
                        break;
                    case "--print-cookie":
                        printCookie = true;
                        break;
                    case "--dry-run":
                        dryRun = true;
                        break;
                    case "--update-indexer":
                        updateIndexer = true;
                        break;
                    case "--indexer-id":
                        var indexerIdValue = ReadValue(args, ref i);
                        if (!int.TryParse(indexerIdValue, out var parsedIndexerId) || parsedIndexerId <= 0)
                        {
                            throw new UsageException("--indexer-id must be a positive integer.");
                        }

                        indexerId = parsedIndexerId;
                        break;
                    case "--indexer-name":
                        indexerName = ReadValue(args, ref i);
                        break;
                    default:
                        throw new UsageException("Unknown option. Use --help for usage.");
                }
            }

            if (clientId.HasValue && clientName is not null)
            {
                throw new UsageException("Use either --client-id or --client-name, not both.");
            }

            if (indexerId.HasValue && indexerName is not null)
            {
                throw new UsageException("Use either --indexer-id or --indexer-name, not both.");
            }

            url ??= environment("LIDARR_URL");
            apiKey ??= environment("LIDARR_API_KEY");
            if (!printCookie && string.IsNullOrWhiteSpace(url))
            {
                throw new UsageException("Lidarr URL is required. Use --url or LIDARR_URL.");
            }

            if (!printCookie && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            {
                throw new UsageException("Lidarr URL must be an absolute HTTP or HTTPS URL.");
            }

            if (!printCookie && string.IsNullOrWhiteSpace(apiKey))
            {
                throw new UsageException("Lidarr API key is required. Use --api-key or LIDARR_API_KEY.");
            }

            return new Options(url, apiKey, clientId, clientName, printCookie, dryRun, updateIndexer, indexerId, indexerName);
        }

        private static string ReadValue(IReadOnlyList<string> args, ref int index)
        {
            if (++index >= args.Count)
            {
                throw new UsageException($"Option '{args[index - 1]}' requires a value.");
            }

            return args[index];
        }
    }
}
