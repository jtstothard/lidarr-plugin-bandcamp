using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.CookiePush.Test;

public sealed class BrowserCaptureCommandFixture
{
    private const string Cookie = "browser-cookie-that-must-stay-secret";
    private const string ApiKey = "api-key-that-must-stay-secret";

    [Fact]
    public async Task Browser_failure_is_sanitized_at_command_boundary()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(["--print-cookie"], TextReader.Null,
            output, error, _ => null, new MockLidarrApi(), new FailedBrowser());
        Assert.Equal(1, result);
        Assert.Contains("Browser login failed", error.ToString());
        Assert.DoesNotContain(" at ", error.ToString());
        AssertSecretSafe(output, error);
    }

    private sealed class FailedBrowser : IBrowserCookieCapture
    {
        public Task<string> CaptureAsync(CancellationToken cancellationToken) =>
            throw new Microsoft.Playwright.PlaywrightException(Cookie + ApiKey);
    }

    [Fact]
    public async Task Cancellation_returns_130_without_secrets_or_stack_trace()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = new StringWriter();
        var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(["--print-cookie"], new StringReader(Cookie),
            output, error, _ => null, new MockLidarrApi(), cancellationToken: cancellation.Token);
        Assert.Equal(130, result);
        Assert.Contains("Cancelled", error.ToString());
        Assert.DoesNotContain(" at ", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task No_cookie_input_captures_in_browser_then_uses_existing_push_and_test_path()
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, "[" + Client() + "]");
        lidarr.Enqueue(HttpMethod.Put, "/api/v1/downloadclient/7", HttpStatusCode.Accepted, Client());
        lidarr.Enqueue(HttpMethod.Post, "/api/v1/downloadclient/test", HttpStatusCode.OK, "{}");
        var browser = new FakeBrowserCookieCapture(Cookie);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CookiePushCommand.RunAsync(
            ["--url", "http://lidarr.test:8686", "--api-key", ApiKey],
            new StringReader(string.Empty),
            output,
            error,
            _ => null,
            lidarr,
            browser);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, browser.CaptureCount);
        Assert.Equal("identity=" + Cookie, lidarr.Requests[1].Body?["fields"]?[0]?["value"]?.GetValue<string>());
        Assert.Contains("Log in to Bandcamp", output.ToString());
        Assert.Contains("Test passed", output.ToString());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task Closing_login_window_before_login_is_clear_and_changes_nothing()
    {
        var lidarr = new MockLidarrApi();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CookiePushCommand.RunAsync(
            ["--url", "http://lidarr.test:8686", "--api-key", ApiKey],
            new StringReader(string.Empty),
            output,
            error,
            _ => null,
            lidarr,
            new ThrowingBrowserCookieCapture());

        Assert.Equal(1, exitCode);
        Assert.Empty(lidarr.Requests);
        Assert.Contains("window closed", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No changes were made", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task Print_cookie_outputs_identity_cookie_without_lidarr_settings_or_requests()
    {
        var lidarr = new MockLidarrApi();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CookiePushCommand.RunAsync(
            ["--print-cookie"],
            new StringReader(string.Empty),
            output,
            error,
            _ => null,
            lidarr,
            new FakeBrowserCookieCapture(Cookie));

        Assert.Equal(0, exitCode);
        Assert.Empty(lidarr.Requests);
        Assert.Contains("identity=" + Cookie, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Dry_run_reports_selected_client_without_updating_or_testing()
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, "[" + Client() + "]");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CookiePushCommand.RunAsync(
            ["--url", "http://lidarr.test:8686", "--api-key", ApiKey, "--dry-run"],
            new StringReader(string.Empty),
            output,
            error,
            _ => null,
            lidarr,
            new FakeBrowserCookieCapture(Cookie));

        Assert.Equal(0, exitCode);
        Assert.Single(lidarr.Requests);
        Assert.Contains("Dry run", output.ToString());
        Assert.Contains("would update Session Cookie", output.ToString());
        Assert.Contains("Bandcamp", output.ToString());
        Assert.Contains("No changes were made", output.ToString());
        AssertSecretSafe(output, error);
    }

    private static void AssertSecretSafe(StringWriter output, StringWriter error)
    {
        Assert.DoesNotContain(Cookie, output.ToString());
        Assert.DoesNotContain(Cookie, error.ToString());
        Assert.DoesNotContain(ApiKey, output.ToString());
        Assert.DoesNotContain(ApiKey, error.ToString());
    }

    private static string Client() => """
        {
          "id": 7,
          "name": "Bandcamp",
          "implementation": "BandcampDownloadClient",
          "fields": [
            { "name": "cookies", "value": "identity=old" }
          ]
        }
        """;

    private sealed class FakeBrowserCookieCapture(string cookie) : IBrowserCookieCapture
    {
        public int CaptureCount { get; private set; }

        public Task<string> CaptureAsync(CancellationToken cancellationToken)
        {
            CaptureCount++;
            return Task.FromResult(cookie);
        }
    }

    private sealed class ThrowingBrowserCookieCapture : IBrowserCookieCapture
    {
        public Task<string> CaptureAsync(CancellationToken cancellationToken)
        {
            throw new BrowserWindowClosedException("The login window closed before Bandcamp login completed. No changes were made.");
        }
    }

    private sealed class MockLidarrApi : HttpMessageHandler
    {
        private readonly Queue<ExpectedRequest> _expected = new();

        public List<RecordedRequest> Requests { get; } = new();

        public void Enqueue(HttpMethod method, string path, HttpStatusCode status, string body)
        {
            _expected.Enqueue(new ExpectedRequest(method, path, status, body));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotEmpty(_expected);
            var expected = _expected.Dequeue();
            Assert.Equal(expected.Method, request.Method);
            Assert.Equal(expected.Path, request.RequestUri?.AbsolutePath);
            var body = request.Content is null
                ? null
                : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?.AsObject();
            Requests.Add(new RecordedRequest(body));
            return new HttpResponseMessage(expected.Status)
            {
                Content = new StringContent(expected.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record ExpectedRequest(HttpMethod Method, string Path, HttpStatusCode Status, string Body);

    private sealed record RecordedRequest(JsonObject? Body);
}
