using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Bandcamp
{
    /// <summary>
    /// Lidarr indexer for Bandcamp.
    ///
    /// Bandcamp's public catalog search can return albums the configured account
    /// cannot download. To avoid unfulfillable releases, search results are built
    /// from the authenticated fan collection and its redownload URLs, mirroring
    /// bandcampsync's load_purchases flow. Public search should only be added as a
    /// fallback when we can prove a result has a downloadable URL.
    /// </summary>
    public class BandcampIndexer : HttpIndexerBase<BandcampIndexerSettings>
    {
        private readonly BandcampApiClient _apiClient;

        public override string Name => "Bandcamp";
        public override string Protocol => nameof(BandcampDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 50;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(2);

        public BandcampIndexer(IHttpClient httpClient,
                               BandcampApiClient apiClient,
                               IIndexerStatusService indexerStatusService,
                               IConfigService configService,
                               IParsingService parsingService,
                               Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _apiClient = apiClient;
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new BandcampRequestGenerator(Settings, _logger);
        }

        public override IParseIndexerResponse GetParser()
        {
            return new BandcampParser(Settings, _logger);
        }

        public override Task<IList<ReleaseInfo>> FetchRecent()
        {
            return Task.FromResult<IList<ReleaseInfo>>(Array.Empty<ReleaseInfo>());
        }

        public override async Task<IList<ReleaseInfo>> Fetch(AlbumSearchCriteria searchCriteria)
        {
            if (!SupportsSearch)
            {
                return Array.Empty<ReleaseInfo>();
            }

            var releases = await FetchCollectionReleases(searchCriteria).ConfigureAwait(false);

            return CleanupReleases(releases);
        }

        public override async Task<IList<ReleaseInfo>> Fetch(ArtistSearchCriteria searchCriteria)
        {
            if (!SupportsSearch)
            {
                return Array.Empty<ReleaseInfo>();
            }

            var releases = await FetchCollectionReleases(
                searchCriteria.CleanArtistQuery,
                null).ConfigureAwait(false);

            return CleanupReleases(releases);
        }

        /// <summary>
        /// Bandcamp is search-only and intentionally has no RSS/recent feed.
        /// Validate by proving the configured identity cookie can load at least one
        /// downloadable collection item instead of probing a nonexistent RSS URL.
        /// </summary>
        protected override async Task<ValidationFailure> TestConnection()
        {
            if (Settings.Cookies.IsNullOrWhiteSpace())
            {
                return new ValidationFailure("Cookies", "Bandcamp identity cookie is required for search.");
            }

            try
            {
                var fanId = await _apiClient.ResolveFanIdAsync(Settings.Cookies).ConfigureAwait(false);
                if (fanId == null)
                {
                    return new ValidationFailure("Cookies", "Could not verify Bandcamp identity cookie.");
                }

                var collection = await _apiClient.GetDownloadableCollectionAsync(Settings.Cookies, fanId.Value, maxPages: 1)
                    .ConfigureAwait(false);

                if (collection.Empty())
                {
                    return new ValidationFailure(string.Empty, "Bandcamp authentication worked, but no downloadable purchases were found in the collection.");
                }

                _logger.Debug("Bandcamp indexer: Collection test passed with {0} downloadable item(s)", collection.Count);
                return default!;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Bandcamp indexer collection test failed");
                return new ValidationFailure(string.Empty, "Unable to connect to Bandcamp indexer. " + ex.Message);
            }
        }

        private async Task<List<ReleaseInfo>> FetchCollectionReleases(AlbumSearchCriteria searchCriteria)
        {
            var results = new List<ReleaseInfo>();
            var fanId = await _apiClient.ResolveFanIdAsync(Settings.Cookies).ConfigureAwait(false);
            if (fanId == null)
            {
                _logger.Debug("Bandcamp indexer: Cannot search collection because fan_id could not be resolved");
                return results;
            }

            var collection = await _apiClient.GetDownloadableCollectionAsync(Settings.Cookies, fanId.Value)
                .ConfigureAwait(false);

            // Extract expected track counts from search criteria for release specificity
            var expectedTrackCounts = ExtractExpectedTrackCounts(searchCriteria);

            var matches = collection
                .Where(item => MatchesSearch(item, searchCriteria, expectedTrackCounts))
                .GroupBy(GetCollectionIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.DownloadPageUrl))
                    .ThenByDescending(item => item.ItemId)
                    .First())
                .ToList();

            _logger.Debug("Bandcamp indexer: {0} unique downloadable collection item(s) matched query", matches.Count);

            foreach (var item in matches)
            {
                var releases = await BuildReleaseInfosForCollectionItem(item, expectedTrackCounts).ConfigureAwait(false);
                foreach (var release in releases)
                {
                    if (results.All(existing => !string.Equals(existing.Guid, release.Guid, StringComparison.OrdinalIgnoreCase)))
                    {
                        results.Add(release);
                    }
                }
            }

            return results;
        }

        private async Task<List<ReleaseInfo>> FetchCollectionReleases(string artistQuery, string? albumQuery)
        {
            var results = new List<ReleaseInfo>();
            var fanId = await _apiClient.ResolveFanIdAsync(Settings.Cookies).ConfigureAwait(false);
            if (fanId == null)
            {
                _logger.Debug("Bandcamp indexer: Cannot search collection because fan_id could not be resolved");
                return results;
            }

            var collection = await _apiClient.GetDownloadableCollectionAsync(Settings.Cookies, fanId.Value)
                .ConfigureAwait(false);

            var matches = collection
                .Where(item => MatchesSearch(item, artistQuery, albumQuery))
                .GroupBy(GetCollectionIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.DownloadPageUrl))
                    .ThenByDescending(item => item.ItemId)
                    .First())
                .ToList();

            _logger.Debug("Bandcamp indexer: {0} unique downloadable collection item(s) matched query", matches.Count);

            foreach (var item in matches)
            {
                var releases = await BuildReleaseInfosForCollectionItem(item).ConfigureAwait(false);
                foreach (var release in releases)
                {
                    if (results.All(existing => !string.Equals(existing.Guid, release.Guid, StringComparison.OrdinalIgnoreCase)))
                    {
                        results.Add(release);
                    }
                }
            }

            return results;
        }

        private async Task<List<ReleaseInfo>> BuildReleaseInfosForCollectionItem(BandcampCollectionItem item, HashSet<int>? expectedTrackCounts = null)
        {
            var releases = new List<ReleaseInfo>();

            if (string.IsNullOrWhiteSpace(item.DownloadPageUrl) || string.IsNullOrWhiteSpace(item.ItemUrl))
            {
                return releases;
            }

            var pageData = await _apiClient.GetDownloadPageDataAsync(Settings.Cookies, item.DownloadPageUrl!)
                .ConfigureAwait(false);
            var albumDurationSeconds = await _apiClient.GetAlbumDurationSecondsAsync(Settings.Cookies, item.ItemUrl!)
                .ConfigureAwait(false);

            if (pageData == null)
            {
                return releases;
            }

            var downloadItem = pageData.DownloadItems.FirstOrDefault(i => i.ItemId == item.ItemId) ??
                pageData.DownloadItems.FirstOrDefault();

            if (downloadItem == null)
            {
                return releases;
            }

            foreach (var format in downloadItem.DownloadUrls)
            {
                if (string.IsNullOrWhiteSpace(format.Value))
                {
                    continue;
                }

                if (!downloadItem.DownloadSizes.TryGetValue(format.Key, out var size) || size <= 0)
                {
                    _logger.Debug("Bandcamp indexer: Skipping {0} / {1} format {2} because no positive size was provided",
                        item.BandName, item.Title, format.Key);
                    continue;
                }

                releases.Add(ToReleaseInfo(item, format.Key, size, albumDurationSeconds, expectedTrackCounts));
            }

            return releases;
        }

        private ReleaseInfo ToReleaseInfo(BandcampCollectionItem item, string formatKey, long size, double? albumDurationSeconds, HashSet<int>? expectedTrackCounts = null)
        {
            var artistName = item.BandName ?? "Unknown Artist";
            var albumTitle = item.Title ?? "Unknown Album";
            var formatLabel = FormatLabel(formatKey, size, albumDurationSeconds);
            var title = BuildReleaseTitle(artistName, albumTitle, formatLabel, item.TrackCount, expectedTrackCounts);
            var publishDate = ParsePublishDate(item.ReleaseDate);
            var downloadUrl = AddFormatFragment(item.DownloadPageUrl!, formatKey);

            return new ReleaseInfo
            {
                Guid = $"bandcamp-{item.ItemUrl}-{formatKey}",
                Title = title,
                Artist = NormalizeReleaseComponent(artistName),
                Album = NormalizeAlbumTitle(artistName, albumTitle),
                PublishDate = publishDate,
                InfoUrl = item.ItemUrl!,
                DownloadUrl = downloadUrl,
                DownloadProtocol = nameof(BandcampDownloadProtocol),
                Codec = CodecForFormat(formatKey),
                Container = ContainerForFormat(formatKey),
                Size = size,
                Source = "bandcamp"
            };
        }

        private static string GetCollectionIdentity(BandcampCollectionItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.ItemUrl))
            {
                return item.ItemUrl!;
            }

            if (!string.IsNullOrWhiteSpace(item.DownloadPageUrl))
            {
                return item.DownloadPageUrl!;
            }

            return $"{item.ItemType}:{item.ItemId}";
        }

        /// <summary>
        /// Extract expected track counts from AlbumSearchCriteria for release specificity.
        /// Returns null if track count information is not available.
        /// </summary>
        private static HashSet<int>? ExtractExpectedTrackCounts(AlbumSearchCriteria searchCriteria)
        {
            if (searchCriteria.Albums == null || searchCriteria.Albums.Count == 0)
            {
                return null;
            }

            var trackCounts = new HashSet<int>();
            foreach (var album in searchCriteria.Albums)
            {
                if (album.AlbumReleases?.Value != null)
                {
                    foreach (var release in album.AlbumReleases.Value)
                    {
                        if (release.TrackCount > 0)
                        {
                            trackCounts.Add(release.TrackCount);
                        }
                    }
                }
            }

            return trackCounts.Count > 0 ? trackCounts : null;
        }

        /// <summary>
        /// Matches collection items against search criteria with optional track count filtering.
        /// When expectedTrackCounts is provided, filters releases to only those matching the expected track count.
        /// </summary>
        private static bool MatchesSearch(BandcampCollectionItem item, AlbumSearchCriteria searchCriteria, HashSet<int>? expectedTrackCounts)
        {
            var artist = NormalizeQueryValue(item.BandName);
            var title = NormalizeQueryValue(item.Title);

            if (!ContainsQuery(artist, searchCriteria.CleanArtistQuery))
            {
                return false;
            }

            if (!searchCriteria.CleanAlbumQuery.IsNullOrWhiteSpace())
            {
                if (!ContainsQuery(title, searchCriteria.CleanAlbumQuery!))
                {
                    return false;
                }
            }

            // Filter by track count if we have expected track counts
            if (expectedTrackCounts != null && expectedTrackCounts.Count > 0)
            {
                if (item.TrackCount > 0 && !expectedTrackCounts.Contains(item.TrackCount))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatchesSearch(BandcampCollectionItem item, string artistQuery, string? albumQuery)
        {
            var artist = NormalizeQueryValue(item.BandName);
            var title = NormalizeQueryValue(item.Title);

            if (!ContainsQuery(artist, artistQuery))
            {
                return false;
            }

            return albumQuery.IsNullOrWhiteSpace() || ContainsQuery(title, albumQuery!);
        }

        private static string NormalizeQueryValue(string? value)
        {
            return value.IsNullOrWhiteSpace() ? string.Empty : SearchCriteriaBase.GetQueryTitle(value!);
        }

        private static bool ContainsQuery(string value, string query)
        {
            if (query.IsNullOrWhiteSpace())
            {
                return true;
            }

            return value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   query.Contains(value, StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime ParsePublishDate(string? releaseDate)
        {
            if (!releaseDate.IsNullOrWhiteSpace() &&
                DateTime.TryParse(releaseDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            return DateTime.UtcNow;
        }

        private static string AddFormatFragment(string downloadPageUrl, string formatKey)
        {
            var separator = downloadPageUrl.Contains('#') ? "&" : "#";
            return $"{downloadPageUrl}{separator}format={Uri.EscapeDataString(formatKey)}";
        }

        internal static string BuildReleaseTitle(string? artistName, string? albumTitle, string formatLabel, int trackCount = 0, HashSet<int>? expectedTrackCounts = null)
        {
            var normalizedArtist = NormalizeReleaseComponent(artistName);
            var normalizedAlbum = NormalizeAlbumTitle(normalizedArtist, albumTitle);
            var baseTitle = $"{normalizedArtist} - {normalizedAlbum}";

            // Append track count for observability when we have it
            if (trackCount > 0)
            {
                baseTitle += $" [{trackCount} tracks]";
            }

            // Add WEB for custom format matching - Bandcamp is a web source
            // This ensures releases match WEB custom format requirements in quality profiles
            return $"{baseTitle} [WEB] [{formatLabel}]";
        }

        internal static string NormalizeAlbumTitle(string? artistName, string? albumTitle)
        {
            var normalizedArtist = NormalizeReleaseComponent(artistName);
            var normalizedAlbum = NormalizeReleaseComponent(albumTitle);

            normalizedAlbum = StripDuplicateArtistPrefix(normalizedAlbum, normalizedArtist);
            normalizedAlbum = StripTrailingReleaseTypeSuffix(normalizedAlbum);

            return normalizedAlbum;
        }

        internal static string NormalizeReleaseComponent(string? value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            value = value!
                .Replace('“', '"')
                .Replace('”', '"')
                .Replace('‘', '\'')
                .Replace('’', '\'');

            value = Regex.Replace(value.Trim(), @"\s+", " ");

            if (value.Length >= 2)
            {
                if ((value[0] == '"' && value[^1] == '"') ||
                    (value[0] == '\'' && value[^1] == '\''))
                {
                    value = value[1..^1].Trim();
                }
            }

            return value;
        }

        private static string StripDuplicateArtistPrefix(string albumTitle, string artistName)
        {
            if (artistName.IsNullOrWhiteSpace() || albumTitle.IsNullOrWhiteSpace())
            {
                return albumTitle;
            }

            var prefix = artistName + " - ";
            while (albumTitle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                albumTitle = albumTitle[prefix.Length..].TrimStart();
            }

            return albumTitle;
        }

        private static string StripTrailingReleaseTypeSuffix(string albumTitle)
        {
            if (albumTitle.IsNullOrWhiteSpace())
            {
                return albumTitle;
            }

            string[] suffixes =
            {
                " - Single",
                " - EP",
                " - LP",
                " - Digital Album",
                " (Single)",
                " (EP)",
                " (LP)",
                " [Single]",
                " [EP]",
                " [LP]"
            };

            var changed = true;
            while (changed)
            {
                changed = false;

                foreach (var suffix in suffixes)
                {
                    if (!albumTitle.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    albumTitle = albumTitle[..^suffix.Length].TrimEnd();
                    changed = true;
                    break;
                }
            }

            return albumTitle;
        }

        private static string FormatLabel(string formatKey, long size, double? albumDurationSeconds)
        {
            return formatKey switch
            {
                "mp3-v0" => "MP3 VBR V0",
                "mp3-320" => "MP3 320",
                "vorbis" or "ogg-vorbis" => InferVorbisLabel(size, albumDurationSeconds),
                "aac-hi" => "AAC",
                "aiff-lossless" or "aiff" => "AIFF",
                _ => formatKey.Replace('-', ' ').ToUpperInvariant()
            };
        }

        private static string InferVorbisLabel(long sizeBytes, double? albumDurationSeconds)
        {
            var bitrate = InferBitrateKbps(sizeBytes, albumDurationSeconds);
            if (bitrate == null)
            {
                return "OGG Vorbis";
            }

            return bitrate.Value switch
            {
                < 176 => "Vorbis Q5",
                < 208 => "Vorbis Q6",
                < 240 => "Vorbis Q7",
                < 288 => "Vorbis Q8",
                < 410 => "Vorbis Q9",
                _ => "Vorbis Q10"
            };
        }

        private static int? InferBitrateKbps(long sizeBytes, double? durationSeconds)
        {
            if (durationSeconds == null || durationSeconds <= 0 || sizeBytes <= 0)
            {
                return null;
            }

            return (int)Math.Round((sizeBytes * 8d) / durationSeconds.Value / 1000d);
        }

        private static string CodecForFormat(string formatKey)
        {
            return formatKey switch
            {
                "flac" => "FLAC",
                "alac" => "ALAC",
                "wav" => "WAV",
                "aiff" or "aiff-lossless" => "AIFF",
                "mp3-v0" or "mp3-320" => "MP3",
                "vorbis" or "ogg-vorbis" => "OGG",
                "aac-hi" => "AAC",
                _ => string.Empty
            };
        }

        private static string ContainerForFormat(string formatKey)
        {
            return formatKey switch
            {
                "flac" => "FLAC",
                "alac" => "ALAC",
                "wav" => "WAV",
                "aiff" or "aiff-lossless" => "AIFF",
                "mp3-v0" or "mp3-320" => "MP3",
                "vorbis" or "ogg-vorbis" => "OGG",
                "aac-hi" => "M4A",
                _ => string.Empty
            };
        }
    }
}
