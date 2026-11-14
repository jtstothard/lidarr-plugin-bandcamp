using System.Net;
using Xunit;
namespace Lidarr.Plugin.Bandcamp.CookiePush.Test;
public class RenewalWorkflowFixture
{
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
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.Equal("https://lidarr.example/base/api/v1/downloadclient/action/renewIdentity?fanId=12345678", handler.Requests[1].Url);
        Assert.Contains("sentinel-candidate", handler.Requests[1].Body);
        Assert.DoesNotContain("prior-secret", handler.Requests[1].Body);
        Assert.DoesNotContain("sentinel", output.ToString() + error);
    }
    [Fact]
    public async Task Storage_refusal_reports_only_known_status_never_server_secret_details()
    {
        var output = new StringWriter(); var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(["--client-name", "Personal", "--fan-id", "12345678"],
            new StringReader("sentinel-candidate"), output, error,
            k => k == "LIDARR_URL" ? "https://lidarr.example" : "sentinel-key",
            new Handoff("""{"success":false,"problem":"StorageFailure","detail":"sentinel-candidate"}"""));
        Assert.Equal(1, result);
        Assert.Contains("StorageFailure", error.ToString());
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
    private sealed class Handoff(string result = "{\"success\":true}") : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Requests.Count == 1
                ? """[{"id":7,"name":"Personal","implementation":"BandcampDownloadClient","configContract":"BandcampDownloadSettings","fields":[{"name":"cookies","value":"prior-secret"},{"name":"authConfiguration","value":""}]}]"""
                : result) };
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
