using System;
using System.IO;
using System.Linq;
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
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    /// <summary>
    /// A session that dies after Phase 1 is renewed once and the item retried from Phase 1.
    /// </summary>
    public class BandcampDownloadClientSessionRenewalFixture : IDisposable
    {
        private const string OldCookie = "identity=old-cookie";
        private const string NewCookie = "identity=new-cookie";
        private const string DownloadPageUrl = "https://bandcamp.com/download?type=album&id=111";

        private const string ValidHomepage =
            @"<html><body><div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:12345678,&quot;isLoggedIn&quot;:true}}}""></div></body></html>";

        private const string DeadHomepage =
            @"<html><body><div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:null,&quot;isLoggedIn&quot;:false}}}""></div></body></html>";

        private const string DownloadPage =
            @"<html><head><script type=""text/javascript"">var pagedata = {
                ""digital_items"": [{
                    ""item_id"": 111,
                    ""item_type"": ""album"",
                    ""title"": ""Fresh"",
                    ""downloads"": {
                        ""flac"": { ""url"": ""https://bandcamp.com/statdownload/album/111?sig=flac"", ""size_mb"": 12 }
                    }
                }]
            };</script></head><body></body></html>";

        private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "bandcamp-renewal-tests", Guid.NewGuid().ToString("N"));
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private int _downloadPageRequests;

        [Fact]
        public async Task GetItems_SessionDiesAfterPhase1AndRenews_ItemRetriesAndCompletes()
        {
            BandcampDownloadClient client = null!;
            var homepageCallsWithOldCookie = 0;

            var http = Http((request, cookie) =>
            {
                switch (request.Url.FullUri)
                {
                    case "https://bandcamp.com":
                        if (cookie == OldCookie)
                        {
                            return Text(Interlocked.Increment(ref homepageCallsWithOldCookie) == 1 ? ValidHomepage : DeadHomepage, request);
                        }

                        return Text(ValidHomepage, request);
                    case DownloadPageUrl:
                        Interlocked.Increment(ref _downloadPageRequests);
                        if (cookie == OldCookie)
                        {
                            // The session dies mid-download; a fresh session becomes available.
                            Settings(client).Cookies = NewCookie;
                            return Text("<html></html>", request);
                        }

                        return Text(DownloadPage, request);
                    default:
                        return Files(request);
                }
            });

            client = CreateClient(http);

            var downloadId = await client.Download(CreateRemoteAlbum(), Mock.Of<IIndexer>());
            var item = await WaitForFinished(client, downloadId);

            Assert.Equal(DownloadItemStatus.Completed, item.Status);
            Assert.Equal(2, _downloadPageRequests);
        }

        [Fact]
        public async Task GetItems_RetryAfterRenewalAlsoFails_ItemFailsWithoutSecondRenewal()
        {
            BandcampDownloadClient client = null!;
            var homepageCallsWithOldCookie = 0;

            var http = Http((request, cookie) =>
            {
                switch (request.Url.FullUri)
                {
                    case "https://bandcamp.com":
                        if (cookie == OldCookie)
                        {
                            return Text(Interlocked.Increment(ref homepageCallsWithOldCookie) == 1 ? ValidHomepage : DeadHomepage, request);
                        }

                        return Text(ValidHomepage, request);
                    case DownloadPageUrl:
                        Interlocked.Increment(ref _downloadPageRequests);
                        if (cookie == OldCookie)
                        {
                            Settings(client).Cookies = NewCookie;
                        }

                        return Text("<html></html>", request);
                    default:
                        return Files(request);
                }
            });

            client = CreateClient(http);

            var downloadId = await client.Download(CreateRemoteAlbum(), Mock.Of<IIndexer>());
            var item = await WaitForFinished(client, downloadId);

            Assert.Equal(DownloadItemStatus.Failed, item.Status);
            Assert.Equal(2, _downloadPageRequests);
        }

        [Fact]
        public async Task GetItems_SessionDiesAfterPhase1AndCannotBeRenewed_ItemFails()
        {
            var homepageCalls = 0;

            var http = Http((request, cookie) =>
            {
                switch (request.Url.FullUri)
                {
                    case "https://bandcamp.com":
                        return Text(Interlocked.Increment(ref homepageCalls) == 1 ? ValidHomepage : DeadHomepage, request);
                    case DownloadPageUrl:
                        Interlocked.Increment(ref _downloadPageRequests);
                        return Text("<html></html>", request);
                    default:
                        return Files(request);
                }
            });

            var client = CreateClient(http);

            var downloadId = await client.Download(CreateRemoteAlbum(), Mock.Of<IIndexer>());
            var item = await WaitForFinished(client, downloadId);

            Assert.Equal(DownloadItemStatus.Failed, item.Status);
            Assert.Equal(1, _downloadPageRequests);
        }

        [Fact]
        public async Task GetItems_NonAuthFailureWithLiveSession_ItemFailsWithoutRetry()
        {
            var http = Http((request, cookie) =>
            {
                switch (request.Url.FullUri)
                {
                    case "https://bandcamp.com":
                        return Text(ValidHomepage, request);
                    case DownloadPageUrl:
                        Interlocked.Increment(ref _downloadPageRequests);
                        return Text("<html></html>", request);
                    default:
                        return Files(request);
                }
            });

            var client = CreateClient(http);

            var downloadId = await client.Download(CreateRemoteAlbum(), Mock.Of<IIndexer>());
            var item = await WaitForFinished(client, downloadId);

            Assert.Equal(DownloadItemStatus.Failed, item.Status);
            Assert.Equal(1, _downloadPageRequests);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }

        private static BandcampDownloadSettings Settings(BandcampDownloadClient client)
        {
            return (BandcampDownloadSettings)client.Definition.Settings;
        }

        private static Mock<IHttpClient> Http(Func<HttpRequest, string, HttpResponse> respond)
        {
            var http = new Mock<IHttpClient>();
            http
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync((HttpRequest request) => respond(request, request.Headers.GetSingleValue("Cookie")));
            return http;
        }

        private static HttpResponse Files(HttpRequest request)
        {
            return request.Url.FullUri switch
            {
                "https://bandcamp.com/statdownload/album/111?sig=flac&.format=flac&.json=true" =>
                    Text(@"{""url"":""https://files.example.com/fresh.flac""}", request),
                "https://files.example.com/fresh.flac" => new HttpResponse(
                    request,
                    new HttpHeader { { "Content-Type", "audio/flac" } },
                    new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22 },
                    HttpStatusCode.OK),
                _ => Text("{}", request)
            };
        }

        private static HttpResponse Text(string content, HttpRequest request)
        {
            return new HttpResponse(request, new HttpHeader(), content, HttpStatusCode.OK);
        }

        private static RemoteAlbum CreateRemoteAlbum()
        {
            return new RemoteAlbum
            {
                Release = new ReleaseInfo
                {
                    Title = "Fresh - Fresh [FLAC]",
                    Album = "Fresh",
                    DownloadUrl = DownloadPageUrl + "#format=flac"
                }
            };
        }

        private static async Task<DownloadClientItem> WaitForFinished(BandcampDownloadClient client, string downloadId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var item = client.GetItems().Single(i => i.DownloadId == downloadId);
                if (item.Status == DownloadItemStatus.Completed || item.Status == DownloadItemStatus.Failed)
                {
                    return item;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Download did not finish in time");
                }

                await Task.Delay(20);
            }
        }

        private BandcampDownloadClient CreateClient(Mock<IHttpClient> http)
        {
            var bandcampHttp = new BandcampHttpClient(http.Object, _logger);
            var apiClient = new BandcampApiClient(bandcampHttp, _logger);
            var proxy = new BandcampDownloadProxy(apiClient, bandcampHttp, _logger);
            var queue = new DownloadTaskQueue(proxy, new BandcampDownloadRegistry(), _logger);

            var client = new BandcampDownloadClient(
                queue,
                new BandcampSessionProviderRegistry(apiClient, _logger),
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
                Settings = new BandcampDownloadSettings
                {
                    Cookies = OldCookie,
                    DownloadPath = _tempRoot
                }
            };

            return client;
        }
    }
}
