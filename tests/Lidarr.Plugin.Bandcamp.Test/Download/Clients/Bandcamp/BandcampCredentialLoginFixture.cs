using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Download.Clients.Bandcamp.Session;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    /// <summary>
    /// Credential login driven through BandcampDownloadClient's public surface, with Bandcamp
    /// simulated at the IHttpClient boundary from recorded fixtures.
    /// </summary>
    public class BandcampCredentialLoginFixture
    {
        private const string EmailedCode = "482913";
        private const string FreshIdentity = "7%09fresh-identity%09%7B%22ex%22%3A0%7D";

        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly Mock<IHttpClient> _http = new();
        private readonly Mock<IDownloadClientRepository> _repository = new();
        private readonly ConcurrentQueue<string> _homepageCookies = new();
        private int _loginPosts;
        private string? _homepageOverride;
        private bool _transportFailure;
        private HttpStatusCode _homepageStatus = HttpStatusCode.OK;
        private string? _retryAfter;
        private HttpStatusCode _loginStatus = HttpStatusCode.OK;
        private HttpStatusCode _loginPageStatus = HttpStatusCode.OK;
        private string _loginCbFixture = "login_cb_success.json";
        private Func<Task>? _homepageDelay;
        private Func<Task>? _loginDelay;
        private readonly ConcurrentQueue<string> _loginBodies = new();
        private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        public BandcampCredentialLoginFixture()
        {
            _http.Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .Returns(async (HttpRequest request) =>
                {
                    var url = request.Url.FullUri;
                    if (url == "https://bandcamp.com")
                    {
                        if (_transportFailure)
                        {
                            throw new System.Net.Http.HttpRequestException("simulated transport failure");
                        }
                        var cookie = request.Headers.GetSingleValue("Cookie") ?? string.Empty;
                        _homepageCookies.Enqueue(cookie);
                        if (_homepageDelay != null)
                        {
                            await _homepageDelay();
                        }

                        var page = cookie.Contains("fresh-identity") ? "homepage_logged_in.html" : "homepage_logged_out.html";
                        var headers = new HttpHeader();
                        if (_retryAfter != null)
                        {
                            headers.Add("Retry-After", _retryAfter);
                        }
                        return new HttpResponse(request, headers, _homepageOverride ?? LoginFixtures.Read(page), _homepageStatus);
                    }

                    if (url == "https://bandcamp.com/login_cb")
                    {
                        Interlocked.Increment(ref _loginPosts);
                        var body = request.ContentData == null ? string.Empty : System.Text.Encoding.UTF8.GetString(request.ContentData);
                        _loginBodies.Enqueue(body);
                        if (_loginDelay != null)
                        {
                            await _loginDelay();
                        }

                        // Bandcamp accepts the login once the emailed code is supplied.
                        var fixture = body.Contains("login.twofactor=" + EmailedCode) ? "login_cb_success.json" : _loginCbFixture;
                        if (body.Contains("password=wrong-password"))
                        {
                            fixture = "login_cb_bad_credentials.json";
                        }

                        var headers = new HttpHeader();
                        if (_retryAfter != null)
                        {
                            headers.Add("Retry-After", _retryAfter);
                        }
                        if (fixture == "login_cb_success.json")
                        {
                            headers.Add("Set-Cookie", $"identity={FreshIdentity}; domain=.bandcamp.com; path=/; secure; HttpOnly");
                        }

                        return new HttpResponse(request, headers, LoginFixtures.Read(fixture), _loginStatus);
                    }

                    if (url == "https://bandcamp.com/login")
                    {
                        var headers = new HttpHeader();
                        if (_retryAfter != null)
                        {
                            headers.Add("Retry-After", _retryAfter);
                        }
                        return new HttpResponse(request, headers, LoginFixtures.Read("login_page.html"), _loginPageStatus);
                    }

                    return new HttpResponse(request, new HttpHeader(), "{}", HttpStatusCode.OK);
                });
        }

        private BandcampDownloadClient CreateClient(BandcampDownloadSettings settings, IBandcampSessionProviderRegistry? registry = null)
        {
            var bandcampHttp = new BandcampHttpClient(_http.Object, _logger);
            var apiClient = new BandcampApiClient(bandcampHttp, _logger);
            registry ??= CreateRegistry();

            var client = new BandcampDownloadClient(
                Mock.Of<NzbDrone.Core.Download.Clients.Bandcamp.IBandcampDownloadQueue>(),
                registry,
                Mock.Of<IAlbumService>(),
                Mock.Of<IReleaseService>(),
                Mock.Of<ITrackService>(),
                Mock.Of<IConfigService>(),
                Mock.Of<IDiskProvider>(d => d.FolderExists(It.IsAny<string>()) == true && d.FolderWritable(It.IsAny<string>()) == true),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ILocalizationService>(),
                _logger);

            var definition = new DownloadClientDefinition { Id = 7, Name = "Bandcamp", Settings = settings };
            client.Definition = definition;
            _repository.Setup(r => r.Find(7)).Returns(definition);
            return client;
        }

        private BandcampSessionProviderRegistry CreateRegistry()
        {
            var bandcampHttp = new BandcampHttpClient(_http.Object, _logger);
            return new BandcampSessionProviderRegistry(
                new BandcampApiClient(bandcampHttp, _logger),
                new BandcampLoginClient(bandcampHttp, _logger),
                new BandcampSessionStore(_repository.Object),
                _logger,
                () => _now);
        }

        private static BandcampDownloadSettings Settings(string cookies = "", string email = "fan@example.com", string password = "hunter2", string verificationCode = "")
        {
            return new BandcampDownloadSettings
            {
                Cookies = cookies,
                Email = email,
                Password = password,
                VerificationCode = verificationCode,
                DownloadPath = "/tmp/bandcamp"
            };
        }

        [Fact]
        public async Task RateLimit_AfterWait_RevalidatesBeforeReusingAnEarlierCachedSession()
        {
            _homepageOverride = LoginFixtures.Read("homepage_logged_in.html");
            var registry = CreateRegistry();
            var client = CreateClient(Settings(cookies: "identity=prior"), registry);
            Assert.True(client.Test().IsValid);
            _homepageStatus = HttpStatusCode.TooManyRequests;
            _retryAfter = "120";
            Assert.False(client.Test().IsValid);
            var provider = registry.Find(7)!;
            Assert.Null(await provider.GetSessionAsync());
            _now = _now.AddSeconds(120);
            _homepageStatus = HttpStatusCode.OK;
            Assert.NotNull(await provider.GetSessionAsync());
            Assert.Equal(BandcampSessionStatus.Valid, provider.State.Status);
            Assert.Equal(3, _homepageCookies.Count);
        }

        [Theory]
        [InlineData("homepage_logged_in.html", 200, null, 0, true)]
        [InlineData("homepage_logged_out.html", 200, null, 1, true)]
        [InlineData("homepage_logged_out.html", 401, null, 1, true)]
        [InlineData("homepage_logged_out.html", 403, null, 1, true)]
        [InlineData("homepage_challenge.html", 200, "ChallengeOrUnrecognized", 0, false)]
        [InlineData("homepage_challenge.html", 401, "ChallengeOrUnrecognized", 0, false)]
        [InlineData("homepage_challenge.html", 403, "ChallengeOrUnrecognized", 0, false)]
        [InlineData("homepage_redesigned.html", 200, "ChallengeOrUnrecognized", 0, false)]
        [InlineData("homepage_empty.html", 200, "ChallengeOrUnrecognized", 0, false)]
        [InlineData("homepage_malformed.html", 200, "ChallengeOrUnrecognized", 0, false)]
        [InlineData("homepage_logged_in.html", 403, "ChallengeOrUnrecognized", 0, false)]
        [InlineData("homepage_logged_out.html", 503, "Unavailable", 0, false)]
        public void HomepageEvidence_DeterminesWhetherPasswordLoginIsAllowed(
            string fixture, int status, string? problem, int expectedLogins, bool valid)
        {
            _homepageOverride = LoginFixtures.Read(fixture);
            _homepageStatus = (HttpStatusCode)status;
            // A successful password login is followed by a normal homepage validation.
            _loginDelay = () =>
            {
                _homepageOverride = LoginFixtures.Read("homepage_logged_in.html");
                _homepageStatus = HttpStatusCode.OK;
                return Task.CompletedTask;
            };
            var registry = CreateRegistry();
            var settings = Settings(cookies: "identity=prior");
            var result = CreateClient(settings, registry).Test();
            Assert.Equal(valid, result.IsValid);
            Assert.Equal(expectedLogins, _loginPosts);
            Assert.Equal(problem, registry.Find(7)!.State.Problem?.Code);
            if (expectedLogins == 0)
            {
                Assert.Equal("identity=prior", settings.Cookies);
            }
        }

        [Theory]
        [InlineData(200, "ChallengeOrUnrecognized")]
        [InlineData(429, "RateLimited")]
        [InlineData(503, "Unavailable")]
        public async Task Download_PreGrabRefusalNamesValidationCauseWithoutLogin(int status, string problem)
        {
            _homepageOverride = LoginFixtures.Read("homepage_challenge.html");
            _homepageStatus = (HttpStatusCode)status;
            var registry = CreateRegistry();
            var client = CreateClient(Settings(cookies: "identity=prior"), registry);
            var ex = await Assert.ThrowsAsync<DownloadClientAuthenticationException>(() => client.Download(
                new RemoteAlbum { Release = new ReleaseInfo { Title = "Album [FLAC]", DownloadUrl = "https://bandcamp.com/download?type=album&id=1#format=flac" } },
                Mock.Of<IIndexer>()));
            Assert.Equal(problem, registry.Find(7)!.State.Problem?.Code);
            Assert.Contains(registry.Find(7)!.State.Problem!.Description, ex.Message);
            Assert.Equal(0, _loginPosts);
        }

        [Fact]
        public void TransportFailure_PreservesIdentityAndShowsAvailabilityAction()
        {
            _transportFailure = true;
            var registry = CreateRegistry();
            var settings = Settings(cookies: "identity=prior");
            var result = CreateClient(settings, registry).Test();
            Assert.False(result.IsValid);
            Assert.Equal(0, _loginPosts);
            Assert.Equal("identity=prior", settings.Cookies);
            Assert.Equal("Unavailable", registry.Find(7)!.State.Problem?.Code);
            Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("availability"));
        }

        [Fact]
        public void MissingSession_HasVisibleActionableHealth()
        {
            var registry = CreateRegistry();
            var result = CreateClient(Settings(email: "", password: ""), registry).Test();
            Assert.False(result.IsValid);
            Assert.Contains("no session configured", HealthMessage(registry));
            Assert.Contains("Last successful validation: never", HealthMessage(registry));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LoginRateLimit_ExposesWaitAndSuppressesFurtherLoginAttempts(bool onLoginPage)
        {
            if (onLoginPage) _loginPageStatus = HttpStatusCode.TooManyRequests;
            else _loginStatus = HttpStatusCode.TooManyRequests;
            _retryAfter = "120";
            var registry = CreateRegistry();
            var client = CreateClient(Settings(), registry);
            Assert.False(client.Test().IsValid);
            Assert.Equal("RateLimited", registry.Find(7)!.State.Problem?.Code);
            Assert.Contains("12:02:00", HealthMessage(registry));
            Assert.False(client.Test().IsValid);
            Assert.Equal(onLoginPage ? 0 : 1, _loginPosts);
            _now = _now.AddMinutes(2);
            _loginStatus = _loginPageStatus = HttpStatusCode.OK;
            Assert.True(client.Test().IsValid);
            Assert.Equal(onLoginPage ? 1 : 2, _loginPosts);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AvailabilityHealth_ReportsModeAndLastProvenValidationWithoutDiscardingCache(bool challenge)
        {
            var registry = CreateRegistry();
            var settings = Settings(cookies: "identity=prior");
            _homepageOverride = LoginFixtures.Read("homepage_logged_in.html");
            var client = CreateClient(settings, registry);
            Assert.True(client.Test().IsValid);
            _now = _now.AddMinutes(1);
            _homepageStatus = challenge ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
            _homepageOverride = LoginFixtures.Read("homepage_challenge.html");
            Assert.False(client.Test().IsValid);
            var health = HealthMessage(registry);
            Assert.Contains("Web identity", health);
            Assert.Contains("2026-01-01 12:00:00 UTC", health);
            Assert.Contains(challenge ? "browser" : "availability", health);
            Assert.Equal(0, _loginPosts);
            // A failed forced check must not shorten the previous successful cache.
            Assert.NotNull(await registry.Find(7)!.GetSessionAsync());
            Assert.Equal(2, _homepageCookies.Count);
        }

        [Theory]
        [InlineData("120", 120)]
        [InlineData("Thu, 01 Jan 2026 12:02:00 GMT", 120)]
        [InlineData("Thursday, 01-Jan-26 12:02:00 GMT", 120)]
        [InlineData("Thu Jan  1 12:02:00 2026", 120)]
        [InlineData("garbage", 300)]
        [InlineData(null, 300)]
        [InlineData("-1", 300)]
        [InlineData("1.5", 300)]
        [InlineData("0", 1)]
        [InlineData("Thu, 01 Jan 2026 11:00:00 GMT", 1)]
        public void RateLimit_WaitsUntilAdvertisedTimeWithoutLoginOrValidationLoop(string? retryAfter, int waitSeconds)
        {
            _homepageStatus = HttpStatusCode.TooManyRequests;
            _retryAfter = retryAfter;
            var registry = CreateRegistry();
            var settings = Settings(cookies: "identity=prior");
            var client = CreateClient(settings, registry);
            var first = client.Test();
            Assert.False(first.IsValid);
            Assert.Equal("RateLimited", registry.Find(7)!.State.Problem?.Code);
            Assert.Contains("wait until", HealthMessage(registry));
            _homepageStatus = HttpStatusCode.OK;
            _homepageOverride = LoginFixtures.Read("homepage_logged_in.html");
            _now = _now.AddSeconds(waitSeconds - 1);
            Assert.False(client.Test().IsValid);
            Assert.Single(_homepageCookies);
            Assert.Equal(0, _loginPosts);
            Assert.Equal("identity=prior", settings.Cookies);
            _now = _now.AddSeconds(1);
            Assert.True(client.Test().IsValid);
            Assert.Equal(2, _homepageCookies.Count);
        }

        [Fact]
        public void Test_UnrecognizedHomepage_RetainsIdentityWithoutPasswordLogin()
        {
            _homepageOverride = "<html><title>New Bandcamp</title></html>";
            var settings = Settings(cookies: "identity=prior");
            var registry = CreateRegistry();
            var result = CreateClient(settings, registry).Test();

            Assert.False(result.IsValid);
            Assert.Equal(0, _loginPosts);
            Assert.Equal("identity=prior", settings.Cookies);
            Assert.Equal("ChallengeOrUnrecognized", registry.Find(7)!.State.Problem?.Code);
            Assert.Contains("browser", HealthMessage(registry), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Test_CredentialsOnly_LogsInAndWritesCookieBackToSettings()
        {
            var settings = Settings();
            var client = CreateClient(settings);

            var result = client.Test();

            Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(e => e.ErrorMessage)));
            Assert.Equal(1, _loginPosts);
            Assert.Equal("identity=" + FreshIdentity, settings.Cookies);
            _repository.Verify(r => r.SetFields(
                It.Is<DownloadClientDefinition>(d => ((BandcampDownloadSettings)d.Settings).Cookies == "identity=" + FreshIdentity),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Once);
        }

        [Fact]
        public void Test_ValidCookieAndCredentials_UsesCookieWithoutLogin()
        {
            var client = CreateClient(Settings(cookies: "identity=" + FreshIdentity));

            var result = client.Test();

            Assert.True(result.IsValid);
            Assert.Equal(0, _loginPosts);
        }

        [Fact]
        public void Test_DeadCookieAndCredentials_LogsInOnce()
        {
            var settings = Settings(cookies: "identity=expired");
            var client = CreateClient(settings);

            var result = client.Test();

            Assert.True(result.IsValid);
            Assert.Equal(1, _loginPosts);
            Assert.Equal("identity=" + FreshIdentity, settings.Cookies);
        }

        [Fact]
        public void Test_BadCredentials_ReportsAgainstPassword()
        {
            _loginCbFixture = "login_cb_bad_credentials.json";
            var client = CreateClient(Settings());

            var result = client.Test();

            Assert.Contains(result.Errors, e => e.PropertyName == "Password");
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Never);
        }

        [Fact]
        public void BadCredentials_NotRetriedUntilSettingsChange()
        {
            _loginCbFixture = "login_cb_bad_credentials.json";
            var registry = CreateRegistry();

            CreateClient(Settings(), registry).Test();
            CreateClient(Settings(), registry).Test();
            Assert.Equal(1, _loginPosts);
            Assert.Equal(BandcampSessionProblem.BadCredentials, registry.Find(7)!.State.Problem);

            // The user fixes the password: Lidarr hands over a new settings object.
            _loginCbFixture = "login_cb_success.json";
            var fixedSettings = Settings(password: "correct-horse");
            var result = CreateClient(fixedSettings, registry).Test();

            Assert.Equal(2, _loginPosts);
            Assert.True(result.IsValid);
            Assert.Equal("identity=" + FreshIdentity, fixedSettings.Cookies);
        }

        [Fact]
        public async Task ConcurrentCallers_ShareOneLogin()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrived = 0;
            _homepageDelay = async () =>
            {
                // Hold the dead-cookie validations until all callers have arrived.
                if (Interlocked.Increment(ref arrived) <= 4)
                {
                    if (Volatile.Read(ref arrived) == 4)
                    {
                        gate.TrySetResult(true);
                    }

                    await gate.Task;
                }
            };

            var registry = CreateRegistry();
            var settings = Settings(cookies: "identity=expired");
            var clients = Enumerable.Range(0, 4).Select(_ => CreateClient(settings, registry)).ToList();

            var results = await Task.WhenAll(clients.Select(c => Task.Run(() => c.Test())));

            Assert.All(results, r => Assert.True(r.IsValid, string.Join(", ", r.Errors.Select(e => e.ErrorMessage))));
            Assert.Equal(1, _loginPosts);
        }

        [Fact]
        public void Test_CookieOnly_InvalidCookie_ReportsAgainstCookiesWithoutLogin()
        {
            var client = CreateClient(Settings(cookies: "identity=expired", email: "", password: ""));

            var result = client.Test();

            Assert.Contains(result.Errors, e => e.PropertyName == "Cookies");
            Assert.Equal(0, _loginPosts);
        }
    
        private string HealthMessage(IBandcampSessionProviderRegistry registry)
        {
            var factory = new Mock<IDownloadClientFactory>();
            factory.Setup(f => f.All()).Returns(new System.Collections.Generic.List<DownloadClientDefinition>
            {
                new() { Id = 7, Name = "Bandcamp", Enable = true, Implementation = nameof(BandcampDownloadClient) }
            });
            return new BandcampSessionHealthCheck(factory.Object, registry, Mock.Of<ILocalizationService>()).Check().Message ?? string.Empty;
        }

        [Fact]
        public void VerificationRequired_ReportsAgainstVerificationCodeAndHealthNamesCause()
        {
            _loginCbFixture = "login_cb_verification_required.json";
            var registry = CreateRegistry();

            var result = CreateClient(Settings(), registry).Test();

            Assert.Contains(result.Errors, e => e.PropertyName == "VerificationCode");
            Assert.Equal(BandcampSessionProblem.VerificationNeeded, registry.Find(7)!.State.Problem);
            Assert.Contains(BandcampSessionProblem.VerificationNeeded.Description, HealthMessage(registry));
        }

        [Fact]
        public void VerificationCode_SubmittedOnceThenClearedAndPersisted()
        {
            _loginCbFixture = "login_cb_verification_required.json";
            var registry = CreateRegistry();
            CreateClient(Settings(), registry).Test();

            // The user types the emailed code in; Lidarr hands over a new settings object.
            var withCode = Settings(verificationCode: EmailedCode);
            var result = CreateClient(withCode, registry).Test();

            Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(e => e.ErrorMessage)));
            Assert.Equal(2, _loginPosts);
            Assert.Contains("login.twofactor=" + EmailedCode, _loginBodies.Last());
            Assert.Equal("", withCode.VerificationCode);
            Assert.Equal("identity=" + FreshIdentity, withCode.Cookies);
            _repository.Verify(r => r.SetFields(
                It.Is<DownloadClientDefinition>(d => ((BandcampDownloadSettings)d.Settings).VerificationCode == ""),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.AtLeastOnce);
            Assert.Equal("", HealthMessage(registry));
        }

        [Fact]
        public void WrongVerificationCode_IsNotResubmitted()
        {
            _loginCbFixture = "login_cb_verification_required.json";
            var registry = CreateRegistry();
            var withCode = Settings(verificationCode: "000000");

            CreateClient(withCode, registry).Test();
            CreateClient(withCode, registry).Test();

            Assert.Equal(1, _loginPosts);
            Assert.Single(_loginBodies, b => b.Contains("login.twofactor=000000"));
            Assert.Equal("", withCode.VerificationCode);
        }

        [Fact]
        public void CaptchaRequired_NoRetryAndHealthPointsToCookiePaste()
        {
            _loginCbFixture = "login_cb_captcha_required.json";
            var registry = CreateRegistry();

            var first = CreateClient(Settings(), registry).Test();
            CreateClient(Settings(), registry).Test();

            Assert.Contains(first.Errors, e => e.PropertyName == "Cookies");
            Assert.Equal(1, _loginPosts);
            Assert.Equal(BandcampSessionProblem.CaptchaRequired, registry.Find(7)!.State.Problem);
            Assert.Contains("cookie", HealthMessage(registry), StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("login_cb_captcha_required.json")]
        [InlineData("login_cb_verification_required.json")]
        public void AfterChallenge_AtMostOneLoginPerSixHours(string fixture)
        {
            _loginCbFixture = fixture;
            var registry = CreateRegistry();

            CreateClient(Settings(), registry).Test();
            _now = _now.AddHours(5).AddMinutes(59);
            CreateClient(Settings(), registry).Test();
            Assert.Equal(1, _loginPosts);

            _now = _now.AddMinutes(2);
            CreateClient(Settings(), registry).Test();
            Assert.Equal(2, _loginPosts);

            CreateClient(Settings(), registry).Test();
            Assert.Equal(2, _loginPosts);
        }

        [Fact]
        public void AfterCaptcha_PastedCookieRestoresSessionAndClearsHealth()
        {
            _loginCbFixture = "login_cb_captcha_required.json";
            var registry = CreateRegistry();
            CreateClient(Settings(), registry).Test();

            var result = CreateClient(Settings(cookies: "identity=" + FreshIdentity), registry).Test();

            Assert.True(result.IsValid);
            Assert.Equal(1, _loginPosts);
            Assert.Equal("", HealthMessage(registry));
        }

        [Fact]
        public async Task ConcurrentCallerWithDifferentCredentials_DoesNotShareAnotherLogin()
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _loginDelay = () => release.Task;
            var registry = CreateRegistry();

            var goodClient = CreateClient(Settings(cookies: "identity=expired"), registry);
            var badClient = CreateClient(Settings(cookies: "identity=expired", password: "wrong-password"), registry);

            // The saved definition is separate from either in-memory settings object.
            _repository.Setup(r => r.Find(7)).Returns(new DownloadClientDefinition { Id = 7, Settings = Settings() });

            var good = Task.Run(() => goodClient.Test());
            while (Volatile.Read(ref _loginPosts) == 0)
            {
                await Task.Delay(5);
            }

            var bad = Task.Run(() => badClient.Test());
            await Task.Delay(100);
            release.TrySetResult(true);

            Assert.True((await good).IsValid);
            Assert.Contains((await bad).Errors, e => e.PropertyName == "Password");
        }

        [Fact]
        public async Task GetSession_CancelledToken_ThrowsWithoutLoggingIn()
        {
            var registry = CreateRegistry();
            CreateClient(Settings(cookies: "identity=expired"), registry);
            var provider = registry.Find(7) ?? registry.Get(7, Settings(cookies: "identity=expired"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RenewAsync(cts.Token));
            Assert.Equal(0, _loginPosts);
        }

        [Fact]
        public void AfterCaptcha_VerificationCodeDoesNotSkipCooldown()
        {
            _loginCbFixture = "login_cb_captcha_required.json";
            var registry = CreateRegistry();
            CreateClient(Settings(), registry).Test();

            CreateClient(Settings(verificationCode: "123456"), registry).Test();

            Assert.Equal(1, _loginPosts);
        }

        [Fact]
        public async Task Download_RefusalNamesTheActualCause()
        {
            _loginCbFixture = "login_cb_captcha_required.json";
            var client = CreateClient(Settings());

            var ex = await Assert.ThrowsAsync<DownloadClientAuthenticationException>(() => client.Download(
                new RemoteAlbum
                {
                    Release = new ReleaseInfo
                    {
                        Title = "Album [FLAC]",
                        DownloadUrl = "https://bandcamp.com/download?type=album&id=1#format=flac"
                    }
                },
                Mock.Of<IIndexer>()));

            Assert.Contains(BandcampSessionProblem.CaptchaRequired.Description, ex.Message);
        }
    }
}
