using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Http.Bandcamp;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    /// <summary>
    /// API client for Bandcamp download flows. Handles fan_id resolution,
    /// collection queries to find purchases, download page parsing for FLAC URLs,
    /// and statdownload URL construction and response parsing.
    /// All HTTP calls go through BandcampHttpClient for rate limiting, cookie
    /// injection, and credential-safe logging.
    /// </summary>
    public class BandcampApiClient
    {
        private const string BandcampBaseUrl = "https://bandcamp.com";

        private static readonly Regex PagedataRegex = new(
            @"var\s+pagedata\s*=\s*(\{.*?\})\s*;",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex DataBlobRegex = new(
            @"data-blob=""(.+?)""",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex StatdownloadUrlRegex = new(
            @"""url""\s*:\s*""([^""]+)""",
            RegexOptions.Compiled);

        private readonly BandcampHttpClient _httpClient;
        private readonly Logger _logger;

        public BandcampApiClient(BandcampHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <summary>
        /// Resolves the fan_id for the authenticated user by fetching the Bandcamp
        /// homepage and extracting it from the embedded data-blob JSON.
        /// Bandcamp's homepage uses a data-blob attribute on a div element containing
        /// pageContext.identity.fanId (camelCase). Falls back to var pagedata for other pages.
        /// </summary>
        /// <param name="cookies">Session cookies from browser.</param>
        /// <returns>The fan_id, or null if resolution fails.</returns>
        public async Task<long?> ResolveFanIdAsync(string cookies)
        {
            _logger.Debug("Bandcamp API: Resolving fan_id from homepage");

            var builder = _httpClient.CreateRequestBuilder(BandcampBaseUrl, cookies);
            var request = builder.Build();
            var response = await _httpClient.ExecuteAsync(request);

            var content = response.Content ?? string.Empty;

            // Approach 1: Parse data-blob attribute (Bandcamp homepage format)
            // The homepage embeds JSON in data-blob="..." on a div element
            var blobMatch = DataBlobRegex.Match(content);
            if (blobMatch.Success)
            {
                try
                {
                    // HTML-decode the blob content (it's double-encoded)
                    var blobJson = System.Net.WebUtility.HtmlDecode(
                        System.Net.WebUtility.HtmlDecode(blobMatch.Groups[1].Value));

                    using var doc = JsonDocument.Parse(blobJson);
                    var root = doc.RootElement;

                    // Navigate: pageContext -> identity -> fanId
                    if (root.TryGetProperty("pageContext", out var pageContext) &&
                        pageContext.TryGetProperty("identity", out var identity) &&
                        identity.TryGetProperty("fanId", out var fanIdEl))
                    {
                        if (fanIdEl.ValueKind == JsonValueKind.Number)
                        {
                            var fanId = fanIdEl.GetInt64();
                            _logger.Debug("Bandcamp API: Resolved fan_id {0} from data-blob", fanId);
                            return fanId;
                        }

                        if (fanIdEl.ValueKind == JsonValueKind.Null)
                        {
                            _logger.Debug("Bandcamp API: fan_id is null in data-blob — cookies may be invalid or expired");
                            return null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Bandcamp API: Failed to parse data-blob JSON");
                }
            }

            // Approach 2: Parse var pagedata (older/alternate Bandcamp page format)
            var pagedataMatch = PagedataRegex.Match(content);
            if (pagedataMatch.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(pagedataMatch.Groups[1].Value);
                    var root = doc.RootElement;

                    // Check pageContext.identity.fanId (camelCase)
                    if (root.TryGetProperty("pageContext", out var pageContext) &&
                        pageContext.TryGetProperty("identity", out var identity) &&
                        identity.TryGetProperty("fanId", out var fanIdEl) &&
                        fanIdEl.ValueKind == JsonValueKind.Number)
                    {
                        var fanId = fanIdEl.GetInt64();
                        _logger.Debug("Bandcamp API: Resolved fan_id {0} from pagedata", fanId);
                        return fanId;
                    }

                    // Also check for fan_id (snake_case) in some page formats
                    if (root.TryGetProperty("fan_id", out var fanIdSnake) &&
                        fanIdSnake.ValueKind == JsonValueKind.Number)
                    {
                        return fanIdSnake.GetInt64();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Bandcamp API: Failed to parse pagedata JSON");
                }
            }

            _logger.Debug("Bandcamp API: Failed to resolve fan_id — no valid identity found in page");
            return null;
        }

        /// <summary>
        /// Queries the Bandcamp fan collection API to find the user's purchases.
        /// Returns a list of purchase items with item_id, item_type, and band_id.
        /// </summary>
        /// <param name="cookies">Session cookies from browser.</param>
        /// <param name="fanId">The authenticated user's fan_id.</param>
        /// <param name="olderThanToken">Pagination token for older items (null for first page).</param>
        /// <returns>List of collection items.</returns>
        public async Task<List<BandcampCollectionItem>> GetCollectionAsync(
            string cookies, long fanId, string? olderThanToken = null)
        {
            _logger.Debug("Bandcamp API: Querying collection for fan_id (token={0})",
                olderThanToken != null ? "present" : "none");

            var url = $"{BandcampBaseUrl}/api/fancollection/1/collection_items";
            var builder = _httpClient.CreateRequestBuilder(url, cookies);
            builder.Method = System.Net.Http.HttpMethod.Post;

            var payload = new
            {
                fan_id = fanId,
                older_than_token = olderThanToken ?? string.Empty,
                count = 100
            };

            builder.Headers.ContentType = "application/json";

            var request = builder.Build();
            request.SetContent(JsonSerializer.Serialize(payload));
            var response = await _httpClient.ExecuteAsync(request);

            var content = response.Content ?? string.Empty;

            try
            {
                return ParseCollectionResponse(content);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Bandcamp API: Failed to parse collection response");
                return new List<BandcampCollectionItem>();
            }
        }

        public async Task<List<BandcampCollectionItem>> GetDownloadableCollectionAsync(
            string cookies, long fanId, int maxPages = 20)
        {
            var results = new List<BandcampCollectionItem>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? olderThanToken = null;

            for (var page = 0; page < maxPages; page++)
            {
                var items = await GetCollectionAsync(cookies, fanId, olderThanToken).ConfigureAwait(false);
                if (items.Count == 0)
                {
                    break;
                }

                foreach (var item in items)
                {
                    if (!item.IsDownloadable)
                    {
                        continue;
                    }

                    var key = !string.IsNullOrWhiteSpace(item.DownloadPageUrl)
                        ? item.DownloadPageUrl!
                        : $"{item.ItemType}:{item.ItemId}";

                    if (seenKeys.Add(key))
                    {
                        results.Add(item);
                    }
                }

                var nextToken = items.LastOrDefault(i => !string.IsNullOrWhiteSpace(i.Token))?.Token;
                if (string.IsNullOrWhiteSpace(nextToken) || string.Equals(nextToken, olderThanToken, StringComparison.Ordinal))
                {
                    break;
                }

                olderThanToken = nextToken;
            }

            _logger.Debug("Bandcamp API: Loaded {0} downloadable collection item(s)", results.Count);
            return results;
        }

        /// <summary>
        /// Finds a purchase in the user's collection matching the given album URL.
        /// Iterates through collection pages until a match is found or all pages exhausted.
        /// </summary>
        /// <param name="cookies">Session cookies from browser.</param>
        /// <param name="fanId">The authenticated user's fan_id.</param>
        /// <param name="albumUrl">The Bandcamp album URL to match against.</param>
        /// <returns>The matching collection item, or null if not found.</returns>
        public async Task<BandcampCollectionItem?> FindPurchaseByUrlAsync(
            string cookies, long fanId, string albumUrl)
        {
            _logger.Debug("Bandcamp API: Searching collection for album URL: {0}", albumUrl);

            string? olderThanToken = null;
            var maxPages = 20; // Safety limit

            for (var page = 0; page < maxPages; page++)
            {
                var items = await GetCollectionAsync(cookies, fanId, olderThanToken);

                if (items == null || items.Count == 0)
                {
                    _logger.Debug("Bandcamp API: No more collection items after page {0}", page);
                    break;
                }

                // Match by album URL — collection items contain item_url field
                var match = items.FirstOrDefault(item =>
                    !string.IsNullOrEmpty(item.ItemUrl) &&
                    albumUrl.Contains(item.ItemUrl, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    _logger.Debug("Bandcamp API: Found purchase matching album URL at page {0}", page);
                    return match;
                }

                // Get the token for the next page from the oldest item
                olderThanToken = items.Last().Token;
            }

            _logger.Debug("Bandcamp API: Purchase not found in collection for URL: {0}", albumUrl);
            return null;
        }

        /// <summary>
        /// Fetches the download page for a purchased album and extracts the download
        /// URL from the embedded pagedata JSON. The download page contains signed URLs
        /// for each format (FLAC, MP3, etc.).
        /// </summary>
        /// <param name="cookies">Session cookies from browser.</param>
        /// <param name="downloadPageUrl">The download page URL (from statdownload or purchase).</param>
        /// <returns>The parsed download pagedata, or null if parsing fails.</returns>
        public async Task<BandcampDownloadPageData?> GetDownloadPageDataAsync(
            string cookies, string downloadPageUrl)
        {
            _logger.Debug("Bandcamp API: Fetching download page pagedata from {0}", downloadPageUrl);

            var builder = _httpClient.CreateRequestBuilder(downloadPageUrl, cookies);
            var request = builder.Build();
            var response = await _httpClient.ExecuteAsync(request);

            var content = response.Content ?? string.Empty;

            // Try data-blob format first (current Bandcamp format on download pages)
            var blobMatch = DataBlobRegex.Match(content);
            if (blobMatch.Success)
            {
                try
                {
                    var blobJson = System.Net.WebUtility.HtmlDecode(
                        System.Net.WebUtility.HtmlDecode(blobMatch.Groups[1].Value));
                    return ParseDownloadPagedata(blobJson);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Bandcamp API: Failed to parse data-blob from download page");
                }
            }

            // Fall back to var pagedata format (older pages)
            var pagedataMatch = PagedataRegex.Match(content);
            if (pagedataMatch.Success)
            {
                try
                {
                    return ParseDownloadPagedata(pagedataMatch.Groups[1].Value);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Bandcamp API: Failed to parse pagedata from download page");
                }
            }

            _logger.Debug("Bandcamp API: Failed to extract pagedata from download page");
            return null;
        }

        /// <summary>
        /// Constructs and resolves a statdownload URL. Bandcamp uses statdownload
        /// endpoints to track download analytics before redirecting to the actual
        /// file URL. This follows the redirect chain and returns the final download URL.
        /// </summary>
        /// <param name="cookies">Session cookies from browser.</param>
        /// <param name="downloadUrl">The initial download URL (from pagedata or purchase).</param>
        /// <param name="format">The desired format (e.g., "FLAC").</param>
        /// <returns>The final download URL for the file, or null if resolution fails.</returns>
        public async Task<string?> ResolveStatdownloadUrlAsync(
            string cookies, string downloadUrl, string format)
        {
            _logger.Debug("Bandcamp API: Resolving statdownload URL for format {0}", format);

            // Bandcamp statdownload URLs look like:
            // https://bandcamp.com/statdownload/{item_type}/{item_id}?{params}
            // They return JSON with a "url" field pointing to the actual file
            var url = $"{downloadUrl}&.format={format}&.json=true";

            var builder = _httpClient.CreateRequestBuilder(url, cookies);
            var request = builder.Build();
            var response = await _httpClient.ExecuteAsync(request);

            var content = response.Content ?? string.Empty;

            // Parse the JSON response to extract the actual download URL
            var urlMatch = StatdownloadUrlRegex.Match(content);
            if (urlMatch.Success)
            {
                var resolvedUrl = urlMatch.Groups[1].Value
                    .Replace("\\/", "/")
                    .Replace("\\u0026", "&");

                _logger.Debug("Bandcamp API: Resolved statdownload URL successfully");
                return resolvedUrl;
            }

            // Some statdownload responses return the URL directly as plain text
            // or redirect — check if the content looks like a URL
            if (content.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("Bandcamp API: Statdownload returned direct URL");
                return content.Trim();
            }

            _logger.Debug("Bandcamp API: Failed to parse statdownload response");
            return null;
        }

        /// <summary>
        /// Downloads the actual FLAC archive file from a resolved download URL.
        /// Returns the raw HTTP response for streaming to disk.
        /// </summary>
        /// <param name="cookies">Session cookies from browser.</param>
        /// <param name="fileUrl">The resolved file download URL.</param>
        /// <returns>The raw HTTP response containing the file data.</returns>
        public async Task<HttpResponse> DownloadFileAsync(string cookies, string fileUrl)
        {
            _logger.Debug("Bandcamp API: Downloading file from resolved URL");

            var builder = _httpClient.CreateRequestBuilder(fileUrl, cookies);
            var request = builder.Build();
            var response = await _httpClient.ExecuteRawAsync(request);

            // Verify content type — Bandcamp should return application/zip or similar
            var contentType = response.Headers?.ContentType ?? string.Empty;
            if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("Bandcamp API: Received HTML instead of expected ZIP — download URL may be expired");
                throw new DownloadException(
                    "Received HTML response instead of expected audio archive. " +
                    "The download URL may have expired or the session cookies may be invalid.");
            }

            _logger.Debug("Bandcamp API: File download response received (Content-Type: {0})", contentType);
            return response;
        }

        private List<BandcampCollectionItem> ParseCollectionResponse(string content)
        {
            var items = new List<BandcampCollectionItem>();

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var redownloadUrls = ParseRedownloadUrls(root);

            // The collection API returns items in "items" or "tralbums" array
            var itemsElement = root.TryGetProperty("items", out var itemsEl) ? itemsEl :
                root.TryGetProperty("tralbums", out var tralbums) ? tralbums :
                default;

            if (itemsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsElement.EnumerateArray())
                {
                    var collectionItem = new BandcampCollectionItem();

                    if (item.TryGetProperty("item_id", out var itemId))
                    {
                        collectionItem.ItemId = itemId.GetInt64();
                    }

                    if (item.TryGetProperty("item_type", out var itemType))
                    {
                        collectionItem.ItemType = itemType.GetString() ?? "album";
                    }

                    if (item.TryGetProperty("band_id", out var bandId))
                    {
                        collectionItem.BandId = bandId.GetInt64();
                    }

                    if (item.TryGetProperty("sale_item_type", out var saleItemType))
                    {
                        collectionItem.SaleItemType = saleItemType.GetString();
                    }

                    if (item.TryGetProperty("sale_item_id", out var saleItemId) && saleItemId.ValueKind == JsonValueKind.Number)
                    {
                        collectionItem.SaleItemId = saleItemId.GetInt64();
                    }

                    if (item.TryGetProperty("token", out var token))
                    {
                        collectionItem.Token = token.GetString() ?? string.Empty;
                    }

                    // Try to get the item URL from various possible fields
                    if (item.TryGetProperty("item_url", out var itemUrl))
                    {
                        collectionItem.ItemUrl = itemUrl.GetString();
                    }
                    else if (item.TryGetProperty("url", out var url))
                    {
                        collectionItem.ItemUrl = url.GetString();
                    }

                    // Try to get album/track title
                    if (item.TryGetProperty("item_title", out var title))
                    {
                        collectionItem.Title = title.GetString();
                    }
                    else if (item.TryGetProperty("title", out var t))
                    {
                        collectionItem.Title = t.GetString();
                    }

                    // Try to get artist name
                    if (item.TryGetProperty("band_name", out var bandName))
                    {
                        collectionItem.BandName = bandName.GetString();
                    }
                    else if (item.TryGetProperty("artist", out var artist))
                    {
                        collectionItem.BandName = artist.GetString();
                    }

                    if (item.TryGetProperty("num_tracks", out var numTracks) && numTracks.ValueKind == JsonValueKind.Number)
                    {
                        collectionItem.TrackCount = numTracks.GetInt32();
                    }

                    if (item.TryGetProperty("release_date", out var releaseDate))
                    {
                        collectionItem.ReleaseDate = releaseDate.GetString();
                    }
                    else if (item.TryGetProperty("item_release_date", out var itemReleaseDate))
                    {
                        collectionItem.ReleaseDate = itemReleaseDate.GetString();
                    }

                    if (!string.IsNullOrWhiteSpace(collectionItem.SaleItemType) && collectionItem.SaleItemId > 0)
                    {
                        var redownloadKey = $"{collectionItem.SaleItemType}{collectionItem.SaleItemId}";
                        if (redownloadUrls.TryGetValue(redownloadKey, out var downloadPageUrl))
                        {
                            collectionItem.DownloadPageUrl = downloadPageUrl;
                        }
                    }

                    items.Add(collectionItem);
                }
            }

            _logger.Debug("Bandcamp API: Parsed {0} collection items", items.Count);
            return items;
        }

        private static Dictionary<string, string> ParseRedownloadUrls(JsonElement root)
        {
            var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!root.TryGetProperty("redownload_urls", out var redownloadUrls) ||
                redownloadUrls.ValueKind != JsonValueKind.Object)
            {
                return urls;
            }

            foreach (var property in redownloadUrls.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var url = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        urls[property.Name] = url;
                    }
                }
            }

            return urls;
        }

        private BandcampDownloadPageData ParseDownloadPagedata(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var data = new BandcampDownloadPageData();

            // Extract download URL and item info from the nested pagedata structure
            // Download pages use: digital_items -> [0] -> downloads -> {format} -> url
            // (bandcampsync reference confirms "digital_items" as the correct key)
            var itemsArray = root.TryGetProperty("digital_items", out var digitalItems)
                ? digitalItems
                : root.TryGetProperty("download_items", out var downloadItems)
                    ? downloadItems
                    : default;

            if (itemsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsArray.EnumerateArray())
                {
                    var downloadItem = new BandcampPagedataDownloadItem();

                    if (item.TryGetProperty("item_id", out var itemId))
                    {
                        downloadItem.ItemId = itemId.GetInt64();
                    }

                    if (item.TryGetProperty("item_type", out var itemType))
                    {
                        downloadItem.ItemType = itemType.GetString() ?? "album";
                    }

                    // Extract download URLs per format
                    if (item.TryGetProperty("downloads", out var downloads))
                    {
                        foreach (var formatProp in downloads.EnumerateObject())
                        {
                            if (formatProp.Value.ValueKind == JsonValueKind.Object &&
                                formatProp.Value.TryGetProperty("url", out var urlEl))
                            {
                                downloadItem.DownloadUrls[formatProp.Name] = urlEl.GetString() ?? string.Empty;

                                if (formatProp.Value.TryGetProperty("size_mb", out var sizeMbEl) &&
                                    sizeMbEl.ValueKind == JsonValueKind.Number)
                                {
                                    downloadItem.DownloadSizes[formatProp.Name] = (long)(sizeMbEl.GetDouble() * 1024 * 1024);
                                }
                            }
                        }
                    }

                    // Extract title info
                    if (item.TryGetProperty("title", out var title))
                    {
                        downloadItem.Title = title.GetString();
                    }

                    data.DownloadItems.Add(downloadItem);
                }
            }

            // Also check for a direct download_url at the root level
            if (root.TryGetProperty("download_url", out var downloadUrl))
            {
                data.DownloadUrl = downloadUrl.GetString();
            }

            _logger.Debug("Bandcamp API: Parsed download pagedata with {0} download items",
                data.DownloadItems.Count);

            return data;
        }
    }

    /// <summary>
    /// Represents an item in the user's Bandcamp collection (purchase).
    /// </summary>
    public class BandcampCollectionItem
    {
        public long ItemId { get; set; }
        public string ItemType { get; set; } = "album";
        public long BandId { get; set; }
        public string? SaleItemType { get; set; }
        public long SaleItemId { get; set; }
        public string Token { get; set; } = string.Empty;
        public string? ItemUrl { get; set; }
        public string? DownloadPageUrl { get; set; }
        public string? Title { get; set; }
        public string? BandName { get; set; }
        public int TrackCount { get; set; }
        public string? ReleaseDate { get; set; }
        public bool IsDownloadable => !string.IsNullOrWhiteSpace(DownloadPageUrl);
    }

    /// <summary>
    /// Parsed data from a Bandcamp download page's pagedata JSON.
    /// Contains download URLs per format for each purchased item.
    /// </summary>
    public class BandcampDownloadPageData
    {
        public List<BandcampPagedataDownloadItem> DownloadItems { get; set; } = new();
        public string? DownloadUrl { get; set; }
    }

    /// <summary>
    /// A single downloadable item parsed from the pagedata JSON on a Bandcamp download page.
    /// Contains per-format download URLs (e.g., FLAC, MP3).
    /// </summary>
    public class BandcampPagedataDownloadItem
    {
        public long ItemId { get; set; }
        public string ItemType { get; set; } = "album";
        public string? Title { get; set; }
        public Dictionary<string, string> DownloadUrls { get; set; } = new();
        public Dictionary<string, long> DownloadSizes { get; set; } = new();
    }

    /// <summary>
    /// Exception thrown when a Bandcamp download operation fails.
    /// </summary>
    public class DownloadException : Exception
    {
        public DownloadException(string message)
            : base(message)
        {
        }

        public DownloadException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
