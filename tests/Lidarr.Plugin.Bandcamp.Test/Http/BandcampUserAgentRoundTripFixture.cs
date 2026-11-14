using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.Indexers.Bandcamp;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Http
{
    /// <summary>
    /// Regression for issue #21: the plugin's real requests (auth/download via
    /// BandcampHttpClient, and indexer search via BandcampRequestGenerator) carry a
    /// browser User-Agent. Lidarr's ManagedHttpDispatcher rejects them with
    /// "User-Agent other than Lidarr not allowed."; BandcampHttpDispatcher must send them
    /// over the wire with the browser User-Agent intact.
    /// </summary>
    public sealed class BandcampUserAgentRoundTripFixture : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly string _baseUrl;
        private string? _lastUserAgent;

        public BandcampUserAgentRoundTripFixture()
        {
            _baseUrl = $"http://127.0.0.1:{GetFreePort()}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(_baseUrl + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        [Fact]
        public async Task HttpClient_request_reaches_server_with_browser_user_agent()
        {
            var request = new BandcampHttpClient(Mock.Of<IHttpClient>(), _logger)
                .CreateRequestBuilder(_baseUrl + "/", "identity=abc")
                .Build();

            await AssertBrowserUserAgentReachesServer(request);
        }

        [Fact]
        public async Task Indexer_search_request_reaches_server_with_browser_user_agent()
        {
            var settings = new BandcampIndexerSettings { BaseUrl = _baseUrl, Cookies = "identity=abc" };
            var criteria = new ArtistSearchCriteria
            {
                Artist = new Artist { Metadata = new ArtistMetadata { Name = "x" } }
            };
            var request = new BandcampRequestGenerator(settings, _logger)
                .GetSearchRequests(criteria).GetAllTiers().First().First();

            await AssertBrowserUserAgentReachesServer(request.HttpRequest);
        }

        [Fact]
        public async Task Lidarr_built_in_dispatcher_rejects_the_same_request()
        {
            // Documents why BandcampHttpDispatcher exists: if this starts passing, Lidarr
            // has relaxed its User-Agent guard and the custom dispatcher may be removable.
            var request = new BandcampHttpClient(Mock.Of<IHttpClient>(), _logger)
                .CreateRequestBuilder(_baseUrl + "/", "identity=abc")
                .Build();

            var ex = await Assert.ThrowsAsync<NotSupportedException>(
                () => CreateDispatcher(builtIn: true).GetResponseAsync(request, new CookieContainer()));
            Assert.Contains("User-Agent other than Lidarr not allowed", ex.Message);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
        }

        private async Task AssertBrowserUserAgentReachesServer(HttpRequest request)
        {
            var response = await CreateDispatcher(builtIn: false).GetResponseAsync(request, new CookieContainer());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Mozilla/5.0", _lastUserAgent);
        }

        private ManagedHttpDispatcher CreateDispatcher(bool builtIn)
        {
            var userAgent = new Mock<IUserAgentBuilder>();
            userAgent.Setup(u => u.GetUserAgent(It.IsAny<bool>())).Returns("Lidarr/test");

            var proxy = Mock.Of<IHttpProxySettingsProvider>();
            var webProxy = Mock.Of<ICreateManagedWebProxy>();
            var certs = Mock.Of<ICertificateValidationService>();

            return builtIn
                ? new ManagedHttpDispatcher(proxy, webProxy, certs, userAgent.Object, new CacheManager(), _logger)
                : new BandcampHttpDispatcher(proxy, webProxy, certs, userAgent.Object, new CacheManager(), _logger);
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                _lastUserAgent = ctx.Request.UserAgent;
                var body = System.Text.Encoding.UTF8.GetBytes("<html></html>");
                ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.Close();
            }
        }

        private static int GetFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
