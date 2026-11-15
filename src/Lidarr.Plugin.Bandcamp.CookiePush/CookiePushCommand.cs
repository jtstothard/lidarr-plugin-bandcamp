using System.Net.Http.Headers;
using Lidarr.Plugin.Bandcamp.Protocol;
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
        var sessionStatus = false;
        try
        {
            if (args.Count == 1 && args[0] is "--help" or "-h")
            {
                output.WriteLine("bandcamp-cookie-push --client-name <exact owner name> --fan-id <expected fan id> [--url <HTTPS Lidarr URL>] [--authelia]\nSet LIDARR_API_KEY privately. --authelia requires LIDARR_AUTHELIA_ORIGIN (exact Lidarr HTTPS origin) and LIDARR_AUTHELIA_SESSION (only the session value); LIDARR_AUTHELIA_COOKIE_NAME defaults to authelia_session. Proxy sessions stay in memory and are never sent to Bandcamp. Redirect minimal identity on stdin for manual import; otherwise a visible isolated browser opens. Complete login/CAPTCHA/2FA yourself. Ctrl-C requests cancellation before the server commit boundary. Keep the operation ID for reconciliation. Use --session-status with --client-name to read the last observed session state without login or validation. Use --renewal-status <id> or --cancel-renewal <id> with --client-name and the same server.");
                return 0;
            }

            string? owner = null; var url = environment("LIDARR_URL"); long fanId = 0; string? operationId = null; var cancelOperation = false; var authelia = false;
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] == "--session-status") { if (sessionStatus) return Usage(error); sessionStatus = true; continue; }
                if (args[i] == "--authelia") { if (authelia) return Usage(error); authelia = true; continue; }
                if (args[i] is not ("--url" or "--client-name" or "--fan-id" or "--renewal-status" or "--cancel-renewal") || i + 1 >= args.Count)
                    return Usage(error);
                var option = args[i]; var value = args[++i];
                if (option is "--renewal-status" or "--cancel-renewal")
                {
                    if (operationId != null || !Guid.TryParseExact(value, "D", out _)) return Usage(error);
                    operationId = value; cancelOperation = option == "--cancel-renewal";
                }
                if (option == "--url") url = value;
                if (option == "--client-name") owner = value;
                if (option == "--fan-id" && (!long.TryParse(value, out fanId) || fanId <= 0)) return Usage(error);
            }

            var key = environment("LIDARR_API_KEY");
            if (sessionStatus && (operationId != null || fanId > 0)) return Usage(error);
            if (string.IsNullOrWhiteSpace(owner) || (fanId <= 0 && operationId == null && !sessionStatus) || string.IsNullOrWhiteSpace(key) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return Usage(error);

            string? proxyCookie = null;
            if (authelia)
            {
                var origin = environment("LIDARR_AUTHELIA_ORIGIN");
                var session = environment("LIDARR_AUTHELIA_SESSION");
                var name = environment("LIDARR_AUTHELIA_COOKIE_NAME") ?? "authelia_session";
                // Explicit origin binding prevents accidentally reusing an inherited session on another server.
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var proxyOrigin) ||
                    proxyOrigin.Scheme != "https" || proxyOrigin.UserInfo.Length != 0 ||
                    proxyOrigin.AbsolutePath != "/" || proxyOrigin.Query.Length != 0 || proxyOrigin.Fragment.Length != 0 ||
                    proxyOrigin.GetLeftPart(UriPartial.Authority) != uri.GetLeftPart(UriPartial.Authority) ||
                    name.Length is 0 or > 128 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) ||
                    string.IsNullOrEmpty(session) || session.Length > 16384 ||
                    session.Any(c => c < 0x21 || c > 0x7e || c is '"' or ',' or ';' or '\\'))
                    return Usage(error);
                proxyCookie = name + "=" + session;
            }

            if (httpHandler is HttpClientHandler transport)
            {
                transport.AllowAutoRedirect = false;
                transport.UseCookies = false;
            }
            using var http = new HttpClient(httpHandler) { BaseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Add("X-Api-Key", key);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (proxyCookie != null) http.DefaultRequestHeaders.Add("Cookie", proxyCookie);
            var clients = (await Send(http, HttpMethod.Get, "api/v1/downloadclient", null, cancellationToken))!.AsArray();
            var owners = clients.Where(c => c?["implementation"]?.GetValue<string>() == "BandcampDownloadClient" &&
                c["name"]?.GetValue<string>() == owner).ToList();
            if (owners.Count != 1) throw new InvalidOperationException();
            var selected = owners[0]!;
            if (selected["fields"]!.AsArray().Any(f => f?["name"]?.GetValue<string>() == "authConfiguration" &&
                !string.IsNullOrWhiteSpace(f["value"]?.GetValue<string>()))) throw new InvalidOperationException();

            var metadata = new JsonObject
            {
                ["id"] = selected["id"]!.DeepClone(), ["name"] = owner,
                ["implementation"] = "BandcampDownloadClient", ["configContract"] = "BandcampDownloadSettings",
                ["fields"] = new JsonArray()
            };
            if (sessionStatus)
            {
                var status = await Send(http, HttpMethod.Post, "api/v1/downloadclient/action/sessionStatus", metadata, cancellationToken);
                return ReportSessionStatus(status, output);
            }
            if (operationId != null)
            {
                var action = cancelOperation ? "cancelIdentityRenewal" : "identityRenewalStatus";
                var receipt = await Send(http, HttpMethod.Post,
                    $"api/v1/downloadclient/action/{action}?operationId={operationId}", metadata, cancellationToken);
                return Report(receipt, output, error);
            }

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
            var begun = await Send(http, HttpMethod.Post,
                "api/v1/downloadclient/action/beginIdentityRenewal", metadata, cancellationToken);
            operationId = begun?["operationId"]?.GetValue<string>();
            if (BandcampRenewalStateWire.Parse(begun?["state"]?.GetValue<string>()) != BandcampRenewalState.Pending || !Guid.TryParseExact(operationId, "D", out _))
                throw new InvalidOperationException();
            output.WriteLine($"Renewal operation: {operationId}");
            JsonNode? result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = await Send(http, HttpMethod.Post,
                    $"api/v1/downloadclient/action/renewIdentity?fanId={fanId}&operationId={operationId}", body, cancellationToken);
            }
            catch (Exception)
            {
                // Cancelling a local HTTP wait never proves remote cancellation. Use a fresh bounded
                // channel, without resubmitting the candidate, to arbitrate cancel versus commit.
                using var reconciliation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    result = await Send(http, HttpMethod.Post,
                        $"api/v1/downloadclient/action/cancelIdentityRenewal?operationId={operationId}", metadata, reconciliation.Token);
                    if (BandcampRenewalStateWire.Parse(result?["state"]?.GetValue<string>()) is BandcampRenewalState.Committing or BandcampRenewalState.Validating or BandcampRenewalState.Pending)
                        result = await Send(http, HttpMethod.Post,
                            $"api/v1/downloadclient/action/identityRenewalStatus?operationId={operationId}", metadata, reconciliation.Token);
                }
                catch (Exception) { result = null; }
            }
            return Report(result, output, error);
        }
        catch (Exception)
        {
            if (sessionStatus)
            {
                error.WriteLine("Session status unavailable. Check owner selection and trusted HTTPS/API/proxy access. No identity was submitted.");
                return 1;
            }
            // Browser/HTTP/JSON errors may contain cookie values, request bodies or API keys.
            error.WriteLine("Renewal did not confirm success. Check owner selection, expected fan ID, HTTPS/API access and Lidarr status. If submitted, reconcile saved status before retrying.");
            return 1;
        }
    }

    private static int ReportSessionStatus(JsonNode? response, TextWriter output)
    {
        if (response?["success"]?.GetValue<bool>() != true) throw new InvalidOperationException();
        var status = response["status"]?.GetValue<string>();
        if (status is not ("Unknown" or "Valid" or "Missing" or "Invalid" or "Error" or "ChallengeOrUnrecognized" or "RateLimited"))
            throw new InvalidOperationException();
        var mode = response["effectiveAuthMode"]?.GetValue<string>() ?? "";
        const string shared = "Shared named configuration; ";
        var baseMode = mode.StartsWith(shared, StringComparison.Ordinal) ? mode[shared.Length..] : mode;
        if (baseMode is not ("Web identity (manual cookie)" or "Web identity (password fallback configured)" or "Web password fallback (no identity)" or "Not configured"))
            throw new InvalidOperationException();
        var last = response["lastSuccessfulValidationUtc"]?.GetValue<string>();
        var displayedTime = "never";
        if (last != null)
        {
            if (!DateTimeOffset.TryParse(last, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp)) throw new InvalidOperationException();
            displayedTime = timestamp.UtcDateTime.ToString("O");
        }
        // Never echo arbitrary server details or newly added fields into the terminal.
        output.WriteLine($"Session status: {status}\nAuth mode: {mode}\nLast successful validation: {displayedTime}");
        return 0;
    }

    private static int Report(JsonNode? receipt, TextWriter output, TextWriter error)
    {
        var state = BandcampRenewalStateWire.Parse(receipt?["state"]?.GetValue<string>());
        if (state == BandcampRenewalState.Committed && receipt?["success"]?.GetValue<bool>() == true)
        {
            output.WriteLine("Shared identity validated and saved. The commit boundary was crossed; cancellation cannot undo it. Search and downloads use the selected owner.");
            return 0;
        }
        if (state == BandcampRenewalState.Cancelled)
        {
            error.WriteLine("Cancellation acknowledged before commit. This operation did not replace the prior identity.");
            return 1;
        }
        var problem = receipt?["problem"]?.GetValue<string>();
        if (state == BandcampRenewalState.Refused && problem is "WrongAccount" or "ReplacementConflict" or "ConfigurationMissing" or
            "ChallengeOrUnrecognized" or "CookieExpired" or "RateLimited" or "Unavailable" or "InvalidRequest")
            error.WriteLine($"Renewal refused ({problem}). This operation did not replace the prior identity. Check Lidarr status and the recovery guide.");
        else
        {
            if (problem == "StorageFailure") error.WriteLine("StorageFailure: persistence acknowledgement failed; the saved outcome may be unknown.");
            error.WriteLine("Renewal outcome is uncertain or still pending. Cancellation is not acknowledged. Use --renewal-status with the operation ID on the same server. Do not retry the identity or roll back a newer session; check the recovery guide.");
        }
        return 1;
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("Use --help. An exact owner name, HTTPS URL and LIDARR_API_KEY are required, plus a positive fan ID for renewal, an operation ID for status/cancel, or --session-status. Secret command arguments/output and separate consumer updates are unsupported.");
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
