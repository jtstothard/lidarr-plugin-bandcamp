using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    /// <summary>
    /// Lidarr download client for Bandcamp. Uses cookie-based authentication to
    /// resolve purchased albums and download them as FLAC archives.
    ///
    /// Lifecycle:
    ///   Download() — enqueues a download via the background task queue
    ///   GetItems() — reports current queue state to Lidarr for import tracking
    ///   RemoveItem() — cancels/removes a download and optionally deletes data
    ///   GetStatus() — reports output root folder for Lidarr's completed download monitoring
    ///   Test() — validates cookie auth (fan_id resolution) and download path accessibility
    ///
    /// All credential-safe logging is inherited from BandcampHttpClient.
    /// </summary>
    public class BandcampDownloadClient : DownloadClientBase<BandcampDownloadSettings>
    {
        private readonly IBandcampDownloadQueue _taskQueue;
        private readonly BandcampApiClient _apiClient;

        public override string Name => "Bandcamp";
        public override string Protocol => nameof(BandcampDownloadProtocol);

        public BandcampDownloadClient(
            IBandcampDownloadQueue taskQueue,
            BandcampApiClient apiClient,
            IConfigService configService,
            IDiskProvider diskProvider,
            IRemotePathMappingService remotePathMappingService,
            ILocalizationService localizationService,
            Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _taskQueue = taskQueue;
            _apiClient = apiClient;
        }

        /// <summary>
        /// Enqueues an album download from Bandcamp. Extracts the album URL from the
        /// release's DownloadUrl (set by the Bandcamp indexer), creates a BandcampDownloadItem,
        /// and enqueues it for background processing via the DownloadTaskQueue.
        /// </summary>
        /// <returns>The download ID for tracking.</returns>
        public override async Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            var albumUrl = remoteAlbum.Release.DownloadUrl;

            if (string.IsNullOrWhiteSpace(albumUrl))
            {
                _logger.Debug("Bandcamp download client: Release has no DownloadUrl — cannot enqueue download");
                throw new DownloadException("Cannot download: release has no Bandcamp album URL.");
            }

            var title = remoteAlbum.Release.Title ?? remoteAlbum.Release.Album ?? albumUrl;

            // Build the download directory: DownloadPath / Artist - Album (sanitized)
            var downloadPath = Settings.DownloadPath;
            var albumDir = MakeValidDirectoryName(title);
            var outputPath = System.IO.Path.Combine(downloadPath, albumDir);

            var item = new BandcampDownloadItem
            {
                AlbumUrl = albumUrl,
                Title = title,
                OutputPath = outputPath,
                Cookies = Settings.Cookies
            };

            var downloadId = await _taskQueue.EnqueueAsync(item).ConfigureAwait(false);

            _logger.Debug("Bandcamp download client: Enqueued download {0} for '{1}' -> {2}",
                downloadId, title, outputPath);

            return downloadId;
        }

        /// <summary>
        /// Returns all tracked download items mapped to Lidarr's DownloadClientItem format.
        /// Lidarr polls this to check download progress and trigger import on completion.
        /// </summary>
        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var items = _taskQueue.GetItems();

            foreach (var kvp in items)
            {
                var bcItem = kvp.Value;

                var clientItem = new DownloadClientItem
                {
                    DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                    DownloadId = bcItem.DownloadId,
                    Title = bcItem.Title,
                    TotalSize = bcItem.TotalSize,
                    RemainingSize = bcItem.TotalSize > 0
                        ? bcItem.TotalSize - bcItem.DownloadedBytes
                        : 0,
                    OutputPath = new OsPath(bcItem.OutputPath),
                    Status = MapStatus(bcItem.Status),
                    Message = bcItem.ErrorMessage,
                    CanBeRemoved = bcItem.Status == BandcampDownloadStatus.Completed ||
                                   bcItem.Status == BandcampDownloadStatus.Failed,
                    CanMoveFiles = bcItem.Status == BandcampDownloadStatus.Completed
                };

                yield return clientItem;
            }
        }

        /// <summary>
        /// Removes a download item from tracking. If deleteData is true, also deletes
        /// the downloaded files from disk using Lidarr's built-in DeleteItemData helper.
        /// </summary>
        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            _logger.Debug("Bandcamp download client: Removing item {0} (deleteData={1})",
                item.DownloadId, deleteData);

            // Remove from the shared queue/registry state
            _taskQueue.RemoveItem(item.DownloadId);

            if (deleteData)
            {
                DeleteItemData(item);
            }
        }

        /// <summary>
        /// Reports the download client status to Lidarr. Bandcamp downloads are local
        /// (IsLocalhost=true) and output to the configured DownloadPath.
        /// Lidarr uses OutputRootFolders to monitor for completed downloads.
        /// </summary>
        public override DownloadClientInfo GetStatus()
        {
            var outputPath = Settings.DownloadPath;

            return new DownloadClientInfo
            {
                IsLocalhost = true,
                OutputRootFolders = new List<OsPath>
                {
                    new(outputPath)
                }
            };
        }

        /// <summary>
        /// Validates the download client configuration:
        /// 1. Tests cookie auth by attempting fan_id resolution
        /// 2. Validates the download path exists and is writable
        /// </summary>
        protected override void Test(List<ValidationFailure> failures)
        {
            _logger.Debug("Bandcamp download client: Running connection test");

            // Test cookie auth — resolve fan_id to verify cookies are valid
            try
            {
                var fanId = _apiClient.ResolveFanIdAsync(Settings.Cookies).GetAwaiter().GetResult();

                if (fanId == null)
                {
                    failures.Add(new ValidationFailure("Cookies",
                        "Could not verify session cookies. Make sure you're pasting the 'identity' cookie " +
                        "value from your browser's Bandcamp cookies (not the full cookie header). " +
                        "The cookie value should be a long string starting with '%22' or containing 't%3D'."));
                    _logger.Debug("Bandcamp download client: Cookie auth test failed — fan_id could not be resolved");
                }
                else
                {
                    _logger.Debug("Bandcamp download client: Cookie auth test passed — fan_id resolved");
                }
            }
            catch (Exception ex)
            {
                failures.Add(new ValidationFailure("Cookies",
                    "Failed to authenticate with Bandcamp: " + ex.Message));
                _logger.Debug(ex, "Bandcamp download client: Cookie auth test threw exception");
            }

            // Test download path — create it if needed, then verify it is writable.
            if (!EnsureDownloadPathExists(failures))
            {
                return;
            }

            var pathFailure = TestFolder(Settings.DownloadPath, "DownloadPath", mustBeWritable: true);
            if (pathFailure != null)
            {
                failures.Add(pathFailure);
            }
            else
            {
                _logger.Debug("Bandcamp download client: Download path test passed");
            }
        }

        /// <summary>
        /// Creates the configured root download path if it does not already exist.
        /// This mirrors the behavior users expect from local download clients: a
        /// missing child folder under an existing mounted path should be created
        /// instead of failing validation with "Folder does not exist".
        /// </summary>
        private bool EnsureDownloadPathExists(List<ValidationFailure> failures)
        {
            if (string.IsNullOrWhiteSpace(Settings.DownloadPath))
            {
                failures.Add(new ValidationFailure("DownloadPath", "Download path is required"));
                return false;
            }

            if (_diskProvider.FolderExists(Settings.DownloadPath))
            {
                return true;
            }

            try
            {
                _logger.Debug("Bandcamp download client: Creating missing download path '{0}'", Settings.DownloadPath);
                _diskProvider.CreateFolder(Settings.DownloadPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Bandcamp download client: Failed to create download path '{0}'", Settings.DownloadPath);
                failures.Add(new ValidationFailure("DownloadPath", "Unable to create download path: " + ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Maps Bandcamp download status to Lidarr's DownloadItemStatus.
        /// </summary>
        private static DownloadItemStatus MapStatus(BandcampDownloadStatus status)
        {
            return status switch
            {
                BandcampDownloadStatus.Queued => DownloadItemStatus.Queued,
                BandcampDownloadStatus.Resolving => DownloadItemStatus.Downloading,
                BandcampDownloadStatus.Downloading => DownloadItemStatus.Downloading,
                BandcampDownloadStatus.Extracting => DownloadItemStatus.Downloading,
                BandcampDownloadStatus.Completed => DownloadItemStatus.Completed,
                BandcampDownloadStatus.Failed => DownloadItemStatus.Failed,
                _ => DownloadItemStatus.Queued
            };
        }

        /// <summary>
        /// Creates a filesystem-safe directory name from a release title.
        /// Strips characters invalid on Windows/Linux and collapses whitespace.
        /// </summary>
        private static string MakeValidDirectoryName(string title)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var name = title;

            foreach (var c in invalid)
            {
                name = name.Replace(c, ' ');
            }

            // Also remove characters invalid in directory names but not in GetInvalidFileNameChars
            name = name.Replace('/', ' ').Replace('\\', ' ').Replace(':', ' ');

            // Collapse whitespace and trim
            while (name.Contains("  "))
            {
                name = name.Replace("  ", " ");
            }

            return name.Trim();
        }
    }
}
