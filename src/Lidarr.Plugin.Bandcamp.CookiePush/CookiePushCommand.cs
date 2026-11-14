using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Lidarr.Plugin.Bandcamp.CookiePush;

public static class CookiePushCommand
{
    private const string Help = """
        Usage: bandcamp-cookie-push [options] < identity-cookie.txt

        Reads an identity cookie from stdin, updates a Bandcamp download client through
        Lidarr's API, and runs Lidarr's download client Test.

        Options:
          --url <url>          Lidarr base URL (or LIDARR_URL)
          --api-key <key>      Lidarr API key (or LIDARR_API_KEY)
          --client-id <id>     Bandcamp download client id
          --client-name <name> Bandcamp download client name
          -h, --help           Show this help

        The cookie is needed on stdin, not as a command-line argument. Secrets are never printed.
        """;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<string, string?> getEnvironmentVariable,
        HttpMessageHandler httpHandler,
        CancellationToken cancellationToken = default)
    {
        if (args.Count == 1 && args[0] is "-h" or "--help")
        {
            output.WriteLine(Help);
            return 0;
        }

        try
        {
            return await RunCoreAsync(args, input, output, getEnvironmentVariable, httpHandler, cancellationToken);
        }
        catch (UsageException ex)
        {
            error.WriteLine("Error: " + ex.Message);
            return 2;
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
        CancellationToken cancellationToken)
    {
        var options = Options.Parse(args, getEnvironmentVariable);
        var cookie = (await input.ReadToEndAsync(cancellationToken)).Trim();
        if (cookie.Length == 0)
        {
            throw new UsageException("Paste an identity cookie on stdin.");
        }

        using var http = new HttpClient(httpHandler)
        {
            BaseAddress = new Uri(options.Url.TrimEnd('/') + "/"),
        };
        http.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var clients = await SendAsync<JsonArray>(http, HttpMethod.Get, "api/v1/downloadclient", null, cancellationToken);
        var bandcampClients = clients.Where(node =>
            string.Equals(node?["implementation"]?.GetValue<string>(), "BandcampDownloadClient", StringComparison.OrdinalIgnoreCase)).ToList();
        if (bandcampClients.Count == 0)
        {
            throw new LidarrException("No Bandcamp download client found in Lidarr.");
        }

        var selectedClients = options.ClientId.HasValue
            ? bandcampClients.Where(node => node?["id"]?.GetValue<int>() == options.ClientId).ToList()
            : options.ClientName is not null
                ? bandcampClients.Where(node => string.Equals(node?["name"]?.GetValue<string>(), options.ClientName, StringComparison.OrdinalIgnoreCase)).ToList()
                : bandcampClients;
        if (selectedClients.Count != 1)
        {
            throw new UsageException("Choose one Bandcamp download client with --client-id <id> or --client-name <name>.");
        }

        var client = selectedClients[0]!.AsObject();
        var id = client["id"]!.GetValue<int>();
        var cookieField = client["fields"]!.AsArray().Single(field =>
            string.Equals(field?["name"]?.GetValue<string>(), "cookies", StringComparison.OrdinalIgnoreCase));
        cookieField!["value"] = cookie.StartsWith("identity=", StringComparison.OrdinalIgnoreCase) ? cookie : "identity=" + cookie;

        var body = new StringContent(client.ToJsonString(), Encoding.UTF8, "application/json");
        await SendAsync<JsonObject>(http, HttpMethod.Put, $"api/v1/downloadclient/{id}", body, cancellationToken);
        output.WriteLine($"Updated Session Cookie for '{client["name"]?.GetValue<string>()}' (id {id}).");

        body = new StringContent(client.ToJsonString(), Encoding.UTF8, "application/json");
        await SendAsync<JsonObject>(http, HttpMethod.Post, "api/v1/downloadclient/test", body, cancellationToken, isTest: true);
        output.WriteLine("Lidarr download client Test passed.");
        return 0;
    }

    private static async Task<T> SendAsync<T>(HttpClient http, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken, bool isTest = false)
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
            throw new LidarrException(isTest
                ? $"Lidarr download client Test failed with HTTP {(int)response.StatusCode}. Check the cookie and client settings."
                : $"Lidarr request failed with HTTP {(int)response.StatusCode}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(json) ? null! : JsonNode.Parse(json) as T ?? throw new InvalidDataException("Lidarr returned an unexpected JSON response.");
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class LidarrException(string message) : Exception(message);

    private sealed record Options(string Url, string ApiKey, int? ClientId, string? ClientName)
    {
        public static Options Parse(IReadOnlyList<string> args, Func<string, string?> environment)
        {
            string? url = null;
            string? apiKey = null;
            int? clientId = null;
            string? clientName = null;
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
                    default:
                        throw new UsageException("Unknown option. Use --help for usage.");
                }
            }

            if (clientId.HasValue && clientName is not null)
            {
                throw new UsageException("Use either --client-id or --client-name, not both.");
            }

            url ??= environment("LIDARR_URL");
            apiKey ??= environment("LIDARR_API_KEY");
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new UsageException("Lidarr URL is required. Use --url or LIDARR_URL.");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new UsageException("Lidarr URL must be an absolute HTTP or HTTPS URL.");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new UsageException("Lidarr API key is required. Use --api-key or LIDARR_API_KEY.");
            }

            return new Options(url, apiKey, clientId, clientName);
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
