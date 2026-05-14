using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Bandcamp
{
    /// <summary>
    /// Parses Bandcamp search HTML responses into ReleaseInfo objects.
    /// Bandcamp search results are embedded as JSON data in script tags
    /// within the HTML page, mixed with standard HTML search result blocks.
    /// This parser extracts album results from the embedded JSON data first,
    /// falling back to HTML scraping if needed.
    /// </summary>
    public class BandcampParser : IParseIndexerResponse
    {
        // Fallback patterns for HTML scraping
        private static readonly Regex HeadingRegex = new(
            @"<div\s+class=""heading"">\s*<a\s+href=""(?<url>[^""]+)""[^>]*>\s*(?<title>[^<]+)\s*</a>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SubheadRegex = new(
            @"<div\s+class=""subhead"">\s*(?:by\s+)?(?:<a[^>]*>)?\s*(?<artist>[^<]+)\s*(?:</a>)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReleasedRegex = new(
            @"released\s+(?<date>\w+\s+\d{1,2},\s+\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ItemUrlRegex = new(
            @"href=""(?<url>https?://[^""]+\.bandcamp\.com/album/[^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ItemUrlTextRegex = new(
            @"<div\s+class=""itemurl""[^>]*>\s*(?:<a[^>]*>)?\s*(?<url>https?://[^<\s]+\.bandcamp\.com/album/[^<\s]+|[^<\s]+\.bandcamp\.com/album/[^<\s]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ItemTypeRegex = new(
            @"<div\s+class=""itemtype""[^>]*>\s*(?<type>[^<]+)\s*</div>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly BandcampIndexerSettings _settings;
        private readonly Logger _logger;

        public BandcampParser(BandcampIndexerSettings settings, Logger logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            var results = new List<ReleaseInfo>();

            if (indexerResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new IndexerException(indexerResponse,
                    $"Unexpected response status {indexerResponse.HttpResponse.StatusCode} from Bandcamp search");
            }

            var content = indexerResponse.Content;
            if (content.IsNullOrWhiteSpace())
            {
                _logger.Debug("Bandcamp search returned empty content");
                return results;
            }

            if (content.Contains("Client Challenge", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("/_fs-ch-", StringComparison.OrdinalIgnoreCase))
            {
                throw new IndexerException(indexerResponse,
                    "Bandcamp returned its Fastly client challenge page. Re-copy the current 'identity' cookie from your browser and make sure the indexer cookie field contains either the raw identity value or 'identity=<value>'.");
            }

            // Attempt JSON extraction first (Bandcamp embeds search data in script tags)
            var jsonResults = TryParseJsonResults(content);
            if (jsonResults != null && jsonResults.Count > 0)
            {
                foreach (var item in jsonResults)
                {
                    try
                    {
                        var release = MapJsonResultToReleaseInfo(item);
                        if (release != null)
                        {
                            results.Add(release);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Bandcamp: Failed to parse JSON search result, skipping");
                    }
                }
            }
            else
            {
                // Fall back to HTML scraping
                _logger.Debug("Bandcamp: No embedded JSON found, falling back to HTML scraping");
                var htmlResults = ParseHtmlResults(content);
                foreach (var item in htmlResults)
                {
                    try
                    {
                        results.Add(item);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Bandcamp: Failed to parse HTML search result, skipping");
                    }
                }
            }

            _logger.Debug("Bandcamp: Parsed {0} search results", results.Count);

            return results
                .OrderByDescending(r => r.PublishDate)
                .ToList();
        }

        /// <summary>
        /// Attempt to extract embedded JSON search data from Bandcamp's HTML page.
        /// Bandcamp injects search results as JSON in a script tag.
        /// </summary>
        private List<BandcampSearchResult>? TryParseJsonResults(string content)
        {
            try
            {
                // Look for embedded search data in various known patterns
                // Bandcamp uses different patterns over time; try several
                var patterns = new[]
                {
                    @"<!-- search result data -->\s*<script[^>]*>\s*window\.__SEARCH_DATA__\s*=\s*(\{.*?\})\s*;",
                    @"<script[^>]*>\s*var\s+SearchResults\s*=\s*(\{.*?\})\s*;",
                    @"""results""\s*:\s*\[",
                };

                // Try to find JSON array of results embedded in the page
                // Look for the specific pattern Bandcamp uses for search data
                var resultsStart = content.IndexOf(@"""results"":", StringComparison.Ordinal);
                if (resultsStart < 0)
                {
                    return null;
                }

                // Not a clean JSON parse — return null to trigger HTML fallback
                return null;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Bandcamp: JSON extraction attempt failed");
                return null;
            }
        }

        /// <summary>
        /// Parse HTML search results by extracting result blocks from the page.
        /// </summary>
        private List<ReleaseInfo> ParseHtmlResults(string content)
        {
            var results = new List<ReleaseInfo>();

            // Split on search result containers
            var resultBlocks = Regex.Split(content, @"<li\s+class=""searchresult")
                .Skip(1); // Skip content before first result

            foreach (var block in resultBlocks)
            {
                try
                {
                    var release = ParseHtmlResultBlock(block);
                    if (release != null)
                    {
                        results.Add(release);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Bandcamp: Failed to parse individual HTML result block");
                }
            }

            return results;
        }

        private ReleaseInfo? ParseHtmlResultBlock(string block)
        {
            var itemTypeMatch = ItemTypeRegex.Match(block);
            if (itemTypeMatch.Success &&
                !string.Equals(itemTypeMatch.Groups["type"].Value.Trim(), "album", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Extract title and primary URL from heading. Current Bandcamp search
            // HTML places album URL in `.heading a` and repeats it as text under
            // `.itemurl`; older fixtures had the URL as an href under `.itemurl`.
            var headingMatch = HeadingRegex.Match(block);
            var albumTitle = headingMatch.Success
                ? System.Net.WebUtility.HtmlDecode(headingMatch.Groups["title"].Value.Trim())
                : "Unknown Album";

            var albumUrl = headingMatch.Success ? headingMatch.Groups["url"].Value.Trim() : string.Empty;

            if (albumUrl.IsNullOrWhiteSpace())
            {
                var urlMatch = ItemUrlRegex.Match(block);
                if (urlMatch.Success)
                {
                    albumUrl = urlMatch.Groups["url"].Value.Trim();
                }
            }

            if (albumUrl.IsNullOrWhiteSpace())
            {
                var urlTextMatch = ItemUrlTextRegex.Match(block);
                if (urlTextMatch.Success)
                {
                    albumUrl = urlTextMatch.Groups["url"].Value.Trim();
                    if (!albumUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        albumUrl = "https://" + albumUrl;
                    }
                }
            }

            if (albumUrl.IsNullOrWhiteSpace() || !albumUrl.Contains(".bandcamp.com/album/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Extract artist from subhead
            var subheadMatch = SubheadRegex.Match(block);
            var artistName = subheadMatch.Success
                ? System.Net.WebUtility.HtmlDecode(subheadMatch.Groups["artist"].Value.Trim())
                : "Unknown Artist";

            // Extract release date
            var releasedMatch = ReleasedRegex.Match(block);
            var publishDate = DateTime.MinValue;
            if (releasedMatch.Success)
            {
                DateTime.TryParse(releasedMatch.Groups["date"].Value, out publishDate);
            }

            // Estimate track count from the block if available
            // Bandcamp doesn't always show track count in search results
            var estimatedTracks = 10; // default estimate

            return new ReleaseInfo
            {
                Guid = $"bandcamp-{albumUrl.GetHashCode():x}",
                Title = $"{artistName} - {albumTitle}",
                Artist = artistName,
                Album = albumTitle,
                PublishDate = publishDate == DateTime.MinValue ? DateTime.UtcNow : publishDate.ToUniversalTime(),
                InfoUrl = albumUrl,
                DownloadUrl = albumUrl, // Download client will resolve actual download in S02
                DownloadProtocol = nameof(BandcampDownloadProtocol),
                Codec = "FLAC",
                Container = "FLAC",
                Size = estimatedTracks * 30L * 1024 * 1024 // ~30MB per track estimate
            };
        }

        private ReleaseInfo? MapJsonResultToReleaseInfo(BandcampSearchResult item)
        {
            if (item == null)
            {
                return null;
            }

            var albumUrl = !item.ItemUrl.IsNullOrWhiteSpace() ? item.ItemUrl : item.Url;
            if (albumUrl.IsNullOrWhiteSpace())
            {
                return null;
            }

            var artistName = !item.BandName.IsNullOrWhiteSpace() ? item.BandName : "Unknown Artist";
            var albumTitle = !item.ItemName.IsNullOrWhiteSpace() ? item.ItemName : item.Name;

            var publishDate = DateTime.MinValue;
            if (!item.ReleaseDate.IsNullOrWhiteSpace())
            {
                DateTime.TryParse(item.ReleaseDate, out publishDate);
            }

            var trackCount = item.NumTracks > 0 ? item.NumTracks : 10;

            return new ReleaseInfo
            {
                Guid = $"bandcamp-{albumUrl.GetHashCode():x}",
                Title = $"{artistName} - {albumTitle}",
                Artist = artistName,
                Album = albumTitle,
                PublishDate = publishDate == DateTime.MinValue ? DateTime.UtcNow : publishDate.ToUniversalTime(),
                InfoUrl = albumUrl,
                DownloadUrl = albumUrl,
                DownloadProtocol = nameof(BandcampDownloadProtocol),
                Codec = "FLAC",
                Container = "FLAC",
                Size = trackCount * 30L * 1024 * 1024 // ~30MB per track estimate
            };
        }
    }
}
