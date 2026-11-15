using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Bandcamp.Session;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
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
        private readonly IBandcampSessionProviderRegistry _sessionProviders;
        private readonly IAlbumService _albumService;
        private readonly IReleaseService _releaseService;
        private readonly ITrackService _trackService;

        public override string Name => "Bandcamp";
        public override string Protocol => nameof(BandcampDownloadProtocol);

        public BandcampDownloadClient(
            IBandcampDownloadQueue taskQueue,
            IBandcampSessionProviderRegistry sessionProviders,
            IAlbumService albumService,
            IReleaseService releaseService,
            ITrackService trackService,
            IConfigService configService,
            IDiskProvider diskProvider,
            IRemotePathMappingService remotePathMappingService,
            ILocalizationService localizationService,
            Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _taskQueue = taskQueue;
            _sessionProviders = sessionProviders;
            _albumService = albumService;
            _releaseService = releaseService;
            _trackService = trackService;
        }

        /// <summary>
        /// Gets this client definition's session provider, pointed at the current settings.
        /// </summary>
        private IBandcampSessionProvider SessionProvider => _sessionProviders.Get(Definition.Id, Settings);

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

            // Pre-grab auth gate: refuse at the client level (Lidarr's download-client failure
            // and backoff) rather than enqueue an item that would fail, be blocklisted and re-searched.
            var provider = SessionProvider;
            BandcampSession? session;
            try
            {
                session = await provider.GetSessionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new DownloadClientUnavailableException("Unable to validate the Bandcamp session", ex);
            }

            if (session == null)
            {
                _logger.Warn("Bandcamp download client: No valid Bandcamp session; refusing grab");
                var cause = provider.State.Problem?.Description ?? "no usable session; set your email and password or a session cookie in the client settings";
                throw new DownloadClientAuthenticationException("Bandcamp session unusable: " + cause);
            }

            var title = remoteAlbum.Release.Title ?? remoteAlbum.Release.Album ?? albumUrl;
            var downloadId = Guid.NewGuid().ToString("N");
            var retagContext = BuildRetagContext(remoteAlbum);

            // Build a unique working directory under the configured download root so retries
            // and concurrent grabs never delete or overwrite each other's extracted files.
            var downloadPath = Settings.DownloadPath;
            var folderTitle = retagContext != null
                ? $"{retagContext.ArtistName} - {retagContext.AlbumTitle}"
                : title;
            var albumDir = MakeValidDirectoryName(folderTitle);
            var outputPath = Path.Combine(downloadPath, $"{albumDir}-{downloadId[..8]}");

            var item = new BandcampDownloadItem
            {
                DownloadId = downloadId,
                AlbumUrl = albumUrl,
                Title = title,
                OutputPath = outputPath,
                SessionProvider = SessionProvider,
                RetagContext = retagContext
            };

            await _taskQueue.EnqueueAsync(item).ConfigureAwait(false);

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

        // Host-authenticated action: candidate travels only in the masked Cookies body field.
        // Resolve the saved named owner, never apply proposed settings or call password login.
        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "sessionStatus")
            {
                var owner = _sessionProviders.GetNamed(Definition.Name);
                if (Definition.Id <= 0 || owner.DefinitionId != Definition.Id)
                    return new { Success = false, Problem = "ConfigurationMissing" };
                // Return only non-secret metadata. This never validates, logs in or writes settings.
                var state = owner.State;
                return new
                {
                    Success = true,
                    OwnerDefinitionId = owner.DefinitionId,
                    Status = state.Status.ToString(),
                    state.EffectiveAuthMode,
                    state.CheckedAtUtc,
                    state.LastSuccessfulValidationUtc,
                    Problem = state.Problem?.Code,
                    state.Description
                };
            }
            if (action == "clearLocalCredentials")
            {
                try
                {
                    return new { Success = _sessionProviders.ClearLocalCredentials(Definition.Id),
                        Message = "Clears only this plugin definition's local credentials; does not revoke Bandcamp server sessions or erase backups, browser sessions or other definitions." };
                }
                catch (Exception) { return new { Success = false, Problem = "StorageFailure" }; }
            }
            if (action is not ("beginIdentityRenewal" or "renewIdentity" or "cancelIdentityRenewal" or "identityRenewalStatus"))
                return base.RequestAction(action, query);
            if (Definition.Id <= 0) return new { Success = false, State = "Unknown", Problem = "InvalidRequest" };
            if (action == "beginIdentityRenewal")
            {
                var owner = _sessionProviders.GetNamed(Definition.Name);
                if (owner.DefinitionId != Definition.Id)
                    return new { Success = false, State = "Unknown", Problem = "ConfigurationMissing" };
                return BandcampIdentityRenewals.Begin(Definition.Id, Definition.Name)?.Receipt()
                    ?? new { Success = false, State = "Unknown", Problem = "Unavailable" };
            }

            if (!query.TryGetValue("operationId", out var id) || !Guid.TryParseExact(id, "D", out var operationId))
                return new { Success = false, State = "Unknown", Problem = "InvalidRequest" };
            var operation = BandcampIdentityRenewals.Find(operationId, Definition.Id, Definition.Name);
            if (operation == null) return new { Success = false, State = "Unknown", Problem = "UnknownOperation" };
            if (action == "cancelIdentityRenewal") operation.Cancel();
            if (action != "renewIdentity" || !operation.Start()) return operation.Receipt();
            try
            {
                if (!query.TryGetValue("fanId", out var value) || !long.TryParse(value, out var fanId) || fanId <= 0)
                {
                    operation.Complete(false, "InvalidRequest");
                    return operation.Receipt();
                }
                var provider = _sessionProviders.GetNamed(Definition.Name);
                if (provider.DefinitionId != Definition.Id)
                {
                    operation.Complete(false, "ConfigurationMissing");
                    return operation.Receipt();
                }
                var session = provider.ReplaceIdentityForFanAsync(Settings.Cookies, fanId, Definition.Id,
                    tryBeginCommit: operation.TryBeginCommit).GetAwaiter().GetResult();
                operation.Complete(session != null, session == null ? provider.State.Problem?.Code : null);
            }
            catch (Exception)
            {
                operation.Complete(false, "RecoveryFailed");
            }
            return operation.Receipt();
        }

        /// <summary>
        /// Validates the download client configuration:
        /// 1. Tests cookie auth by attempting fan_id resolution
        /// 2. Validates the download path exists and is writable
        /// </summary>
        protected override void Test(List<ValidationFailure> failures)
        {
            _logger.Debug("Bandcamp download client: Running connection test");

            // Test auth: validate the cookie (live fan_id lookup); with credentials set, a dead
            // or missing cookie triggers a login and the outcome is reported per field.
            try
            {
                var hasCredentials = Settings.HasCredentials();
                var provider = _sessionProviders.GetForTest(Definition.Id, Settings);
                var session = hasCredentials || !string.IsNullOrWhiteSpace(Settings.AuthConfiguration)
                    ? provider.RenewAsync().GetAwaiter().GetResult()
                    : provider.ValidateAsync().GetAwaiter().GetResult();

                if (session == null && provider.State.Problem != null)
                {
                    var problem = provider.State.Problem;
                    var field = problem == BandcampSessionProblem.ConfigurationMissing ? nameof(BandcampDownloadSettings.AuthConfiguration)
                        : problem == BandcampSessionProblem.BadCredentials ? "Password"
                        : problem == BandcampSessionProblem.VerificationNeeded ? "VerificationCode" : "Cookies";
                    failures.Add(new ValidationFailure(field, problem.Description));
                }
                else if (session == null && hasCredentials)
                {
                    failures.Add(LoginFailure(provider));
                    _logger.Debug("Bandcamp download client: Login test failed ({0})", provider.LastLoginOutcome);
                }
                else if (session == null)
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

        private static ValidationFailure LoginFailure(IBandcampSessionProvider provider)
        {
            if (provider.State.Problem == BandcampSessionProblem.StorageFailure ||
                provider.State.Problem == BandcampSessionProblem.ReplacementConflict)
            {
                return new ValidationFailure("Cookies", provider.State.Problem.Description);
            }

            return provider.LastLoginOutcome switch
            {
                BandcampLoginOutcome.BadCredentials => new ValidationFailure("Password",
                    "Bandcamp rejected the email or password. Login won't be retried until you change them."),
                BandcampLoginOutcome.VerificationRequired => new ValidationFailure("VerificationCode",
                    "Bandcamp emailed you a verification code. Enter it here and save; it is used once, then cleared."),
                BandcampLoginOutcome.CaptchaRequired => new ValidationFailure("Cookies",
                    "Bandcamp asked for a captcha, which can't be solved here. Paste a session cookie from your browser instead."),
                _ => new ValidationFailure("Email",
                    "Could not log in to Bandcamp: " + (provider.LastLoginMessage ?? "unknown error"))
            };
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
        /// Builds a conservative retagging context from Lidarr's matched album metadata.
        /// We always carry canonical artist/album information. Release-level MBIDs are only
        /// included when we can identify a single preferred release safely.
        /// </summary>
        private BandcampRetagContext? BuildRetagContext(RemoteAlbum remoteAlbum)
        {
            if (remoteAlbum.Artist == null || remoteAlbum.Albums == null || remoteAlbum.Albums.Count != 1)
            {
                return null;
            }

            var album = _albumService.GetAlbum(remoteAlbum.Albums[0].Id);
            var artist = _albumService.GetAlbum(album.Id).Artist?.Value ?? remoteAlbum.Artist;
            var release = SelectPreferredRelease(album.Id);
            var tracks = BuildTrackContexts(album.Id, release?.Id);

            return new BandcampRetagContext
            {
                ArtistName = artist.Name,
                ArtistMusicBrainzId = artist.ForeignArtistId,
                AlbumTitle = album.Title,
                AlbumMusicBrainzId = album.ForeignAlbumId,
                AlbumType = album.AlbumType,
                AlbumDisambiguation = album.Disambiguation,
                AlbumReleaseDate = album.ReleaseDate,
                Genres = album.Genres.Any() ? album.Genres.ToArray() : Array.Empty<string>(),
                PreferredRelease = release == null ? null : new BandcampRetagReleaseContext
                {
                    ReleaseMusicBrainzId = release.ForeignReleaseId,
                    ReleaseArtistMusicBrainzId = artist.ForeignArtistId,
                    ReleaseStatus = release.Status,
                    Label = release.Label.FirstOrDefault(),
                    ReleaseDate = release.ReleaseDate,
                    DiscCount = release.Media.Count,
                    MediaByDisc = release.Media.ToDictionary(m => m.Number, m => m.Format)
                },
                Tracks = tracks
            };
        }

        private AlbumRelease? SelectPreferredRelease(int albumId)
        {
            var releases = _releaseService.GetReleasesByAlbum(albumId);
            if (releases.Count == 1)
            {
                return releases[0];
            }

            var monitored = releases.Where(r => r.Monitored).ToList();
            if (monitored.Count == 1)
            {
                return monitored[0];
            }

            return null;
        }

        private List<BandcampRetagTrackContext> BuildTrackContexts(int albumId, int? preferredReleaseId)
        {
            var tracks = preferredReleaseId.HasValue
                ? _trackService.GetTracksByRelease(preferredReleaseId.Value)
                : _trackService.GetTracksByAlbum(albumId);

            return tracks
                .GroupBy(t => t.AbsoluteTrackNumber)
                .Select(g =>
                {
                    var distinctTitles = g.Select(t => t.Title).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (distinctTitles.Count > 1)
                    {
                        return null;
                    }

                    var distinctRecordings = g.Select(t => t.ForeignRecordingId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (distinctRecordings.Count > 1)
                    {
                        return null;
                    }

                    var track = g.OrderBy(t => t.MediumNumber).ThenBy(t => t.AbsoluteTrackNumber).First();
                    return new BandcampRetagTrackContext
                    {
                        AbsoluteTrackNumber = track.AbsoluteTrackNumber,
                        MediumNumber = track.MediumNumber,
                        Title = track.Title,
                        RecordingMusicBrainzId = distinctRecordings.SingleOrDefault(),
                        ReleaseTrackMusicBrainzId = preferredReleaseId.HasValue ? track.ForeignTrackId : null
                    };
                })
                .Where(t => t != null)
                .OrderBy(t => t!.AbsoluteTrackNumber)
                .Select(t => t!)
                .ToList();
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
            var invalid = Path.GetInvalidFileNameChars();
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
