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
        private bool _candidateValid = true;
        private int _loginPosts;
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
                        var cookie = request.Headers.GetSingleValue("Cookie") ?? string.Empty;
                        _homepageCookies.Enqueue(cookie);
                        if (_homepageDelay != null)
                        {
                            await _homepageDelay();
                        }

                        var page = cookie.Contains("fresh-identity") && _candidateValid ? "homepage_logged_in.html" : "homepage_logged_out.html";
                        return new HttpResponse(request, new HttpHeader(), LoginFixtures.Read(page), HttpStatusCode.OK);
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
                        if (fixture == "login_cb_success.json")
                        {
                            headers.Add("Set-Cookie", $"identity={FreshIdentity}; domain=.bandcamp.com; path=/; secure; HttpOnly");
                        }

                        return new HttpResponse(request, headers, LoginFixtures.Read(fixture), HttpStatusCode.OK);
                    }

                    if (url == "https://bandcamp.com/login")
                    {
                        return new HttpResponse(request, new HttpHeader(), LoginFixtures.Read("login_page.html"), HttpStatusCode.OK);
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
        public async Task ImportedCandidateIsValidatedAndPersistedWithoutPasswordLogin()
        {
            var settings = Settings(cookies: "identity=prior", email: "", password: "");
            var registry = CreateRegistry();
            CreateClient(settings, registry);
            var session = await registry.Get(7, settings).ReplaceIdentityAsync("identity=" + FreshIdentity);
            Assert.NotNull(session);
            Assert.Equal("identity=" + FreshIdentity, session.Cookies);
            Assert.Equal(session.Cookies, settings.Cookies);
            Assert.Equal(0, _loginPosts);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Once);
        }

        [Fact]
        public void CaptchaStillSuppressesRetryWhenCodePersistenceFails()
        {
            _loginCbFixture = "login_cb_captcha_required.json";
            var settings = Settings(verificationCode: "wrong-code");
            var registry = CreateRegistry();
            var client = CreateClient(settings, registry);
            _repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Throws(new InvalidOperationException("storage unavailable"));
            Assert.False(client.Test().IsValid);
            Assert.False(client.Test().IsValid);
            Assert.Equal(1, _loginPosts);
            Assert.Equal("", settings.VerificationCode);
        }

        [Fact]
        public async Task IndependentRecoveryProvidersCannotOverwriteAnAlreadyCommittedIdentity()
        {
            var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _loginDelay = () =>
            {
                if (Volatile.Read(ref _loginPosts) == 2) arrived.TrySetResult(true);
                return release.Task;
            };
            var firstSettings = Settings(cookies: "identity=prior");
            var secondSettings = Settings(cookies: "identity=prior");
            var first = CreateClient(firstSettings, CreateRegistry());
            var second = CreateClient(secondSettings, CreateRegistry());
            var persisted = Settings(cookies: "identity=prior");
            _repository.Setup(r => r.Find(7)).Returns(() => new DownloadClientDefinition { Id = 7, Settings = persisted });
            _repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Callback<DownloadClientDefinition, Expression<Func<DownloadClientDefinition, object>>[]>((definition, _) =>
                    persisted = Newtonsoft.Json.JsonConvert.DeserializeObject<BandcampDownloadSettings>(
                        Newtonsoft.Json.JsonConvert.SerializeObject(definition.Settings))!);
            var pending = new[] { Task.Run(() => first.Test()), Task.Run(() => second.Test()) };
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.SetResult(true);
            var results = await Task.WhenAll(pending);
            Assert.Single(results, result => result.IsValid);
            Assert.Equal("identity=" + FreshIdentity, persisted.Cookies);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Once);
        }

        [Fact]
        public async Task DownloadRefusalReportsStorageFailureAndRetainsIdentity()
        {
            var settings = Settings(cookies: "identity=prior");
            var client = CreateClient(settings);
            _repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Throws(new InvalidOperationException("storage unavailable"));
            var exception = await Assert.ThrowsAsync<DownloadClientAuthenticationException>(() => client.Download(
                new RemoteAlbum { Release = new ReleaseInfo { Title = "Album [FLAC]",
                    DownloadUrl = "https://bandcamp.com/download?type=album&id=1#format=flac" } }, Mock.Of<IIndexer>()));
            Assert.Contains(BandcampSessionProblem.StorageFailure.Description, exception.Message);
            Assert.Equal("identity=prior", settings.Cookies);
        }

        [Fact]
        public async Task CancellationDuringCandidateValidationRetainsPriorIdentity()
        {
            var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _homepageDelay = () =>
            {
                if (_homepageCookies.Last().Contains("fresh-identity"))
                {
                    arrived.TrySetResult(true);
                    return release.Task;
                }

                return Task.CompletedTask;
            };
            var settings = Settings(cookies: "identity=prior");
            var registry = CreateRegistry();
            CreateClient(settings, registry);
            var provider = registry.Get(7, settings);
            using var cancellation = new CancellationTokenSource();
            var pending = provider.RenewAsync(cancellation.Token);
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            release.SetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal("identity=prior", settings.Cookies);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Never);
        }

        [Fact]
        public void CandidateBecomesActiveOnlyAfterPersistenceAndSurvivesReRead()
        {
            var settings = Settings(cookies: "identity=prior");
            var client = CreateClient(settings);
            BandcampDownloadSettings? saved = null;
            _repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Callback<DownloadClientDefinition, Expression<Func<DownloadClientDefinition, object>>[]>((definition, _) =>
                {
                    Assert.Equal("identity=prior", settings.Cookies);
                    saved = Newtonsoft.Json.JsonConvert.DeserializeObject<BandcampDownloadSettings>(
                        Newtonsoft.Json.JsonConvert.SerializeObject(definition.Settings));
                });
            Assert.True(client.Test().IsValid);
            Assert.Equal("identity=" + FreshIdentity, saved!.Cookies);
            Assert.True(CreateClient(saved, CreateRegistry()).Test().IsValid);
            Assert.Equal(1, _loginPosts);
        }

        [Fact]
        public void FailureToPersistConsumedCodeDoesNotPublishCandidate()
        {
            var settings = Settings(cookies: "identity=prior", verificationCode: EmailedCode);
            var registry = CreateRegistry();
            var client = CreateClient(settings, registry);
            _repository.SetupSequence(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Throws(new InvalidOperationException("storage unavailable"));
            Assert.False(client.Test().IsValid);
            Assert.Equal("identity=prior", settings.Cookies);
            Assert.Equal(BandcampSessionProblem.StorageFailure, registry.Find(7)!.State.Problem);
        }

        [Fact]
        public async Task SettingsChangedDuringLoginDoesNotGetOverwrittenByOldCandidate()
        {
            var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _loginDelay = () => { arrived.TrySetResult(true); return release.Task; };
            var settings = Settings(cookies: "identity=prior");
            var registry = CreateRegistry();
            var client = CreateClient(settings, registry);
            var pending = Task.Run(() => client.Test());
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var replacement = Settings(cookies: "identity=manual", password: "new-password");
            registry.Get(7, replacement);
            _repository.Setup(r => r.Find(7)).Returns(new DownloadClientDefinition { Id = 7, Settings = replacement });
            release.SetResult(true);
            Assert.False((await pending).IsValid);
            Assert.Equal("identity=manual", replacement.Cookies);
            Assert.Equal("identity=prior", settings.Cookies);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Never);
        }

        [Fact]
        public void StorageFailureLeavesPriorIdentityAndReportsStorageProblem()
        {
            var settings = Settings(cookies: "identity=prior");
            var registry = CreateRegistry();
            var client = CreateClient(settings, registry);
            _repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Throws(new InvalidOperationException("storage unavailable"));
            Assert.False(client.Test().IsValid);
            Assert.Equal("identity=prior", settings.Cookies);
            Assert.Equal(BandcampSessionProblem.StorageFailure, registry.Find(7)!.State.Problem);
            Assert.Contains("storage", HealthMessage(registry));
        }

        [Fact]
        public void InvalidLoginCandidateLeavesPriorIdentityUntouched()
        {
            _candidateValid = false;
            var settings = Settings(cookies: "identity=prior");
            var result = CreateClient(settings).Test();
            Assert.False(result.IsValid);
            Assert.Equal("identity=prior", settings.Cookies);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Never);
        }

        [Fact]
        public async Task RestartedMonitorRetainsCaptchaCooldownEvenWithVerificationCode()
        {
            _loginCbFixture = "login_cb_captcha_required.json";
            var settings = Settings();
            var client = CreateClient(settings, CreateRegistry());
            BandcampDownloadSettings? saved = null;
            _repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Callback<DownloadClientDefinition, Expression<Func<DownloadClientDefinition, object>>[]>((definition, _) =>
                    saved = Newtonsoft.Json.JsonConvert.DeserializeObject<BandcampDownloadSettings>(
                        Newtonsoft.Json.JsonConvert.SerializeObject(definition.Settings)));
            client.Test();
            var restored = saved!;
            restored.VerificationCode = EmailedCode;
            _now = _now.AddHours(5).AddMinutes(59);
            var registry = CreateRegistry();
            var factory = new Mock<IDownloadClientFactory>();
            var definition = new DownloadClientDefinition
            {
                Id = 7, Name = "Bandcamp", Enable = true,
                Implementation = nameof(BandcampDownloadClient), Settings = restored
            };
            _repository.Setup(r => r.Find(7)).Returns(definition);
            factory.Setup(f => f.All()).Returns(new System.Collections.Generic.List<DownloadClientDefinition> { definition });
            var monitor = new BandcampSessionMonitor(factory.Object, registry,
                Mock.Of<NzbDrone.Core.Messaging.Events.IEventAggregator>(), _logger, () => _now);
            await monitor.RunDueChecksAsync();
            Assert.Equal(1, _loginPosts);
            Assert.Equal(BandcampSessionProblem.CaptchaRequired, registry.Find(7)!.State.Problem);
            _now = _now.AddHours(1);
            await monitor.RunDueChecksAsync();
            Assert.Equal(2, _loginPosts);
            Assert.Equal(BandcampSessionStatus.Valid, registry.Find(7)!.State.Status);
            Assert.Equal("", restored.VerificationCode);
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

            // The current settings now select the other credentials: the old candidate
            // must not win even though Bandcamp accepted that older login.
            Assert.False((await good).IsValid);
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
