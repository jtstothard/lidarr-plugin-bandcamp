using System.Net;
using Xunit;
namespace Lidarr.Plugin.Bandcamp.CookiePush.Test;
public class RenewalWorkflowFixture
{
    [Theory]
    [InlineData("Valid", "2026-11-15T12:00:00Z", "2026-11-15")]
    [InlineData("Unknown", null, "never")]
    public async Task Session_status_reports_only_safe_metadata_without_reading_identity_or_opening_browser(string status, string? last, string expectedTime)
    {
        var response = System.Text.Json.JsonSerializer.Serialize(new { success = true, status,
            effectiveAuthMode = "Shared named configuration; Web identity (manual cookie)", lastSuccessfulValidationUtc = last,
            cookies = "sentinel-candidate", detail = "prior-secret" });
        var handler = new Handoff(response, sessionStatus: true);var output = new StringWriter();var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(["--client-name", "Personal", "--session-status"],
            new RefusesRead(), output, error, key => key == "LIDARR_URL" ? "https://lidarr.example" : "sentinel-key", handler);
        Assert.Equal(0, result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/api/v1/downloadclient/action/sessionStatus", handler.Requests[1].Url);
        Assert.DoesNotContain("prior-secret", handler.Requests[1].Body);
        Assert.Contains(status, output.ToString());Assert.Contains("Web identity", output.ToString());Assert.Contains(expectedTime, output.ToString());
        Assert.DoesNotContain("sentinel", output.ToString() + error);Assert.DoesNotContain("prior-secret", output.ToString() + error);
    }

    [Fact]
    public async Task Session_status_refuses_unknown_metadata_without_echoing_it()
    {
        var output = new StringWriter();var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(["--client-name", "Personal", "--session-status"], TextReader.Null,
            output, error, key => key == "LIDARR_URL" ? "https://lidarr.example" : "sentinel-key",
            new Handoff("""{"success":true,"status":"Valid","effectiveAuthMode":"sentinel-private","lastSuccessfulValidationUtc":null}""", sessionStatus: true));
        Assert.Equal(1, result);Assert.DoesNotContain("sentinel", output.ToString() + error);
    }

    private sealed class RefusesRead : TextReader
    {
        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Must not read candidate for status");
    }

    [Theory]
    [InlineData("Pending", 1, "uncertain")]
    [InlineData("Validating", 1, "uncertain")]
    [InlineData("Committing", 1, "uncertain")]
    [InlineData("Committed", 0, "validated and saved")]
    [InlineData("Cancelled", 1, "Cancellation acknowledged")]
    [InlineData("Refused", 1, "Renewal refused")]
    [InlineData("Unknown", 1, "uncertain")]
    [InlineData("committed", 1, "uncertain")]
    [InlineData("3", 1, "uncertain")]
    [InlineData("FutureState", 1, "uncertain")]
    public async Task Receipt_wire_states_preserve_existing_meanings_and_unknown_values_never_confirm_success(string state, int expectedExit, string message)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var receipt = System.Text.Json.JsonSerializer.Serialize(new { state, success = true, problem = "WrongAccount" });
        var result = await CookiePushCommand.RunAsync(["--client-name", "Personal", "--fan-id", "12345678"],
            new StringReader("identity=sentinel-candidate"), output, error,
            key => key == "LIDARR_URL" ? "https://lidarr.example" : "sentinel-api-key", new Handoff(receipt));
        Assert.Equal(expectedExit, result);
        Assert.Contains(message, output.ToString() + error);
        Assert.DoesNotContain("sentinel", output.ToString() + error);
    }

    [Fact]
    public async Task Secret_print_option_is_rejected_without_output_or_requests()
    {
        var output = new StringWriter(); var error = new StringWriter();
        var handler = new Handler();
        var result = await CookiePushCommand.RunAsync(["--print-cookie"], new StringReader("sentinel-identity"), output, error, _ => null, handler);
        Assert.Equal(2, result);
        Assert.DoesNotContain("sentinel", output.ToString() + error);
        Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task Manual_import_sends_only_candidate_to_named_owner_transaction_never_puts_settings()
    {
        var output = new StringWriter(); var error = new StringWriter(); var handler = new Handoff();
        var result = await CookiePushCommand.RunAsync(["--client-name", "Personal", "--fan-id", "12345678"],
            new StringReader("identity=sentinel-candidate"), output, error,
            key => key == "LIDARR_URL" ? "https://lidarr.example/base" : "sentinel-api-key", handler);
        Assert.Equal(0, result);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("POST", handler.Requests[2].Method);
        Assert.StartsWith("https://lidarr.example/base/api/v1/downloadclient/action/renewIdentity?fanId=12345678&operationId=", handler.Requests[2].Url);
        Assert.DoesNotContain("sentinel-candidate", handler.Requests[1].Body);
        Assert.Contains("sentinel-candidate", handler.Requests[2].Body);
        Assert.DoesNotContain("prior-secret", handler.Requests[2].Body);
        Assert.DoesNotContain("sentinel", output.ToString() + error);
    }
    [Fact]
    public async Task Storage_uncertainty_reports_only_known_status_never_server_secret_details()
    {
        var output = new StringWriter(); var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(["--client-name", "Personal", "--fan-id", "12345678"],
            new StringReader("sentinel-candidate"), output, error,
            k => k == "LIDARR_URL" ? "https://lidarr.example" : "sentinel-key",
            new Handoff("""{"success":false,"state":"Unknown","problem":"StorageFailure","detail":"sentinel-candidate"}"""));
        Assert.Equal(1, result);
        Assert.Contains("StorageFailure", error.ToString());
        Assert.Contains("unknown", error.ToString());
        Assert.DoesNotContain("prior identity was retained", error.ToString());
        Assert.DoesNotContain("sentinel", output.ToString() + error);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_or_closed_browser_never_submits_a_replacement_or_reveals_exception(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handoff(); var output = new StringWriter(); var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(["--client-name", "Personal", "--fan-id", "12345678"],
            TextReader.Null, output, error, k => k == "LIDARR_URL" ? "https://lidarr.example" : "sentinel-key",
            handler, new ClosedBrowser(cancel, cancellation), cancellation.Token);
        Assert.Equal(1, result);
        Assert.Single(handler.Requests);
        var text = output.ToString() + error;
        Assert.DoesNotContain("sentinel", text);
        Assert.DoesNotContain(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("sentinel-key"))), text);
    }
    private sealed class ClosedBrowser(bool cancel, CancellationTokenSource cancellation) : IBrowserCookieCapture
    {
        public Task<string> CaptureAsync(CancellationToken token)
        {
            if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new BrowserWindowClosedException("sentinel-key sentinel-identity");
        }
    }
    private sealed class Handoff(string result = "{\"success\":true,\"state\":\"Committed\"}", bool sessionStatus = false) : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Requests.Count == 1
                ? """[{"id":7,"name":"Personal","implementation":"BandcampDownloadClient","configContract":"BandcampDownloadSettings","fields":[{"name":"cookies","value":"prior-secret"},{"name":"authConfiguration","value":""}]}]"""
                : Requests.Count == 2 && !sessionStatus ? """{"success":false,"state":"Pending","operationId":"00000000-0000-0000-0000-000000000051"}""" : result) };
        }
    }
    [Fact]
    public async Task Real_http_transport_disables_redirect_and_cookie_forwarding_before_use()
    {
        var handler = new HttpClientHandler(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await CookiePushCommand.RunAsync(["--client-name", "Personal", "--fan-id", "1"], TextReader.Null,
            TextWriter.Null, TextWriter.Null, key => key == "LIDARR_URL" ? "https://lidarr.invalid" : "key",
            handler, cancellationToken: cancelled.Token);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }
    internal sealed class Handler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request.RequestUri!.ToString()); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
