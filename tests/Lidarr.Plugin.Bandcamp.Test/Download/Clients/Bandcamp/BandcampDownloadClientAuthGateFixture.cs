using System;
using System.Linq;
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
    public class BandcampDownloadClientAuthGateFixture
    {
        private const string LiveHomepage =
            @"<html><body><div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:12345678,&quot;isLoggedIn&quot;:true}}}""></div></body></html>";

        private const string DeadHomepage =
            @"<html><body><div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:null,&quot;isLoggedIn&quot;:false}}}""></div></body></html>";

        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private int _homepageCalls;
        private string _homepage = LiveHomepage;
        private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task Download_DeadCookie_ThrowsClientLevelFailureAndEnqueuesNothing()
        {
            _homepage = DeadHomepage;
            var (client, queue) = CreateClient();
            using var _ = queue;

            await Assert.ThrowsAsync<DownloadClientAuthenticationException>(
                () => client.Download(CreateRemoteAlbum("111"), Mock.Of<IIndexer>()));

            Assert.Empty(client.GetItems());
        }

        [Fact]
        public async Task Download_ValidSession_EnqueuesItem()
        {
            var (client, queue) = CreateClient();
            using var _ = queue;

            var downloadId = await client.Download(CreateRemoteAlbum("111"), Mock.Of<IIndexer>());

            Assert.Single(client.GetItems().Where(i => i.DownloadId == downloadId));
        }

        [Fact]
        public async Task Download_ValidationSucceeded_IsReusedFor15Minutes()
        {
            var (client, queue) = CreateClient();
            using var _ = queue;

            await client.Download(CreateRemoteAlbum("111"), Mock.Of<IIndexer>());
            _now = _now.AddMinutes(14);
            await client.Download(CreateRemoteAlbum("222"), Mock.Of<IIndexer>());
            Assert.Equal(1, _homepageCalls);

            _now = _now.AddMinutes(2);
            await client.Download(CreateRemoteAlbum("333"), Mock.Of<IIndexer>());
            Assert.Equal(2, _homepageCalls);
        }

        private (BandcampDownloadClient Client, DownloadTaskQueue Queue) CreateClient()
        {
            var httpClient = new Mock<IHttpClient>();
            httpClient
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .Returns((HttpRequest request) =>
                {
                    if (request.Url.FullUri == "https://bandcamp.com")
                    {
                        Interlocked.Increment(ref _homepageCalls);
                        return Task.FromResult(new HttpResponse(request, new HttpHeader(), _homepage, System.Net.HttpStatusCode.OK));
                    }

                    return Task.FromResult(new HttpResponse(request, new HttpHeader(), "{}", System.Net.HttpStatusCode.OK));
                });

            var apiClient = new BandcampApiClient(new BandcampHttpClient(httpClient.Object, _logger), _logger);
            var sessions = new BandcampSessionProviderRegistry(apiClient, _logger, () => _now);
            var queue = new DownloadTaskQueue(new IdleProxy(), new BandcampDownloadRegistry(), _logger);

            var client = new BandcampDownloadClient(
                queue,
                sessions,
                Mock.Of<IAlbumService>(),
                Mock.Of<IReleaseService>(),
                Mock.Of<ITrackService>(),
                Mock.Of<IConfigService>(),
                Mock.Of<IDiskProvider>(),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ILocalizationService>(),
                _logger);

            client.Definition = new DownloadClientDefinition
            {
                Id = 1,
                Name = "Bandcamp",
                Settings = new BandcampDownloadSettings { Cookies = "identity=testcookie", DownloadPath = "/tmp/bandcamp" }
            };

            return (client, queue);
        }

        private static RemoteAlbum CreateRemoteAlbum(string id)
        {
            return new RemoteAlbum
            {
                Release = new ReleaseInfo
                {
                    Title = $"Album {id} [FLAC]",
                    Album = $"Album {id}",
                    DownloadUrl = $"https://bandcamp.com/download?type=album&id={id}#format=flac"
                }
            };
        }

        private sealed class IdleProxy : IBandcampDownloadProxy
        {
            public Task ExecuteDownloadAsync(BandcampDownloadItem item, CancellationToken cancellationToken)
            {
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }
}
