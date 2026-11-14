using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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
    public class BandcampDownloadClientQueueFixture
    {
        private const string LiveHomepage =
            @"<html><body><div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:12345678,&quot;isLoggedIn&quot;:true}}}""></div></body></html>";

        [Fact]
        public async Task Download_QueuedByOneClient_IsVisibleFromAnotherClientInstance()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var httpClient = new Mock<IHttpClient>();
            httpClient
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync((HttpRequest request) =>
                    new HttpResponse(request, new HttpHeader(), LiveHomepage, System.Net.HttpStatusCode.OK));
            var apiClient = new BandcampApiClient(new BandcampHttpClient(httpClient.Object, logger), logger);
            var registry = new BandcampDownloadRegistry();
            var proxy = new BlockingProxy();
            using var queue = new DownloadTaskQueue(proxy, registry, logger);

            var client1 = CreateClient(queue, apiClient, logger);
            var client2 = CreateClient(queue, apiClient, logger);

            var remoteAlbum = new RemoteAlbum
            {
                Release = new ReleaseInfo
                {
                    Title = "Fresh - Fresh [FLAC]",
                    Album = "Fresh",
                    DownloadUrl = "https://bandcamp.com/download?type=album&id=111#format=flac"
                }
            };

            var downloadId = await client1.Download(remoteAlbum, Mock.Of<IIndexer>());

            var items = client2.GetItems().ToList();

            var item = Assert.Single(items.Where(i => i.DownloadId == downloadId));
            Assert.Equal("Fresh - Fresh [FLAC]", item.Title);
            Assert.True(item.Status == DownloadItemStatus.Queued || item.Status == DownloadItemStatus.Downloading);

            proxy.Release();
        }

        [Fact]
        public async Task Download_SessionCookieChangedAfterGrab_QueuedItemRunsWithCurrentCookie()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var httpClient = new Mock<IHttpClient>();
            var firstRequestGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloadPageCookies = new ConcurrentQueue<string>();
            var downloadPageCalls = 0;

            httpClient
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .Returns(async (HttpRequest request) =>
                {
                    if (request.Url.FullUri == "https://bandcamp.com")
                    {
                        return new HttpResponse(request, new HttpHeader(), LiveHomepage, System.Net.HttpStatusCode.OK);
                    }

                    if (request.Url.FullUri.StartsWith("https://bandcamp.com/download"))
                    {
                        downloadPageCookies.Enqueue(request.Headers.GetSingleValue("Cookie"));
                        if (Interlocked.Increment(ref downloadPageCalls) == 1)
                        {
                            await firstRequestGate.Task;
                        }
                    }

                    // No download data on the page, so each item fails after using its session.
                    return new HttpResponse(request, new HttpHeader(), "<html></html>", System.Net.HttpStatusCode.OK);
                });

            var bandcampHttp = new BandcampHttpClient(httpClient.Object, logger);
            var apiClient = new BandcampApiClient(bandcampHttp, logger);
            var sessions = new BandcampSessionProviderRegistry(apiClient, null, null, logger);
            var proxy = new BandcampDownloadProxy(apiClient, bandcampHttp, logger);
            using var queue = new DownloadTaskQueue(proxy, new BandcampDownloadRegistry(), logger);

            var client = CreateClient(queue, apiClient, logger, sessions, "identity=old-cookie");

            await client.Download(CreateRemoteAlbum("111"), Mock.Of<IIndexer>());
            await WaitUntil(() => Volatile.Read(ref downloadPageCalls) == 1);
            await client.Download(CreateRemoteAlbum("222"), Mock.Of<IIndexer>());

            // Session renewed (e.g. user pasted a fresh cookie) while the second item is still queued.
            ((BandcampDownloadSettings)client.Definition.Settings).Cookies = "identity=new-cookie";
            firstRequestGate.SetResult(true);

            await WaitUntil(() => client.GetItems().Count(i => i.Status == DownloadItemStatus.Failed) == 2);

            // Item 1 starts on the old cookie, then renews once (#28) and retries on the new one; item 2 runs on the new one.
            Assert.Equal(new[] { "identity=old-cookie", "identity=new-cookie", "identity=new-cookie" }, downloadPageCookies.ToArray());
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

        private static async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Condition not met in time");
                }

                await Task.Delay(20);
            }
        }

        private static BandcampDownloadClient CreateClient(IBandcampDownloadQueue queue, BandcampApiClient apiClient, Logger logger)
        {
            return CreateClient(queue, apiClient, logger, new BandcampSessionProviderRegistry(apiClient, null, null, logger), "identity=testcookie");
        }

        private static BandcampDownloadClient CreateClient(
            IBandcampDownloadQueue queue,
            BandcampApiClient apiClient,
            Logger logger,
            IBandcampSessionProviderRegistry sessions,
            string cookies)
        {
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
                logger);

            client.Definition = new DownloadClientDefinition
            {
                Id = 1,
                Name = "Bandcamp",
                Settings = new BandcampDownloadSettings
                {
                    Cookies = cookies,
                    DownloadPath = "/tmp/bandcamp"
                }
            };

            return client;
        }

        private sealed class BlockingProxy : IBandcampDownloadProxy
        {
            private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Release()
            {
                _gate.TrySetResult(true);
            }

            public async Task ExecuteDownloadAsync(BandcampDownloadItem item, CancellationToken cancellationToken)
            {
                item.Status = BandcampDownloadStatus.Resolving;
                item.Phase = "resolving";
                await _gate.Task.WaitAsync(cancellationToken);
                item.Status = BandcampDownloadStatus.Completed;
                item.Phase = "completed";
                item.CompletedAt = DateTime.UtcNow;
            }
        }
    }
}
