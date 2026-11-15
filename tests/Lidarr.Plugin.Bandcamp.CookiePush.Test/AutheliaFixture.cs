using System.Net;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.CookiePush.Test;

public class AutheliaFixture
{
    [Fact]
    public async Task Authelia_keeps_real_transport_redirects_and_response_cookies_disabled()
    {
        var handler = new HttpClientHandler(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await CookiePushCommand.RunAsync(["--authelia", "--client-name", "Personal", "--fan-id", "123"],
            TextReader.Null, TextWriter.Null, TextWriter.Null, Environment, handler, cancellationToken: cancelled.Token);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task Explicit_session_authenticates_status_without_submitting_an_identity()
    {
        var handler = new Proxy();
        var output = new StringWriter(); var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(
            ["--authelia", "--client-name", "Personal", "--renewal-status", "00000000-0000-0000-0000-000000000051"],
            TextReader.Null, output, error, Environment, handler);
        Assert.Equal(1, result); // Acknowledged historical cancellation, not a replacement.
        Assert.Equal(2, handler.Calls);
        Assert.Contains("Cancellation acknowledged", error.ToString());
        Assert.DoesNotContain("sentinel", output.ToString() + error);
    }

    private static string? Environment(string key) => key switch
    {
        "LIDARR_URL" => "https://lidarr.example/base",
        "LIDARR_API_KEY" => "sentinel-key",
        "LIDARR_AUTHELIA_ORIGIN" => "https://lidarr.example",
        "LIDARR_AUTHELIA_SESSION" => "sentinel-proxy-session",
        _ => null
    };

    [Theory]
    [InlineData("LIDARR_AUTHELIA_ORIGIN", "https://other.example")]
    [InlineData("LIDARR_AUTHELIA_ORIGIN", "http://lidarr.example")]
    [InlineData("LIDARR_AUTHELIA_ORIGIN", "https://lidarr.example:444")]
    [InlineData("LIDARR_AUTHELIA_ORIGIN", "https://user@lidarr.example")]
    [InlineData("LIDARR_AUTHELIA_ORIGIN", "https://lidarr.example/path")]
    [InlineData("LIDARR_AUTHELIA_ORIGIN", "https://lidarr.example?key=sentinel")]
    [InlineData("LIDARR_AUTHELIA_ORIGIN", null)]
    [InlineData("LIDARR_AUTHELIA_SESSION", null)]
    [InlineData("LIDARR_AUTHELIA_SESSION", "")]
    [InlineData("LIDARR_AUTHELIA_SESSION", "sentinel; identity=other")]
    [InlineData("LIDARR_AUTHELIA_SESSION", "sentinel\r\nX-Test: bad")]
    [InlineData("LIDARR_AUTHELIA_SESSION", "sentinel,other")]
    [InlineData("LIDARR_AUTHELIA_COOKIE_NAME", "identity;other")]
    [InlineData("LIDARR_AUTHELIA_COOKIE_NAME", "")]
    public async Task Invalid_proxy_configuration_is_refused_before_any_request(string field, string? value)
    {
        var handler = new Proxy(); var output = new StringWriter(); var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(["--authelia", "--client-name", "Personal", "--fan-id", "123"],
            new StringReader("sentinel-candidate"), output, error, k => k == field ? value : Environment(k), handler);
        Assert.Equal(2, result);
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain("sentinel", output.ToString() + error);
    }

    [Fact]
    public async Task Session_environment_is_ignored_without_explicit_opt_in()
    {
        var handler = new NoCookie();
        await CookiePushCommand.RunAsync(["--client-name", "Personal", "--fan-id", "123"],
            TextReader.Null, TextWriter.Null, TextWriter.Null, Environment, handler);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class NoCookie : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }

    [Fact]
    public async Task Lost_submission_response_uses_same_proxy_session_to_cancel_without_candidate_replay()
    {
        var handler = new LostResponse(); var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(["--authelia", "--client-name", "Personal", "--fan-id", "123"],
            new StringReader("sentinel-candidate"), TextWriter.Null, error, Environment, handler);
        Assert.Equal(1, result);
        Assert.Equal(4, handler.Calls);
        Assert.Contains("Cancellation acknowledged", error.ToString());
        Assert.DoesNotContain("sentinel", error.ToString());
    }

    private sealed class LostResponse : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal("authelia_session=sentinel-proxy-session", Assert.Single(request.Headers.GetValues("Cookie")));
            if (Calls == 3) throw new HttpRequestException("sentinel-response-lost");
            if (Calls == 4)
            {
                Assert.Contains("/cancelIdentityRenewal?operationId=", request.RequestUri!.AbsoluteUri);
                Assert.DoesNotContain("sentinel-candidate", await request.Content!.ReadAsStringAsync(token));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Calls == 1
                ? """[{"id":7,"name":"Personal","implementation":"BandcampDownloadClient","fields":[]}]"""
                : Calls == 2 ? """{"state":"Pending","operationId":"00000000-0000-0000-0000-000000000051"}"""
                : """{"state":"Cancelled"}""") };
        }
    }

    [Fact]
    public async Task Proxy_redirect_is_refused_without_forwarding_secrets_or_opening_browser()
    {
        var handler = new Redirect(); var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(["--authelia", "--client-name", "Personal", "--fan-id", "123"],
            TextReader.Null, TextWriter.Null, error, Environment, handler);
        Assert.Equal(1, result);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("sentinel", error.ToString());
    }

    private sealed class Redirect : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://other.example/sentinel");
            return Task.FromResult(response);
        }
    }

    private sealed class Proxy : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal("https://lidarr.example", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Equal("authelia_session=sentinel-proxy-session", Assert.Single(request.Headers.GetValues("Cookie")));
            Assert.Equal("sentinel-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Calls == 1
                ? """[{"id":7,"name":"Personal","implementation":"BandcampDownloadClient","fields":[{"name":"authConfiguration","value":""}]}]"""
                : """{"state":"Cancelled"}""") });
        }
    }
}
