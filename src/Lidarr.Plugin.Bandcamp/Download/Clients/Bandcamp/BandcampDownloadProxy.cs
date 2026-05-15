using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    /// <summary>
    /// Orchestrates the full Bandcamp download flow for a single album/track:
    /// 1. Resolve fan_id from session cookies
    /// 2. Find purchase in user's collection matching the album URL
    /// 3. Get download page data and extract per-format download URLs
    /// 4. Resolve statdownload URL to get the actual file download URL
    /// 5. Download the archive (ZIP or single FLAC) via Lidarr's HTTP client
    /// 6. Write to temp file, then extract ZIP or move single file to output dir
    ///
    /// All logging at debug level. Fan ID only at trace level.
    /// No cookie values or auth headers are ever logged.
    /// </summary>
    public class BandcampDownloadProxy : IBandcampDownloadProxy
    {
        private const string BandcampDownloadBaseUrl = "https://bandcamp.com/download";

        private readonly BandcampApiClient _apiClient;
        private readonly BandcampHttpClient _httpClient;
        private readonly Logger _logger;

        public BandcampDownloadProxy(
            BandcampApiClient apiClient,
            BandcampHttpClient httpClient,
            Logger logger)
        {
            _apiClient = apiClient;
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task ExecuteDownloadAsync(
            BandcampDownloadItem item, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(item.Cookies))
            {
                throw new DownloadException(
                    "Cannot execute download: session cookies are not set on the download item.");
            }

            var cookies = item.Cookies!;
            var requestedFormat = ExtractFormatFragment(item.AlbumUrl);
            var lookupUrl = NormalizeUrl(StripFragment(item.AlbumUrl));

            // Phase 1: Resolve fan_id
            item.Phase = "fan_id_resolution";
            _logger.Debug("Bandcamp download proxy [{0}]: Phase 1 — Resolving fan_id", item.DownloadId);

            var fanId = await _apiClient.ResolveFanIdAsync(cookies).ConfigureAwait(false);
            if (fanId == null)
            {
                throw new DownloadException(
                    "Failed to resolve Bandcamp fan_id. Session cookies may be invalid or expired.");
            }

            _logger.Trace("Bandcamp download proxy [{0}]: Resolved fan_id {1}", item.DownloadId, fanId);

            // Phase 2: Find purchase in collection
            item.Phase = "purchase_resolution";
            _logger.Debug("Bandcamp download proxy [{0}]: Phase 2 — Resolving download page for URL: {1}",
                item.DownloadId, lookupUrl);

            cancellationToken.ThrowIfCancellationRequested();

            BandcampCollectionItem? purchase = null;
            string downloadPageUrl;

            if (IsDownloadPageUrl(lookupUrl))
            {
                downloadPageUrl = lookupUrl; // Already normalized on line 57
                _logger.Debug("Bandcamp download proxy [{0}]: Using indexer-provided redownload URL", item.DownloadId);
            }
            else
            {
                purchase = await _apiClient.FindPurchaseByUrlAsync(
                    cookies, fanId.Value, lookupUrl).ConfigureAwait(false);

                if (purchase == null)
                {
                    throw new DownloadException(
                        $"Failed to resolve purchase for album URL '{lookupUrl}'. " +
                        "The album may not be in your Bandcamp collection.");
                }

                _logger.Debug("Bandcamp download proxy [{0}]: Found purchase — item_id={1}, type={2}",
                    item.DownloadId, purchase.ItemId, purchase.ItemType);

                downloadPageUrl = !string.IsNullOrWhiteSpace(purchase.DownloadPageUrl)
                    ? NormalizeUrl(purchase.DownloadPageUrl!)
                    : BuildDownloadPageUrl(purchase);
            }

            // Phase 3: Get download page data
            item.Phase = "download_url_extraction";
            _logger.Debug("Bandcamp download proxy [{0}]: Phase 3 — Extracting download URL", item.DownloadId);

            cancellationToken.ThrowIfCancellationRequested();

            var pageData = await _apiClient.GetDownloadPageDataAsync(
                cookies, downloadPageUrl).ConfigureAwait(false);

            if (pageData == null || pageData.DownloadItems.Count == 0)
            {
                throw new DownloadException(
                    $"Failed to extract download URLs from purchase page for '{item.AlbumUrl}'.");
            }

            // Find the download URL for the requested format
            var downloadEntry = purchase != null
                ? pageData.DownloadItems.FirstOrDefault(i => i.ItemId == purchase.ItemId) ?? pageData.DownloadItems[0]
                : pageData.DownloadItems[0];
            var formatKey = requestedFormat ?? "flac";

            if (!downloadEntry.DownloadUrls.TryGetValue(formatKey, out var downloadUrl) &&
                !downloadEntry.DownloadUrls.TryGetValue("flac", out downloadUrl))
            {
                // Fallback: use the first available format
                var firstFormat = downloadEntry.DownloadUrls.FirstOrDefault();
                if (firstFormat.Value != null)
                {
                    downloadUrl = firstFormat.Value;
                    _logger.Debug("Bandcamp download proxy [{0}]: Requested format '{1}' not available, falling back to '{2}'",
                        item.DownloadId, formatKey, firstFormat.Key);
                }
                else
                {
                    throw new DownloadException(
                        $"No download URLs available for purchase '{item.AlbumUrl}'.");
                }
            }

            // Phase 4: Resolve statdownload URL
            item.Phase = "statdownload";
            _logger.Debug("Bandcamp download proxy [{0}]: Phase 4 — Resolving statdownload URL", item.DownloadId);

            cancellationToken.ThrowIfCancellationRequested();

            var resolvedDownload = await _apiClient.ResolveStatdownloadUrlAsync(
                cookies, downloadUrl, formatKey).ConfigureAwait(false);

            var rawResolvedUrl = resolvedDownload?.DownloadUrl;
            var resolvedUrl = rawResolvedUrl != null ? NormalizeUrl(rawResolvedUrl) : null;

            if (string.IsNullOrWhiteSpace(resolvedUrl) && resolvedDownload?.DirectResponse == null)
            {
                // Fall back to using the download URL directly
                resolvedUrl = downloadUrl;
                _logger.Debug("Bandcamp download proxy [{0}]: Statdownload resolution empty, using direct URL",
                    item.DownloadId);
            }

            // Phase 5: Download the file to a temp location
            item.Phase = "file_download";
            item.Status = BandcampDownloadStatus.Downloading;
            _logger.Debug("Bandcamp download proxy [{0}]: Phase 5 — Downloading archive", item.DownloadId);

            cancellationToken.ThrowIfCancellationRequested();

            // Ensure output directory exists and doesn't contain stale unreadable files from prior attempts.
            PrepareOutputDirectory(item.OutputPath);

            var tempFile = Path.Combine(item.OutputPath, $"{item.DownloadId}.tmp");

            try
            {
                if (resolvedDownload?.DirectResponse != null)
                {
                    await WriteResponseToFileAsync(resolvedDownload.DirectResponse, tempFile, item, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await DownloadToFileAsync(cookies, resolvedUrl!, tempFile, item, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                TryDeleteFile(tempFile);
                throw;
            }

            // Phase 6: Extract or move the file
            item.Phase = "extraction";
            item.Status = BandcampDownloadStatus.Extracting;
            _logger.Debug("Bandcamp download proxy [{0}]: Phase 6 — Extracting archive", item.DownloadId);

            try
            {
                await ExtractDownloadAsync(tempFile, item.OutputPath, cancellationToken).ConfigureAwait(false);
                NormalizeExtractedMetadata(item.OutputPath, item);
            }
            finally
            {
                TryDeleteFile(tempFile);
            }

            _logger.Debug("Bandcamp download proxy [{0}]: All phases completed -> {1}",
                item.DownloadId, item.OutputPath);
        }

        /// <summary>
        /// Downloads a file from the resolved URL to a local temp file.
        /// Lidarr's HttpResponse buffers the full response into byte[], so we write
        /// ResponseData to disk in one shot. Progress jumps to 1.0 on completion.
        /// </summary>
        private async Task DownloadToFileAsync(
            string cookies,
            string fileUrl,
            string tempFilePath,
            BandcampDownloadItem item,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Use the API client's DownloadFileAsync which validates Content-Type
            var response = await _apiClient.DownloadFileAsync(cookies, fileUrl).ConfigureAwait(false);
            await WriteResponseToFileAsync(response, tempFilePath, item, cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteResponseToFileAsync(
            HttpResponse response,
            string tempFilePath,
            BandcampDownloadItem item,
            CancellationToken cancellationToken)
        {
            var responseData = response.ResponseData;
            if (responseData == null || responseData.Length == 0)
            {
                throw new DownloadException("Download response contained no data.");
            }

            // Track size for progress reporting
            item.TotalSize = responseData.Length;

            // Write to temp file asynchronously
            await File.WriteAllBytesAsync(tempFilePath, responseData, cancellationToken).ConfigureAwait(false);

            item.DownloadedBytes = responseData.Length;
            item.Progress = 1.0;

            _logger.Debug("Bandcamp download proxy [{0}]: File download complete ({1} bytes)",
                item.DownloadId, item.DownloadedBytes);
        }

        /// <summary>
        /// Extracts a downloaded archive. Handles ZIP files and single audio files
        /// (Bandcamp sometimes serves individual tracks as bare FLAC, not zipped).
        /// </summary>
        private async Task ExtractDownloadAsync(
            string archivePath, string outputDir, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsZipFile(archivePath))
                {
                    _logger.Debug("Bandcamp download proxy: Extracting ZIP archive to {0}", outputDir);
                    ZipFile.ExtractToDirectory(archivePath, outputDir, overwriteFiles: true);
                    NormalizeExtractedPermissions(outputDir);
                }
                else
                {
                    // Single file (e.g., bare FLAC) — move to output directory with proper extension
                    var extension = DetectFileExtension(archivePath) ?? ".flac";
                    var fileName = Path.GetFileNameWithoutExtension(archivePath) + extension;
                    var destPath = Path.Combine(outputDir, fileName);

                    _logger.Debug("Bandcamp download proxy: Moving single file to {0}", destPath);
                    File.Move(archivePath, destPath, overwrite: true);
                    NormalizeFilePermissions(destPath);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private void NormalizeExtractedMetadata(string outputDir, BandcampDownloadItem item)
        {
            if (item.RetagContext == null)
            {
                return;
            }

            var audioFiles = Directory
                .EnumerateFiles(outputDir, "*", SearchOption.AllDirectories)
                .Where(IsSupportedAudioFile)
                .Select(path => new
                {
                    Path = path,
                    ExistingTag = SafeReadAudioTag(path)
                })
                .OrderBy(f => f.ExistingTag?.Track > 0 ? 0 : 1)
                .ThenBy(f => f.ExistingTag?.Track ?? int.MaxValue)
                .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (audioFiles.Count == 0)
            {
                return;
            }

            for (var i = 0; i < audioFiles.Count; i++)
            {
                try
                {
                    ApplyCanonicalTagsToFile(audioFiles[i].Path, item.RetagContext, i, audioFiles.Count, audioFiles[i].ExistingTag);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Bandcamp download proxy [{0}]: Failed to normalize tags for {1}", item.DownloadId, audioFiles[i].Path);
                }
            }
        }

        internal static void ApplyCanonicalTagsToFile(string path,
                                                      BandcampRetagContext context,
                                                      int fileIndex,
                                                      int totalFiles,
                                                      AudioTag? existingTag = null)
        {
            existingTag ??= SafeReadAudioTag(path);
            var tag = existingTag?.IsValid == true ? existingTag : new AudioTag();
            var trackContext = ResolveTrackContext(context, tag, path, fileIndex, totalFiles);

            tag.Title = trackContext?.Title ?? tag.Title ?? Path.GetFileNameWithoutExtension(path);
            tag.Performers = new[] { context.ArtistName };
            tag.AlbumArtists = new[] { context.ArtistName };
            tag.Album = context.AlbumTitle;
            tag.Track = (uint)(trackContext?.AbsoluteTrackNumber ?? (int)tag.Track);
            tag.TrackCount = (uint)Math.Max(context.Tracks.Count, (int)tag.TrackCount);
            tag.Disc = (uint)Math.Max(trackContext?.MediumNumber ?? (int)tag.Disc, 1);
            tag.DiscCount = (uint)Math.Max(context.PreferredRelease?.DiscCount ?? (int)tag.DiscCount, 1);
            tag.Media = ResolveMedia(context, trackContext?.MediumNumber) ?? tag.Media;
            tag.Date = context.PreferredRelease?.ReleaseDate ?? context.AlbumReleaseDate ?? tag.Date;
            tag.Year = (uint)((context.AlbumReleaseDate ?? tag.Date)?.Year ?? (int)tag.Year);
            tag.OriginalReleaseDate = context.AlbumReleaseDate ?? tag.OriginalReleaseDate;
            tag.OriginalYear = (uint)(context.AlbumReleaseDate?.Year ?? (int)tag.OriginalYear);
            tag.Publisher = context.PreferredRelease?.Label ?? tag.Publisher;
            tag.Genres = context.Genres.Any() ? context.Genres : tag.Genres;
            tag.MusicBrainzReleaseStatus = context.PreferredRelease?.ReleaseStatus?.ToLowerInvariant() ?? tag.MusicBrainzReleaseStatus;
            tag.MusicBrainzReleaseType = context.AlbumType?.ToLowerInvariant() ?? tag.MusicBrainzReleaseType;
            tag.MusicBrainzReleaseId = context.PreferredRelease?.ReleaseMusicBrainzId ?? tag.MusicBrainzReleaseId;
            tag.MusicBrainzArtistId = context.ArtistMusicBrainzId ?? tag.MusicBrainzArtistId;
            tag.MusicBrainzReleaseArtistId = context.PreferredRelease?.ReleaseArtistMusicBrainzId ?? context.ArtistMusicBrainzId ?? tag.MusicBrainzReleaseArtistId;
            tag.MusicBrainzReleaseGroupId = context.AlbumMusicBrainzId ?? tag.MusicBrainzReleaseGroupId;
            tag.MusicBrainzTrackId = trackContext?.RecordingMusicBrainzId ?? tag.MusicBrainzTrackId;
            tag.MusicBrainzReleaseTrackId = trackContext?.ReleaseTrackMusicBrainzId ?? tag.MusicBrainzReleaseTrackId;
            tag.MusicBrainzAlbumComment = context.AlbumDisambiguation ?? tag.MusicBrainzAlbumComment;
            tag.Write(path);
        }

        private static BandcampRetagTrackContext? ResolveTrackContext(BandcampRetagContext context,
                                                                      AudioTag tag,
                                                                      string path,
                                                                      int fileIndex,
                                                                      int totalFiles)
        {
            if (context.Tracks.Count == 0)
            {
                return null;
            }

            if (context.Tracks.Count == 1 && totalFiles == 1)
            {
                return context.Tracks[0];
            }

            if (tag.Track > 0)
            {
                var byNumber = context.Tracks.FirstOrDefault(t => t.AbsoluteTrackNumber == tag.Track);
                if (byNumber != null)
                {
                    return byNumber;
                }
            }

            var fileTrackNumber = ExtractLeadingTrackNumber(path);
            if (fileTrackNumber > 0)
            {
                var byFileName = context.Tracks.FirstOrDefault(t => t.AbsoluteTrackNumber == fileTrackNumber);
                if (byFileName != null)
                {
                    return byFileName;
                }
            }

            if (!string.IsNullOrWhiteSpace(tag.Title))
            {
                var matches = context.Tracks
                    .Where(t => string.Equals(NormalizeTrackKey(t.Title), NormalizeTrackKey(tag.Title), StringComparison.Ordinal))
                    .ToList();

                if (matches.Count == 1)
                {
                    return matches[0];
                }
            }

            if (context.Tracks.Count == totalFiles)
            {
                return context.Tracks[fileIndex];
            }

            return null;
        }

        private static AudioTag? SafeReadAudioTag(string path)
        {
            try
            {
                return new AudioTag(path);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSupportedAudioFile(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".aif", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".opus", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ResolveMedia(BandcampRetagContext context, int? mediumNumber)
        {
            if (mediumNumber == null || context.PreferredRelease == null)
            {
                return null;
            }

            return context.PreferredRelease.MediaByDisc.TryGetValue(mediumNumber.Value, out var media)
                ? media
                : null;
        }

        private static int ExtractLeadingTrackNumber(string path)
        {
            var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"^(?<track>\d{1,3})\b");
            if (match.Success && int.TryParse(match.Groups["track"].Value, out var track))
            {
                return track;
            }

            return 0;
        }

        private static string NormalizeTrackKey(string value)
        {
            return Regex.Replace(value, @"[^a-z0-9]+", string.Empty, RegexOptions.IgnoreCase)
                .ToLowerInvariant();
        }

        /// <summary>
        /// Checks if a file is a ZIP archive by reading the magic bytes (PK\x03\x04).
        /// </summary>
        private static bool IsZipFile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var buffer = new byte[4];
                var read = stream.Read(buffer, 0, 4);

                if (read < 4)
                {
                    return false;
                }

                // ZIP local file header: 0x50 0x4B 0x03 0x04
                return buffer[0] == 0x50 && buffer[1] == 0x4B &&
                       buffer[2] == 0x03 && buffer[3] == 0x04;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Detects audio file extension from magic bytes. Supports FLAC (fLaC),
        /// MP3 (ID3/\xFF\xFB), OGG (OggS), WAV (RIFF), and AAC (ADTS).
        /// </summary>
        private static string? DetectFileExtension(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var buffer = new byte[4];
                var read = stream.Read(buffer, 0, 4);

                if (read < 4)
                {
                    return null;
                }

                // FLAC: "fLaC"
                if (buffer[0] == 0x66 && buffer[1] == 0x4C &&
                    buffer[2] == 0x61 && buffer[3] == 0x43)
                {
                    return ".flac";
                }

                // WAV: "RIFF"
                if (buffer[0] == 0x52 && buffer[1] == 0x49 &&
                    buffer[2] == 0x46 && buffer[3] == 0x46)
                {
                    return ".wav";
                }

                // OGG: "OggS"
                if (buffer[0] == 0x4F && buffer[1] == 0x67 &&
                    buffer[2] == 0x67 && buffer[3] == 0x53)
                {
                    return ".ogg";
                }

                // MP3: ID3 tag header
                if (buffer[0] == 0x49 && buffer[1] == 0x44 && buffer[2] == 0x33)
                {
                    return ".mp3";
                }

                // MP3: sync word
                if (buffer[0] == 0xFF && (buffer[1] & 0xE0) == 0xE0)
                {
                    return ".mp3";
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Builds the Bandcamp download page URL from a collection purchase.
        /// Pattern: https://bandcamp.com/download?type={item_type}&amp;id={item_id}
        /// </summary>
        private static string BuildDownloadPageUrl(BandcampCollectionItem purchase)
        {
            var itemType = purchase.ItemType ?? "album";
            return $"{BandcampDownloadBaseUrl}?type={itemType}&id={purchase.ItemId}";
        }

        /// <summary>
        /// Normalizes URLs to use forward slashes, preventing Windows backslash
        /// path bugs that break on Linux hosts.
        ///
        /// Bandcamp's JSON API may return URLs with backslashes (e.g., from Windows
        /// systems) in fields like item_url, download_page_url, or statdownload responses.
        /// Example problematic URL from Charli xcx album:
        ///   "https://charlixcx.bandcamp.com\\album\\crash"
        ///
        /// These backslashes break path resolution on Linux hosts where Lidarr typically
        /// runs in Docker, causing download failures with path not found errors.
        ///
        /// Handles:
        /// - Single backslashes: "https://bandcamp.com\album\test" → "https://bandcamp.com/album/test"
        /// - JSON-escaped forward slashes: "https://bandcamp.com\/album\/test" → "https://bandcamp.com/album/test"
        /// - Double backslashes: "https://bandcamp.com\\album\\test" → "https://bandcamp.com/album/test"
        /// - Preserves existing forward slashes (no-op)
        /// - Preserves query parameters and fragments
        /// </summary>
        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            // Replace backslashes with forward slashes for cross-platform compatibility
            // This handles both Windows path separators and JSON-escaped sequences
            return url.Replace('\\', '/');
        }

        private static bool IsDownloadPageUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                   uri.Host.EndsWith("bandcamp.com", StringComparison.OrdinalIgnoreCase) &&
                   uri.AbsolutePath.Equals("/download", StringComparison.OrdinalIgnoreCase);
        }

        private static string StripFragment(string url)
        {
            var hashIndex = url.IndexOf('#');
            return hashIndex >= 0 ? url[..hashIndex] : url;
        }

        private static string? ExtractFormatFragment(string url)
        {
            var hashIndex = url.IndexOf('#');
            if (hashIndex < 0 || hashIndex == url.Length - 1)
            {
                return null;
            }

            var fragment = url[(hashIndex + 1)..];
            foreach (var part in fragment.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = part.Split('=', 2);
                if (split.Length == 2 && string.Equals(split[0], "format", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(split[1]);
                }
            }

            return null;
        }

        private static void PrepareOutputDirectory(string outputDir)
        {
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                return;
            }

            NormalizeExtractedPermissions(outputDir);
            Directory.Delete(outputDir, recursive: true);
            Directory.CreateDirectory(outputDir);
        }

        private static void NormalizeExtractedPermissions(string outputDir)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                NormalizeDirectoryPermissions(outputDir);

                foreach (var directory in Directory.EnumerateDirectories(outputDir, "*", SearchOption.AllDirectories))
                {
                    NormalizeDirectoryPermissions(directory);
                }

                foreach (var file in Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories))
                {
                    NormalizeFilePermissions(file);
                }
            }
            catch
            {
                // Best-effort normalization only; let the real extraction/import error surface.
            }
        }

        private static void NormalizeDirectoryPermissions(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch
            {
                // Best effort.
            }
        }

        private static void NormalizeFilePermissions(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead |
                    UnixFileMode.OtherRead);
            }
            catch
            {
                // Best effort.
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup — don't mask the original exception
            }
        }
    }
}
