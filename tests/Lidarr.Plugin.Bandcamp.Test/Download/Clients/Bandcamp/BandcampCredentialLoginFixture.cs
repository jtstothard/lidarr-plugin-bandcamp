using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private string? _homepageOverride;
        private bool _transportFailure;
        private HttpStatusCode _homepageStatus = HttpStatusCode.OK;
        private string? _retryAfter;
        private HttpStatusCode _loginStatus = HttpStatusCode.OK;
        private HttpStatusCode _loginPageStatus = HttpStatusCode.OK;
        private string? _loginResponse;
        private bool _loginTransportFailure;
        private bool _omitIdentity;
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

                        var page = cookie.Contains("fresh-identity") && _candidateValid ? "homepage_logged_in.html" : "homepage_logged_out.html";
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
                        if (!_omitIdentity && fixture == "login_cb_success.json")
                        {
                            headers.Add("Set-Cookie", $"identity={FreshIdentity}; domain=.bandcamp.com; path=/; secure; HttpOnly");
                        }

                        return new HttpResponse(request, headers, _loginResponse ?? LoginFixtures.Read(fixture), _loginStatus);
                    }

                    if (url == "https://bandcamp.com/login")
                    {
                        if (_loginTransportFailure) throw new System.Net.Http.HttpRequestException("private-response-sentinel");
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
        public async Task NamedConsumersCoalesceTheExistingLoginAndPersistOneOwner()
        {
            var settings = Settings(cookies: "identity=prior");
            var definition = new DownloadClientDefinition { Id = 7, Name = "Shared fan", Implementation = nameof(BandcampDownloadClient), Settings = settings };
            var saved = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
            _repository.Setup(r => r.All()).Returns(new List<DownloadClientDefinition> { definition });
            _repository.Setup(r => r.Find(7)).Returns(() => new DownloadClientDefinition
            {
                Id = 7, Name = "Shared fan", Implementation = nameof(BandcampDownloadClient),
                Settings = Newtonsoft.Json.JsonConvert.DeserializeObject<BandcampDownloadSettings>(saved)!
            });
            _repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Callback<DownloadClientDefinition, Expression<Func<DownloadClientDefinition, object>>[]>((d, _) => saved = Newtonsoft.Json.JsonConvert.SerializeObject(d.Settings));
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _loginDelay = () => { entered.TrySetResult(true); return release.Task; };
            var http = new BandcampHttpClient(_http.Object, _logger);
            var registry = new BandcampSessionProviderRegistry(new BandcampApiClient(http, _logger),
                new BandcampLoginClient(http, _logger), new BandcampSessionStore(_repository.Object), _logger, _repository.Object);
            var first = registry.GetNamed("Shared fan").GetSessionAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = registry.Get(8, new BandcampDownloadSettings { AuthConfiguration = "Shared fan", Cookies = "identity=other-account" }).GetSessionAsync();
            release.SetResult(true);
            var results = await Task.WhenAll(first, second);
            Assert.All(results, result => Assert.Equal("identity=" + FreshIdentity, result?.Cookies));
            Assert.Equal(1, _loginPosts);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Once);
        }

        [Fact]
        public async Task OwnerChangedToConsumerCannotReceiveAnotherAccountsChallengeMetadata()
        {
            _loginCbFixture = "login_cb_captcha_required.json";
            var settings = Settings(cookies: "identity=prior");
            var client = CreateClient(settings);
            _loginDelay = () =>
            {
                _repository.Setup(r => r.Find(7)).Returns(new DownloadClientDefinition
                {
                    Id = 7, Settings = new BandcampDownloadSettings { AuthConfiguration = "Another account", Cookies = "identity=prior" }
                });
                return Task.CompletedTask;
            };
            Assert.False((await Task.Run(() => client.Test())).IsValid);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Never);
        }

        [Theory]
        [InlineData(200, "ChallengeOrUnrecognized")]
        [InlineData(503, "Unavailable")]
        [InlineData(429, "RateLimited")]
        public async Task ImportedCandidateFailureUsesTypedDiagnosisAndRetainsIdentity(int status, string problem)
        {
            var settings = Settings(cookies: "identity=prior");
            var registry = CreateRegistry();
            CreateClient(settings, registry);
            var provider = registry.Get(7, settings);
            _homepageOverride = "<html>challenge or unavailable</html>";
            _homepageStatus = (HttpStatusCode)status;
            _retryAfter = "120";
            Assert.Null(await provider.ReplaceIdentityAsync("identity=" + FreshIdentity));
            Assert.Equal(problem, provider.State.Problem?.Code);
            Assert.Equal("identity=prior", settings.Cookies);
            Assert.Equal(0, _loginPosts);
            if (status == 429)
            {
                Assert.Null(await provider.ReplaceIdentityAsync("identity=" + FreshIdentity));
                Assert.Single(_homepageCookies);
            }
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Never);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        public async Task EmptyImportedIdentityCannotReplacePriorSession(string candidate)
        {
            var settings = Settings(cookies: "identity=prior");
            var registry = CreateRegistry();
            CreateClient(settings, registry);
            _homepageOverride = LoginFixtures.Read("homepage_logged_in.html");
            Assert.Null(await registry.Get(7, settings).ReplaceIdentityAsync(candidate));
            Assert.Equal("identity=prior", settings.Cookies);
            Assert.Empty(_homepageCookies);
        }

        [Fact]
        public void StaleVerificationMetadataCannotOverridePendingCaptcha()
        {
            _loginCbFixture = "login_cb_captcha_required.json";
            var registry = CreateRegistry();
            CreateClient(Settings(), registry).Test();
            var staleSettings = Settings(verificationCode: EmailedCode);
            staleSettings.ChallengeUntilUtc = _now.AddHours(6);
            staleSettings.ChallengeProblem = BandcampSessionProblem.VerificationNeeded.Code;
            Assert.False(CreateClient(staleSettings, registry).Test().IsValid);
            Assert.Equal(1, _loginPosts);
            Assert.Equal(BandcampSessionProblem.CaptchaRequired, registry.Find(7)!.State.Problem);
        }

        [Fact]
        public void HelperDoesNotFollowANameReassignedBetweenSelectionAndReplacement()
        {
            var original = new DownloadClientDefinition { Id = 7, Name = "Bandcamp", Implementation = nameof(BandcampDownloadClient), Settings = Settings(cookies: "identity=prior") };
            var reassigned = new DownloadClientDefinition { Id = 8, Name = "Bandcamp", Implementation = nameof(BandcampDownloadClient), Settings = Settings(cookies: "identity=other") };
            var http = new BandcampHttpClient(_http.Object, _logger);
            var registry = new BandcampSessionProviderRegistry(new BandcampApiClient(http, _logger),
                new BandcampLoginClient(http, _logger), new BandcampSessionStore(_repository.Object), _logger, _repository.Object);
            var client = CreateClient(Settings(cookies: "identity=" + FreshIdentity), registry);
            _repository.Setup(r => r.All()).Returns(new List<DownloadClientDefinition> { original });
            var operationId = BeginRenewal(client);
            var reads = 0;
            _repository.Setup(r => r.All()).Returns(() => new List<DownloadClientDefinition> { ++reads == 1 ? original : reassigned });
            _repository.Setup(r => r.Find(7)).Returns(original);
            _repository.Setup(r => r.Find(8)).Returns(reassigned);
            var result = client.RequestAction("renewIdentity", new Dictionary<string, string> { ["fanId"] = "12345678", ["operationId"] = operationId });
            Assert.False((bool)result.GetType().GetProperty("Success")!.GetValue(result)!);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Never());
        }

        [Theory]
        [InlineData("99", false, false)]
        [InlineData("12345678", true, false)]
        [InlineData("12345678", false, true)]
        public void HelperActionValidatesExpectedAccountBeforeSharedCommit(string expectedFan, bool success, bool storageFails)
        {
            var saved = Settings(cookies: "identity=prior", email: "", password: "");
            var bandcampHttp = new BandcampHttpClient(_http.Object, _logger);
            var registry = new BandcampSessionProviderRegistry(new BandcampApiClient(bandcampHttp, _logger),
                new BandcampLoginClient(bandcampHttp, _logger), new BandcampSessionStore(_repository.Object), _logger, _repository.Object);
            var candidate = Settings(cookies: "identity=" + FreshIdentity, email: "", password: "");
            var client = CreateClient(candidate, registry);
            var owner = new DownloadClientDefinition { Id = 7, Name = "Bandcamp", Implementation = nameof(BandcampDownloadClient), Settings = saved };
            _repository.Setup(r => r.All()).Returns(new List<DownloadClientDefinition> { owner });
            _repository.Setup(r => r.Find(7)).Returns(owner);
            if (storageFails) _repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>())).Throws(new System.IO.IOException("sentinel-secret"));
            var result = client.RequestAction("renewIdentity", new Dictionary<string, string> { ["fanId"] = expectedFan, ["operationId"] = BeginRenewal(client) });
            Assert.Equal(success, (bool)result.GetType().GetProperty("Success")!.GetValue(result)!);
            Assert.Equal(0, _loginPosts);
            Assert.Equal(success ? "identity=" + FreshIdentity : "identity=prior", saved.Cookies);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), success || storageFails ? Times.Once() : Times.Never());
            if (storageFails) Assert.Equal("StorageFailure", result.GetType().GetProperty("Problem")!.GetValue(result));
        }

        [Fact]
        public async Task HelperAcceptedCancellationDuringValidationPreservesPriorIdentity()
        {
            var saved = Settings(cookies: "identity=prior", email: "", password: "");
            var http = new BandcampHttpClient(_http.Object, _logger);
            var registry = new BandcampSessionProviderRegistry(new BandcampApiClient(http, _logger),
                null, new BandcampSessionStore(_repository.Object), _logger, _repository.Object);
            var client = CreateClient(Settings(cookies: "identity=" + FreshIdentity), registry);
            var owner = new DownloadClientDefinition { Id = 7, Name = "Bandcamp", Implementation = nameof(BandcampDownloadClient), Settings = saved };
            _repository.Setup(r => r.All()).Returns(new List<DownloadClientDefinition> { owner });
            _repository.Setup(r => r.Find(7)).Returns(owner);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _homepageDelay = () => { entered.TrySetResult(); return release.Task; };
            var query = new Dictionary<string, string> { ["fanId"] = "12345678", ["operationId"] = BeginRenewal(client) };
            var pending = Task.Run(() => client.RequestAction("renewIdentity", query));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            object cancellation;
            try { cancellation = client.RequestAction("cancelIdentityRenewal", query); }
            finally { release.TrySetResult(); }
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Cancelled", ActionState(cancellation));
            Assert.Equal("Cancelled", ActionState(result));
            Assert.Equal("identity=prior", saved.Cookies);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Never());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task HelperHttpWorkflowCancelsSubmittedValidationAndReconcilesReceipt(bool loseCancelResponse)
        {
            var saved = Settings(cookies: "identity=prior", email: "", password: "");
            var http = new BandcampHttpClient(_http.Object, _logger);
            var registry = new BandcampSessionProviderRegistry(new BandcampApiClient(http, _logger),
                null, new BandcampSessionStore(_repository.Object), _logger, _repository.Object);
            var client = CreateClient(Settings(cookies: "identity=" + FreshIdentity), registry);
            var owner = new DownloadClientDefinition { Id = 7, Name = "Bandcamp", Implementation = nameof(BandcampDownloadClient), Settings = saved };
            _repository.Setup(r => r.All()).Returns(new List<DownloadClientDefinition> { owner });
            _repository.Setup(r => r.Find(7)).Returns(owner);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _homepageDelay = () => { entered.TrySetResult(); return release.Task; };
            using var cancellation = new CancellationTokenSource();
            var output = new System.IO.StringWriter(); var error = new System.IO.StringWriter();
            var transport = new RenewalActionTransport(client) { LoseCancelResponse = loseCancelResponse };
            var pending = Lidarr.Plugin.Bandcamp.CookiePush.CookiePushCommand.RunAsync(
                new[] { "--client-name", "Bandcamp", "--fan-id", "12345678" },
                new System.IO.StringReader("identity=" + FreshIdentity), output, error,
                key => key == "LIDARR_URL" ? "https://lidarr.example" : "sentinel-key", transport,
                cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            int exit;
            try { exit = await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { release.TrySetResult(); }
            await transport.Submitted!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, exit);
            if (loseCancelResponse) Assert.Contains("uncertain", error.ToString());
            else Assert.Contains("Cancellation acknowledged", error.ToString());
            Assert.Equal("identity=prior", saved.Cookies);
            Assert.DoesNotContain(FreshIdentity, output.ToString() + error);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task HelperCommitRaceReportsUncertaintyThenReconcilesWithoutRollback(bool loseResponse)
        {
            var saved = Settings(cookies: "identity=prior", email: "", password: "");
            var http = new BandcampHttpClient(_http.Object, _logger);
            var registry = new BandcampSessionProviderRegistry(new BandcampApiClient(http, _logger),
                null, new BandcampSessionStore(_repository.Object), _logger, _repository.Object);
            var client = CreateClient(Settings(cookies: "identity=" + FreshIdentity), registry);
            var owner = new DownloadClientDefinition { Id = 7, Name = "Bandcamp", Implementation = nameof(BandcampDownloadClient), Settings = saved };
            _repository.Setup(r => r.All()).Returns(new List<DownloadClientDefinition> { owner });
            _repository.Setup(r => r.Find(7)).Returns(owner);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim(loseResponse);
            var persisted = "identity=prior";
            var writes = 0;
            _repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Callback<DownloadClientDefinition, Expression<Func<DownloadClientDefinition, object>>[]>((d, _) =>
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    persisted = ((BandcampDownloadSettings)d.Settings).Cookies;
                    Interlocked.Increment(ref writes);
                });
            using var cancellation = new CancellationTokenSource();
            var output = new System.IO.StringWriter(); var error = new System.IO.StringWriter();
            var transport = new RenewalActionTransport(client) { LoseResponse = loseResponse };
            var pending = Lidarr.Plugin.Bandcamp.CookiePush.CookiePushCommand.RunAsync(
                new[] { "--client-name", "Bandcamp", "--fan-id", "12345678" },
                new System.IO.StringReader("identity=" + FreshIdentity), output, error,
                key => key == "LIDARR_URL" ? "https://lidarr.example" : "sentinel-key", transport,
                cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!loseResponse) cancellation.Cancel();
            int exit;
            try { exit = await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { release.Set(); }
            await transport.Submitted!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(loseResponse ? 0 : 1, exit);
            if (!loseResponse)
            {
                Assert.Contains("uncertain", error.ToString());
                Assert.DoesNotContain("Cancellation acknowledged", error.ToString());
            }
            Assert.Equal("identity=" + FreshIdentity, persisted);
            Assert.Equal(1, writes);
            // Later sessions must never be rolled back by reconciliation of this receipt.
            saved.Cookies = "identity=newer-session";
            var statusOutput = new System.IO.StringWriter();
            var status = await Lidarr.Plugin.Bandcamp.CookiePush.CookiePushCommand.RunAsync(
                new[] { "--client-name", "Bandcamp", "--renewal-status", transport.OperationId! },
                new System.IO.StringReader("must-not-be-read"), statusOutput, System.IO.TextWriter.Null,
                key => key == "LIDARR_URL" ? "https://lidarr.example" : "sentinel-key", new RenewalActionTransport(client));
            Assert.Equal(0, status);
            Assert.Contains("saved", statusOutput.ToString());
            Assert.Equal("identity=newer-session", saved.Cookies);
            Assert.Equal(1, writes);
            var duplicate = client.RequestAction("renewIdentity", new Dictionary<string, string>
                { ["operationId"] = transport.OperationId!, ["fanId"] = "12345678" });
            Assert.Equal("Committed", ActionState(duplicate));
            Assert.Equal(1, writes);
        }

        [Fact]
        public void CancellationBeforeSubmissionAndUnknownOrOtherOwnerIdsNeverCommit()
        {
            var saved = Settings(cookies: "identity=prior");
            var http = new BandcampHttpClient(_http.Object, _logger);
            var registry = new BandcampSessionProviderRegistry(new BandcampApiClient(http, _logger),
                null, new BandcampSessionStore(_repository.Object), _logger, _repository.Object);
            var client = CreateClient(Settings(cookies: "identity=" + FreshIdentity), registry);
            var owner = new DownloadClientDefinition { Id = 7, Name = "Bandcamp", Implementation = nameof(BandcampDownloadClient), Settings = saved };
            _repository.Setup(r => r.All()).Returns(new List<DownloadClientDefinition> { owner });
            _repository.Setup(r => r.Find(7)).Returns(owner);
            var query = new Dictionary<string, string> { ["operationId"] = BeginRenewal(client), ["fanId"] = "12345678" };
            Assert.Equal("Cancelled", ActionState(client.RequestAction("cancelIdentityRenewal", query)));
            Assert.Equal("Cancelled", ActionState(client.RequestAction("renewIdentity", query)));
            query["operationId"] = Guid.NewGuid().ToString("D");
            Assert.Equal("Unknown", ActionState(client.RequestAction("renewIdentity", query)));
            query["operationId"] = BeginRenewal(client);
            client.Definition.Id = 8;
            Assert.Equal("Unknown", ActionState(client.RequestAction("renewIdentity", query)));
            Assert.Equal("Unknown", ActionState(client.RequestAction("cancelIdentityRenewal", query)));
            Assert.Equal("identity=prior", saved.Cookies);
            Assert.Empty(_homepageCookies);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(),
                It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Never());
        }

        private sealed class RenewalActionTransport(BandcampDownloadClient client) : System.Net.Http.HttpMessageHandler
        {
            public Task<object>? Submitted { get; private set; }
            public string? OperationId { get; private set; }
            public bool LoseResponse { get; init; }
            public bool LoseCancelResponse { get; init; }
            protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
            {
                if (request.Method == System.Net.Http.HttpMethod.Get)
                    return Json("""[{"id":7,"name":"Bandcamp","implementation":"BandcampDownloadClient","fields":[]}]""");
                var uri = request.RequestUri!;
                var action = uri.AbsolutePath.Split('/').Last();
                var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                Assert.Equal(7, body["id"]!.GetValue<int>());
                Assert.Equal("Bandcamp", body["name"]!.GetValue<string>());
                if (action == "renewIdentity")
                    ((BandcampDownloadSettings)client.Definition.Settings).Cookies = body["fields"]![0]!["value"]!.GetValue<string>();
                else Assert.Empty(body["fields"]!.AsArray());
                var work = Task.Run(() => client.RequestAction(action, query));
                if (action == "renewIdentity") Submitted = work;
                var result = await work.WaitAsync(token);
                if (action == "beginIdentityRenewal")
                    OperationId = (string)result.GetType().GetProperty("OperationId")!.GetValue(result)!;
                if ((action == "renewIdentity" && LoseResponse) || (action == "cancelIdentityRenewal" && LoseCancelResponse)) throw new System.Net.Http.HttpRequestException("sentinel-key response lost");
                return Json(System.Text.Json.JsonSerializer.Serialize(result,
                    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
            }
            private static System.Net.Http.HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent(json) };
        }

        private static string BeginRenewal(BandcampDownloadClient client)
        {
            var receipt = client.RequestAction("beginIdentityRenewal", new Dictionary<string, string>());
            return (string)receipt.GetType().GetProperty("OperationId")!.GetValue(receipt)!;
        }

        private static string? ActionState(object? result) => result?.GetType().GetProperty("State")?.GetValue(result) as string;

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

        [Theory]
        [InlineData("")]
        [InlineData("identity=prior")]
        public void Test_UnrecognizedCredentialResponse_ReportsBrowserHandoffWithoutPasswordReplay(string cookies)
        {
            _loginResponse = "<html>bot challenge private-response-sentinel</html>";
            var settings = Settings(cookies: cookies);
            var registry = CreateRegistry();
            var client = CreateClient(settings, registry);
            var result = client.Test();

            Assert.False(result.IsValid);
            var message = string.Join("; ", result.Errors.Select(e => e.ErrorMessage));
            Assert.Contains("unrecognized login response", message);
            Assert.DoesNotContain("temporarily unavailable", message);
            Assert.DoesNotContain("saved identity", message);
            Assert.DoesNotContain("private-response-sentinel", message + HealthMessage(registry));
            Assert.Contains("unrecognized login response", HealthMessage(registry));
            Assert.Equal("LoginResponseUnrecognized", registry.Find(7)!.State.Problem?.Code);
            Assert.Equal(cookies, settings.Cookies);
            Assert.False(client.Test().IsValid);
            Assert.Equal(1, _loginPosts);
            // A restart and a submitted code cannot turn an unknown challenge into a password replay.
            settings.VerificationCode = "123456";
            var restarted = CreateRegistry();
            Assert.False(CreateClient(settings, restarted).Test().IsValid);
            Assert.Contains("unrecognized login response", HealthMessage(restarted));
            Assert.Equal(1, _loginPosts);
            _now = _now.AddHours(6);
            Assert.False(CreateClient(settings, restarted).Test().IsValid);
            Assert.Equal(2, _loginPosts);
        }

        [Theory]
        [InlineData("page", "LoginPageRejected", "could not load the login page", 0)]
        [InlineData("callback", "LoginCallbackRejected", "login submission returned an unsuccessful HTTP status", 1)]
        [InlineData("no-session", "LoginSessionMissing", "accepted login but issued no identity cookie", 1)]
        [InlineData("transport", "LoginTransportFailure", "login request could not be completed", 0)]
        [InlineData("json-array", "LoginResponseUnrecognized", "unrecognized login response", 1)]
        [InlineData("json-fields", "LoginResponseUnrecognized", "unrecognized login response", 1)]
        public void Test_CredentialFailures_PreserveSanitizedReasonInTestAndHealth(string failure, string code, string reason, int posts)
        {
            if (failure == "page") _loginPageStatus = HttpStatusCode.Forbidden;
            if (failure == "callback") _loginStatus = HttpStatusCode.ServiceUnavailable;
            if (failure == "no-session") _omitIdentity = true;
            if (failure == "transport") _loginTransportFailure = true;
            if (failure == "json-array") _loginResponse = "[\"private-response-sentinel\"]";
            if (failure == "json-fields") _loginResponse = "{\"errors\":[null,3,\"private-response-sentinel\"]}";
            var settings = Settings();
            var registry = CreateRegistry();
            var result = CreateClient(settings, registry).Test();
            Assert.False(result.IsValid);
            var message = string.Join("; ", result.Errors.Select(e => e.ErrorMessage));
            Assert.Contains(reason, message);
            Assert.Contains(reason, HealthMessage(registry));
            Assert.Equal(code, registry.Find(7)!.State.Problem?.Code);
            Assert.DoesNotContain("saved identity", message + HealthMessage(registry));
            Assert.DoesNotContain("private-response-sentinel", message + HealthMessage(registry));
            Assert.DoesNotContain("fan@example.com", message + HealthMessage(registry));
            Assert.DoesNotContain("hunter2", message + HealthMessage(registry));
            Assert.DoesNotContain(FreshIdentity, message + HealthMessage(registry));
            Assert.Equal("", settings.Cookies);
            Assert.Equal(posts, _loginPosts);
            _repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()),
                failure.StartsWith("json") ? Times.Once() : Times.Never());
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
