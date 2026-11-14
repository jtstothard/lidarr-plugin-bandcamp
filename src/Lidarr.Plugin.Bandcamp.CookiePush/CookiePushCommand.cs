using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Lidarr.Plugin.Bandcamp.CookiePush;

// Transport adapter for the existing shared session transaction, never a login engine.
public static class CookiePushCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextReader input,
        TextWriter output, TextWriter error, Func<string, string?> environment,
        HttpMessageHandler httpHandler, IBrowserCookieCapture? browserCookieCapture = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Count == 1 && args[0] is "--help" or "-h")
            {
                output.WriteLine("bandcamp-cookie-push --client-name <exact owner name> --fan-id <expected fan id> [--url <HTTPS Lidarr URL>]\nSet LIDARR_API_KEY privately. Redirect minimal identity on stdin for manual import; otherwise a visible isolated browser opens. Complete login/CAPTCHA/2FA yourself. Ctrl-C cancels before handoff; once submitted, reconcile status in Lidarr.");
                return 0;
            }

            string? owner = null; var url = environment("LIDARR_URL"); long fanId = 0;
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] is not ("--url" or "--client-name" or "--fan-id") || i + 1 >= args.Count)
                    return Usage(error);
                var option = args[i]; var value = args[++i];
                if (option == "--url") url = value;
                if (option == "--client-name") owner = value;
                if (option == "--fan-id" && (!long.TryParse(value, out fanId) || fanId <= 0)) return Usage(error);
            }

            var key = environment("LIDARR_API_KEY");
            if (string.IsNullOrWhiteSpace(owner) || fanId <= 0 || string.IsNullOrWhiteSpace(key) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return Usage(error);

            if (httpHandler is HttpClientHandler transport)
            {
                transport.AllowAutoRedirect = false;
                transport.UseCookies = false;
            }
            using var http = new HttpClient(httpHandler) { BaseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/") };
            http.DefaultRequestHeaders.Add("X-Api-Key", key);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var clients = (await Send(http, HttpMethod.Get, "api/v1/downloadclient", null, cancellationToken))!.AsArray();
            var owners = clients.Where(c => c?["implementation"]?.GetValue<string>() == "BandcampDownloadClient" &&
                c["name"]?.GetValue<string>() == owner).ToList();
            if (owners.Count != 1) throw new InvalidOperationException();
            var selected = owners[0]!;
            if (selected["fields"]!.AsArray().Any(f => f?["name"]?.GetValue<string>() == "authConfiguration" &&
                !string.IsNullOrWhiteSpace(f["value"]?.GetValue<string>()))) throw new InvalidOperationException();

            var cookie = (await input.ReadToEndAsync(cancellationToken)).Trim();
            if (cookie.Length == 0)
            {
                if (browserCookieCapture == null) return Usage(error);
                output.WriteLine("Opening isolated Bandcamp login. Complete login and verification personally.");
                cookie = await browserCookieCapture.CaptureAsync(cancellationToken);
            }
            if (cookie.StartsWith("identity=", StringComparison.Ordinal)) cookie = cookie[9..];
            if (cookie.Length == 0 || cookie.Length > 16384 || cookie.Any(c => c <= 32 || c >= 127 || c is ';' or ','))
                throw new InvalidOperationException();
            cancellationToken.ThrowIfCancellationRequested();
            // Never resend secrets or unrelated provider settings returned by the host.
            var body = new JsonObject
            {
                ["id"] = selected["id"]!.DeepClone(), ["name"] = owner,
                ["implementation"] = "BandcampDownloadClient", ["configContract"] = "BandcampDownloadSettings",
                ["fields"] = new JsonArray(new JsonObject { ["name"] = "cookies", ["value"] = "identity=" + cookie })
            };
            var result = await Send(http, HttpMethod.Post,
                $"api/v1/downloadclient/action/renewIdentity?fanId={fanId}", body, cancellationToken);
            if (result?["success"]?.GetValue<bool>() != true)
            {
                var problem = result?["problem"]?.GetValue<string>();
                if (problem is "StorageFailure" or "WrongAccount" or "ReplacementConflict" or "ConfigurationMissing" or
                    "ChallengeOrUnrecognized" or "CookieExpired" or "RateLimited" or "Unavailable" or "InvalidRequest")
                {
                    error.WriteLine($"Renewal refused ({problem}). The prior identity was retained. Check Lidarr status and the recovery guide.");
                    return 1;
                }
                throw new InvalidOperationException();
            }
            output.WriteLine("Shared identity validated and saved. Search and downloads use the selected owner.");
            return 0;
        }
        catch (Exception)
        {
            // Browser/HTTP/JSON errors may contain cookie values, request bodies or API keys.
            error.WriteLine("Renewal did not confirm success. Check owner selection, expected fan ID, HTTPS/API access and Lidarr status. If submitted, reconcile saved status before retrying.");
            return 1;
        }
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("Use --help. An exact owner name, positive expected fan ID, HTTPS URL and LIDARR_API_KEY are required. Secret command arguments/output and separate consumer updates are unsupported.");
        return 2;
    }

    private static async Task<JsonNode?> Send(HttpClient http, HttpMethod method, string path, JsonObject? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
    }
}
