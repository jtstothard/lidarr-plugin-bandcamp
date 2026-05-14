using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

            var releases = await FetchCollectionReleases(
                searchCriteria.CleanArtistQuery,
                searchCriteria.CleanAlbumQuery).ConfigureAwait(false);

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
                .ToList();

            _logger.Debug("Bandcamp indexer: {0} downloadable collection item(s) matched query", matches.Count);

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

        private async Task<List<ReleaseInfo>> BuildReleaseInfosForCollectionItem(BandcampCollectionItem item)
        {
            var releases = new List<ReleaseInfo>();

            if (string.IsNullOrWhiteSpace(item.DownloadPageUrl) || string.IsNullOrWhiteSpace(item.ItemUrl))
            {
                return releases;
            }

            var pageData = await _apiClient.GetDownloadPageDataAsync(Settings.Cookies, item.DownloadPageUrl!)
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

                releases.Add(ToReleaseInfo(item, format.Key, size));
            }

            return releases;
        }

        private ReleaseInfo ToReleaseInfo(BandcampCollectionItem item, string formatKey, long size)
        {
            var artistName = item.BandName ?? "Unknown Artist";
            var albumTitle = item.Title ?? "Unknown Album";
            var formatLabel = FormatLabel(formatKey);
            var title = $"{artistName} - {albumTitle} [{formatLabel}]";
            var publishDate = ParsePublishDate(item.ReleaseDate);
            var downloadUrl = AddFormatFragment(item.DownloadPageUrl!, formatKey);

            return new ReleaseInfo
            {
                Guid = $"bandcamp-{item.ItemUrl}-{formatKey}",
                Title = title,
                Artist = artistName,
                Album = albumTitle,
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

        private static string FormatLabel(string formatKey)
        {
            return formatKey switch
            {
                "mp3-v0" => "MP3 V0",
                "mp3-320" => "MP3 320",
                "vorbis" or "ogg-vorbis" => "OGG Vorbis",
                "aac-hi" => "AAC",
                "aiff-lossless" => "AIFF Lossless",
                _ => formatKey.Replace('-', ' ').ToUpperInvariant()
            };
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
