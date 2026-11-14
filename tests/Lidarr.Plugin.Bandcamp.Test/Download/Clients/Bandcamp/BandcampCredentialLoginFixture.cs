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
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Download.Clients.Bandcamp.Session;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
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
        private const string FreshIdentity = "7%09fresh-identity%09%7B%22ex%22%3A0%7D";

        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly Mock<IHttpClient> _http = new();
        private readonly Mock<IDownloadClientRepository> _repository = new();
        private readonly ConcurrentQueue<string> _homepageCookies = new();
        private int _loginPosts;
        private string _loginCbFixture = "login_cb_success.json";
        private Func<Task>? _homepageDelay;

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

                        var page = cookie.Contains("fresh-identity") ? "homepage_logged_in.html" : "homepage_logged_out.html";
                        return new HttpResponse(request, new HttpHeader(), LoginFixtures.Read(page), HttpStatusCode.OK);
                    }

                    if (url == "https://bandcamp.com/login_cb")
                    {
                        Interlocked.Increment(ref _loginPosts);
                        var headers = new HttpHeader();
                        if (_loginCbFixture == "login_cb_success.json")
                        {
                            headers.Add("Set-Cookie", $"identity={FreshIdentity}; domain=.bandcamp.com; path=/; secure; HttpOnly");
                        }

                        return new HttpResponse(request, headers, LoginFixtures.Read(_loginCbFixture), HttpStatusCode.OK);
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
                _logger);
        }

        private static BandcampDownloadSettings Settings(string cookies = "", string email = "fan@example.com", string password = "hunter2")
        {
            return new BandcampDownloadSettings
            {
                Cookies = cookies,
                Email = email,
                Password = password,
                DownloadPath = "/tmp/bandcamp"
            };
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
    }
}
