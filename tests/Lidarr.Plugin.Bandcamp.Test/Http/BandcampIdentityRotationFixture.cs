using Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp;

using System.Linq;

using System;
using System.Linq.Expressions;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NLog;
using Newtonsoft.Json;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Download.Clients.Bandcamp.Session;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Http
{
    public class BandcampIdentityRotationFixture
    {
        private readonly Mock<IHttpClient> _http = new();
        private readonly Mock<IDownloadClientRepository> _repository = new();
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private BandcampDownloadSettings _saved = new() { Cookies = "identity=prior" };
        private string? _rotation = "identity=rotated; Domain=.bandcamp.com; Path=/; Secure; HttpOnly";
        private bool _foldResponseHeaders;
        private bool _homepageRotation;
        private string? _extraCookie;
        private HttpStatusCode _responseStatus = HttpStatusCode.OK;
        private string? _responseUrl;
        private Func<Task>? _candidateDelay;
        private int _writes;
        private int _validations;
        private bool _candidateValid = true;
        private bool _otherFan;
        private bool _storageFails;
        private readonly BandcampHttpClient _client;
        private readonly BandcampApiClient _api;

        public BandcampIdentityRotationFixture()
        {
            _repository.Setup(r => r.All()).Returns(() => new[] { Definition() }.ToList());
            _repository.Setup(r => r.Find(7)).Returns(() => Definition());
            _repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Callback<DownloadClientDefinition, Expression<Func<DownloadClientDefinition, object>>[]>((d, _) =>
                {
                    if (_storageFails) throw new InvalidOperationException("storage unavailable");
                    _saved = JsonConvert.DeserializeObject<BandcampDownloadSettings>(JsonConvert.SerializeObject(d.Settings))!;
                    _writes++;
                });
            _http.Setup(h => h.ExecuteAsync(It.IsAny<HttpRequest>())).Returns(async (HttpRequest request) =>
            {
                var headers = new HttpHeader();
                if (request.Url.FullUri == "https://bandcamp.com")
                {
                    _validations++;
                    if (_homepageRotation && request.Headers.GetSingleValue("Cookie") == "identity=prior") headers.Add("Set-Cookie", _rotation!);
                    if (request.Headers.GetSingleValue("Cookie") == "identity=rotated" && _candidateDelay != null) await _candidateDelay();
                    var valid = request.Headers.GetSingleValue("Cookie") == "identity=prior" || _candidateValid;
                    var body = LoginFixtures.Read(valid ? "homepage_logged_in.html" : "homepage_logged_out.html");
                    if (_otherFan && request.Headers.GetSingleValue("Cookie") == "identity=rotated") body = "var pagedata = {\"fan_id\":999};";
                    return new HttpResponse(request, headers, body, HttpStatusCode.OK);
                }
                if (_rotation != null) headers.Add("Set-Cookie", _rotation);
                if (_extraCookie != null) headers.Add("Set-Cookie", _extraCookie);
                if (_foldResponseHeaders)
                {
                    using var native = new System.Net.Http.HttpResponseMessage();
                    native.Headers.TryAddWithoutValidation("Set-Cookie", headers.GetValues("Set-Cookie"));
                    headers = new HttpHeader(native.Headers);
                }
                return new HttpResponse(_responseUrl == null ? request : new HttpRequest(_responseUrl), headers, "{\"items\":[]}", _responseStatus);
            });
            _client = new BandcampHttpClient(_http.Object, _logger);
            _api = new BandcampApiClient(_client, _logger);
        }

        private DownloadClientDefinition Definition() => new() { Id = 7, Name = "Fan", Implementation = nameof(BandcampDownloadClient), Settings = JsonConvert.DeserializeObject<BandcampDownloadSettings>(JsonConvert.SerializeObject(_saved))! };
        private BandcampSessionProviderRegistry Registry() => new(_api, null, new BandcampSessionStore(_repository.Object), _logger, _repository.Object);

        [Fact]
        public async Task AuthenticatedRotationIsValidatedSavedMinimallyAndSurvivesProcessReread()
        {
            var provider = Registry().GetNamed("Fan");
            var session = await provider.GetSessionAsync();
            using var observation = _api.ObserveSession(session!);
            await _api.GetCollectionAsync(session!.Cookies, session.FanId);
            Assert.Equal("identity=rotated", _saved.Cookies);
            Assert.Equal("identity=rotated", (await provider.GetSessionAsync())!.Cookies);
            Assert.Equal("identity=rotated", (await Registry().GetNamed("Fan").GetSessionAsync())!.Cookies);
            Assert.Equal(1, _writes);
            Assert.Equal(3, _validations);
        }

        [Fact]
        public async Task ValidRotationFromAnotherFanCannotSwitchTheSharedOwner()
        {
            var registry = Registry();
            var search = registry.GetNamed("Fan");
            var download = registry.Get(8, new BandcampDownloadSettings { AuthConfiguration = "Fan" });
            var session = (await search.GetSessionAsync())!;
            _otherFan = true;
            using (_api.ObserveSession(session)) await _api.GetCollectionAsync(session.Cookies, session.FanId);
            Assert.Equal(BandcampSessionProblem.WrongAccount, search.State.Problem);
            Assert.Equal(search.State.Problem, download.State.Problem);
            Assert.Equal(session.FanId, (await download.GetSessionAsync())!.FanId);
            Assert.Equal("identity=prior", (await download.GetSessionAsync())!.Cookies);
            Assert.Equal("identity=prior", (await Registry().GetNamed("Fan").GetSessionAsync())!.Cookies);
            Assert.Equal(0, _writes);
        }

        [Theory]
        [InlineData("identity=prior; Path=/; Secure")]
        [InlineData("tracking=unrelated; Domain=.bandcamp.com; Path=/; Secure")]
        [InlineData("identity=; Path=/; Secure")]
        [InlineData("identity=bad value; Path=/; Secure")]
        [InlineData("identity=bad,value; Path=/; Secure")]
        [InlineData("identity=rotated; Domain=artist.bandcamp.com; Path=/; Secure")]
        [InlineData("identity=rotated; Domain=bandcamp.com.evil.test; Path=/; Secure")]
        [InlineData("identity=rotated; Path=/api; Secure")]
        [InlineData("identity=rotated; Secure")]
        [InlineData("identity=rotated; Path=/")]
        [InlineData("identity=rotated; Path=/; Secure; Domain=bandcamp.com; Domain=.bandcamp.com")]
        [InlineData("identity=rotated; Path=/; Secure; Max-Age=0")]
        [InlineData("identity=rotated; Path=/; Secure; Max-Age=invalid")]
        [InlineData("identity=rotated; Path=/; Secure; Expires=invalid")]
        [InlineData("identity=rotated; Path=/; Secure; Expires=Thu, 01 Jan 1970 00:00:00 GMT")]
        [InlineData("identity=rotated; Path=/; Secure; HttpOnly=invalid")]
        [InlineData("identity=rotated; Path=/; Secure; SameSite=invalid")]
        [InlineData("identity=rotated; Path=/; Secure; Unknown=attribute")]
        [InlineData("identity=rotated\n; Path=/; Secure")]
        [InlineData("identity=rotated; Path=/; Secure=")]
        [InlineData("identity=rotated; Path=/; Secure; HttpOnly=")]
        public async Task UnchangedUnrelatedOrRejectedHeadersLeaveTheValidatedIdentityUntouched(string header)
        {
            _rotation = header;
            var provider = Registry().GetNamed("Fan");
            var session = await provider.GetSessionAsync();
            using var observation = _api.ObserveSession(session!);
            await _api.GetCollectionAsync(session!.Cookies, session.FanId);
            Assert.Equal("identity=prior", (await provider.GetSessionAsync())!.Cookies);
            Assert.Equal("identity=prior", (await Registry().GetNamed("Fan").GetSessionAsync())!.Cookies);
            Assert.Equal(0, _writes);
            Assert.Equal(2, _validations);
            Assert.Equal(BandcampSessionStatus.Valid, provider.State.Status);
        }

        [Theory]
        [InlineData("http://bandcamp.com/api", false)]
        [InlineData("https://artist.bandcamp.com/api", false)]
        [InlineData("https://bcbits.com/archive", true)]
        [InlineData("https://bandcamp.com.evil.test/api", false)]
        [InlineData("https://bandcamp.com:444/api", false)]
        public async Task UnauthorizedOriginsReceiveNoIdentityAndCannotRotateIt(string url, bool raw)
        {
            var provider = Registry().GetNamed("Fan");
            using var observation = _api.ObserveSession((await provider.GetSessionAsync())!);
            var request = _client.CreateRequestBuilder(url, "identity=prior").Build();
            if (raw) await _client.ExecuteRawAsync(request);
            else await _client.ExecuteAsync(request);
            Assert.Null(request.Headers.GetSingleValue("Cookie"));
            Assert.Equal("identity=prior", (await provider.GetSessionAsync())!.Cookies);
            Assert.Equal(0, _writes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AuthenticatedRequestsDoNotUseHostCookieStorageOrAutomaticRedirects(bool raw)
        {
            var provider = Registry().GetNamed("Fan");
            using var observation = _api.ObserveSession((await provider.GetSessionAsync())!);
            var request = _client.CreateRequestBuilder("https://bandcamp.com/download", "identity=prior").Build();
            if (raw) await _client.ExecuteRawAsync(request);
            else await _client.ExecuteAsync(request);
            Assert.False(request.AllowAutoRedirect);
            Assert.False(request.StoreRequestCookie);
            Assert.False(request.StoreResponseCookie);
            Assert.Equal("identity=rotated", (await provider.GetSessionAsync())!.Cookies);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailedCandidateRetainsPriorIdentityInBothConsumersAndAfterReread(bool storageFailure)
        {
            var registry = Registry();
            var search = registry.GetNamed("Fan");
            var download = registry.Get(8, new BandcampDownloadSettings { AuthConfiguration = "Fan" });
            var session = await search.GetSessionAsync();
            using var observation = _api.ObserveSession(session!);
            _candidateValid = storageFailure;
            _storageFails = storageFailure;
            await _api.GetCollectionAsync(session!.Cookies, session.FanId);
            Assert.Equal(storageFailure ? BandcampSessionProblem.StorageFailure : BandcampSessionProblem.CookieExpired, search.State.Problem);
            Assert.Equal(search.State.Problem, download.State.Problem);
            Assert.Equal("identity=prior", (await download.GetSessionAsync())!.Cookies);
            Assert.Equal("identity=prior", (await Registry().GetNamed("Fan").GetSessionAsync())!.Cookies);
            Assert.Equal(0, _writes);
        }

        [Fact]
        public async Task ConcurrentReplacementCannotBeOverwrittenByAnOlderResponse()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _candidateDelay = () => { entered.TrySetResult(); return release.Task; };
            var provider = Registry().GetNamed("Fan");
            var session = await provider.GetSessionAsync();
            using var observation = _api.ObserveSession(session!);
            var pending = _api.GetCollectionAsync(session!.Cookies, session.FanId);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(await provider.ReplaceIdentityAsync("identity=newer"));
            release.SetResult();
            await pending;
            Assert.Equal(BandcampSessionProblem.ReplacementConflict, provider.State.Problem);
            Assert.Equal("identity=newer", (await provider.GetSessionAsync())!.Cookies);
            Assert.Equal("identity=newer", (await Registry().GetNamed("Fan").GetSessionAsync())!.Cookies);
            Assert.Equal(1, _writes);
        }

        [Theory]
        [InlineData(401)]
        [InlineData(403)]
        [InlineData(429)]
        [InlineData(500)]
        [InlineData(302)]
        public async Task UnsuccessfulResponsesCannotAuthorizeRotation(int status)
        {
            _responseStatus = (HttpStatusCode)status;
            var provider = Registry().GetNamed("Fan");
            using var observation = _api.ObserveSession((await provider.GetSessionAsync())!);
            var request = _client.CreateRequestBuilder("https://bandcamp.com/download", "identity=prior").Build();
            request.SuppressHttpError = true;
            if (status == 429)
            {
                await Assert.ThrowsAsync<BandcampRateLimitedException>(() => _client.ExecuteAsync(request));
                Assert.Null(await provider.GetSessionAsync());
                Assert.Equal("identity=prior", _saved.Cookies);
            }
            else
            {
                await _client.ExecuteAsync(request);
                Assert.Equal("identity=prior", (await provider.GetSessionAsync())!.Cookies);
            }
            Assert.Equal(0, _writes);
        }

        [Fact]
        public async Task ResponseFromAnUnauthorizedOriginCannotRotateAnAuthenticatedRequest()
        {
            _responseUrl = "https://artist.bandcamp.com/download";
            var provider = Registry().GetNamed("Fan");
            using var observation = _api.ObserveSession((await provider.GetSessionAsync())!);
            await _api.GetCollectionAsync("identity=prior", 12345678);
            Assert.Equal("identity=prior", (await provider.GetSessionAsync())!.Cookies);
            Assert.Equal(0, _writes);
        }

        [Fact]
        public async Task UnvalidatedIdentityCannotAuthorizeRotation()
        {
            var provider = Registry().GetNamed("Fan");
            await _api.GetCollectionAsync("identity=prior", 12345678);
            Assert.Equal(0, _writes);
            Assert.Equal("identity=prior", (await provider.GetSessionAsync())!.Cookies);
        }

        [Theory]
        [InlineData("tracking=unrelated; Path=/; Secure", "identity=rotated")]
        [InlineData("identity=ambiguous; Path=/; Secure", "identity=prior")]
        public async Task MultipleResponseCookiesNeverBroadenTheStoredIdentity(string extraCookie, string expected)
        {
            _extraCookie = extraCookie;
            var provider = Registry().GetNamed("Fan");
            using var observation = _api.ObserveSession((await provider.GetSessionAsync())!);
            await _api.GetCollectionAsync("identity=prior", 12345678);
            Assert.Equal(expected, (await Registry().GetNamed("Fan").GetSessionAsync())!.Cookies);
        }

        [Fact]
        public async Task SuccessfullyValidatedHomepageCanRotateTheIdentityWithoutRecursiveRotation()
        {
            _homepageRotation = true;
            var provider = Registry().GetNamed("Fan");
            Assert.Equal("identity=rotated", (await provider.GetSessionAsync())!.Cookies);
            Assert.Equal("identity=rotated", (await Registry().GetNamed("Fan").GetSessionAsync())!.Cookies);
            Assert.Equal(1, _writes);
            Assert.Equal(3, _validations);
        }

        [Fact]
        public void CredentialBearingUrlsAreRejectedByTheHostRequestBoundary()
        {
            Assert.Throws<ArgumentException>(() => _client.CreateRequestBuilder("https://user@bandcamp.com/api", "identity=prior"));
        }

        [Fact]
        public async Task RotationBelongsOnlyToTheSelectedOwnerEvenWhenAnotherOwnerHasTheSameCookie()
        {
            var other = new DownloadClientDefinition { Id = 8, Name = "Other", Implementation = nameof(BandcampDownloadClient), Settings = new BandcampDownloadSettings { Cookies = "identity=prior" } };
            _repository.Setup(r => r.All()).Returns(() => new[] { Definition(), other }.ToList());
            _repository.Setup(r => r.Find(8)).Returns(other);
            _repository.Setup(r => r.SetFields(It.Is<DownloadClientDefinition>(d => d.Id == 8), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Callback<DownloadClientDefinition, Expression<Func<DownloadClientDefinition, object>>[]>((d, _) => other.Settings = d.Settings);
            var registry = Registry();
            var selected = await registry.GetNamed("Fan").GetSessionAsync();
            await registry.GetNamed("Other").GetSessionAsync();
            using var observation = _api.ObserveSession(selected!);
            await _api.GetCollectionAsync(selected!.Cookies, selected.FanId);
            Assert.Equal("identity=rotated", (await registry.GetNamed("Fan").GetSessionAsync())!.Cookies);
            Assert.Equal("identity=prior", (await registry.GetNamed("Other").GetSessionAsync())!.Cookies);
        }

        [Fact]
        public async Task CancelledRotationRetainsPriorIdentityWithoutPublishingCandidate()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _candidateDelay = () => { entered.TrySetResult(); return release.Task; };
            using var cancellation = new CancellationTokenSource();
            var provider = Registry().GetNamed("Fan");
            var session = await provider.GetSessionAsync();
            using var observation = _api.ObserveSession(session!, cancellation.Token);
            var pending = _api.GetCollectionAsync(session!.Cookies, session.FanId);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal("identity=prior", (await provider.GetSessionAsync())!.Cookies);
            Assert.Equal("identity=prior", (await Registry().GetNamed("Fan").GetSessionAsync())!.Cookies);
            Assert.Equal(0, _writes);
        }

        [Fact]
        public async Task ResponseOutsideTheSelectedSessionScopeCannotRotateItsIdentity()
        {
            var provider = Registry().GetNamed("Fan");
            var session = await provider.GetSessionAsync();
            using (_api.ObserveSession(session!))
            {
                _rotation = null;
                await _api.GetCollectionAsync(session!.Cookies, session.FanId);
            }

            _rotation = "identity=rotated; Path=/; Secure";
            await _api.GetCollectionAsync(session!.Cookies, session.FanId);
            Assert.Equal("identity=prior", (await provider.GetSessionAsync())!.Cookies);
            Assert.Equal(0, _writes);
        }

        [Fact]
        public async Task AmbiguousHostFoldedCookieHeadersAreRejectedWithoutGuessingCookieBoundaries()
        {
            _foldResponseHeaders = true;
            _extraCookie = "tracking=unrelated; Domain=.bandcamp.com; Path=/; Secure";
            var provider = Registry().GetNamed("Fan");
            using var observation = _api.ObserveSession((await provider.GetSessionAsync())!);
            await _api.GetCollectionAsync("identity=prior", 12345678);
            Assert.Equal("identity=prior", (await Registry().GetNamed("Fan").GetSessionAsync())!.Cookies);
            Assert.Equal(0, _writes);
        }
    }
}
