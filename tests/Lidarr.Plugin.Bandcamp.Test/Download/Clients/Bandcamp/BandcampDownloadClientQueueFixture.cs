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
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    public class BandcampDownloadClientQueueFixture
    {
        [Fact]
        public async Task Download_QueuedByOneClient_IsVisibleFromAnotherClientInstance()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var httpClient = new Mock<IHttpClient>();
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

        private static BandcampDownloadClient CreateClient(IBandcampDownloadQueue queue, BandcampApiClient apiClient, Logger logger)
        {
            var client = new BandcampDownloadClient(
                queue,
                apiClient,
                Mock.Of<IConfigService>(),
                Mock.Of<IDiskProvider>(),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ILocalizationService>(),
                logger);

            client.Definition = new DownloadClientDefinition
            {
                Name = "Bandcamp",
                Settings = new BandcampDownloadSettings
                {
                    Cookies = "identity=testcookie",
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
