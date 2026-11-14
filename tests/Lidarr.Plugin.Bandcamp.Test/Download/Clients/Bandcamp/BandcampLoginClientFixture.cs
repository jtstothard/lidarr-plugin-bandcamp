using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients.Bandcamp.Session;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    /// <summary>
    /// Login client tested at the HTTP boundary (IHttpClient) with recorded Bandcamp responses.
    /// </summary>
    public class BandcampLoginClientFixture
    {
        private readonly Mock<IHttpClient> _http = new();
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private HttpRequest? _loginPost;

        private BandcampLoginClient CreateClient()
        {
            return new BandcampLoginClient(new BandcampHttpClient(_http.Object, _logger), _logger);
        }

        private void SetupLogin(string loginCbFixture, HttpStatusCode status = HttpStatusCode.OK, string? identity = "7%09fresh-identity%09%7B%22ex%22%3A0%7D")
        {
            _http.Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync((HttpRequest request) =>
                {
                    if (request.Url.FullUri.EndsWith("/login_cb"))
                    {
                        _loginPost = request;
                        var headers = new HttpHeader();
                        if (identity != null && status == HttpStatusCode.OK && loginCbFixture == "login_cb_success.json")
                        {
                            headers.Add("Set-Cookie", $"identity={identity}; domain=.bandcamp.com; path=/; expires=Sun, 14 Nov 2027 16:00:00 GMT; secure; HttpOnly");
                            headers.Add("Set-Cookie", "js_logged_in=1; domain=.bandcamp.com; path=/");
                        }

                        return new HttpResponse(request, headers, LoginFixtures.Read(loginCbFixture), status);
                    }

                    var pageHeaders = new HttpHeader();
                    pageHeaders.Add("Set-Cookie", "client_id=4E8C2B1A9F; domain=.bandcamp.com; path=/");
                    return new HttpResponse(request, pageHeaders, LoginFixtures.Read("login_page.html"), HttpStatusCode.OK);
                });
        }

        [Fact]
        public async Task Login_Success_ReturnsIdentityCookieFromResponse()
        {
            SetupLogin("login_cb_success.json");

            var result = await CreateClient().LoginAsync("fan@example.com", "hunter2");

            Assert.Equal(BandcampLoginOutcome.Success, result.Outcome);
            Assert.Equal("identity=7%09fresh-identity%09%7B%22ex%22%3A0%7D", result.Cookies);
        }

        [Fact]
        public async Task Login_PostsFormWithCredentialsAndCrumbFromLoginPage()
        {
            SetupLogin("login_cb_success.json");

            await CreateClient().LoginAsync("fan@example.com", "hunter2");

            Assert.NotNull(_loginPost);
            Assert.Equal(System.Net.Http.HttpMethod.Post, _loginPost!.Method);
            Assert.Equal("https://bandcamp.com/login_cb", _loginPost.Url.FullUri);
            var body = Encoding.UTF8.GetString(_loginPost.ContentData);
            Assert.Contains("user.name=fan%40example.com", body);
            Assert.Contains("login.password=hunter2", body);
            Assert.Contains("login.crumb=%7Clogin_cb%7C1763130000%7CZk3bQx9TqM2vYcR8uWnA1pLs0eE%3D", body);
            Assert.Contains("client_id=4E8C2B1A9F", _loginPost.Headers.GetSingleValue("Cookie"));
        }

        [Fact]
        public async Task Login_SendsCsrfTokenFromLoginPageMeta()
        {
            SetupLogin("login_cb_success.json");

            await CreateClient().LoginAsync("fan@example.com", "hunter2");

            Assert.Equal("Rj8kQ2mVx7LpN4cT9bWz", _loginPost!.Headers.GetSingleValue("X-CSRF-TOKEN"));
        }

        [Theory]
        [InlineData("login_cb_bad_credentials.json", BandcampLoginOutcome.BadCredentials)]
        [InlineData("login_cb_verification_required.json", BandcampLoginOutcome.VerificationRequired)]
        [InlineData("login_cb_captcha_required.json", BandcampLoginOutcome.CaptchaRequired)]
        public async Task Login_RejectedResponse_ReturnsTypedOutcomeWithoutCookie(string fixture, BandcampLoginOutcome expected)
        {
            SetupLogin(fixture);

            var result = await CreateClient().LoginAsync("fan@example.com", "wrong");

            Assert.Equal(expected, result.Outcome);
            Assert.Null(result.Cookies);
        }

        [Fact]
        public async Task Login_ServerError_ReturnsError()
        {
            SetupLogin("login_cb_bad_credentials.json", HttpStatusCode.InternalServerError);

            var result = await CreateClient().LoginAsync("fan@example.com", "hunter2");

            Assert.Equal(BandcampLoginOutcome.Error, result.Outcome);
        }

        [Fact]
        public async Task Login_OkWithoutIdentityCookie_ReturnsError()
        {
            SetupLogin("login_cb_success.json", identity: null);

            var result = await CreateClient().LoginAsync("fan@example.com", "hunter2");

            Assert.Equal(BandcampLoginOutcome.Error, result.Outcome);
            Assert.Null(result.Cookies);
        }

        [Fact]
        public async Task Login_RequestThrows_ReturnsGenericMessageWithoutExceptionText()
        {
            _http.Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ThrowsAsync(new System.Exception("connect failed for fan@example.com:hunter2"));

            var result = await CreateClient().LoginAsync("fan@example.com", "hunter2");

            Assert.Equal(BandcampLoginOutcome.Error, result.Outcome);
            Assert.DoesNotContain("hunter2", result.Message ?? string.Empty);
            Assert.DoesNotContain("connect failed", result.Message ?? string.Empty);
        }
    }

    internal static class LoginFixtures
    {
        public static string Read(string name)
        {
            var dir = Path.Combine(
                Path.GetDirectoryName(typeof(LoginFixtures).Assembly.Location)!,
                "Download", "Clients", "Bandcamp", "LoginFixtureData");
            return File.ReadAllText(Path.Combine(dir, name));
        }
    }
}
