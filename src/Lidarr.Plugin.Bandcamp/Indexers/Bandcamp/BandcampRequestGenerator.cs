using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace NzbDrone.Core.Indexers.Bandcamp
{
    /// <summary>
    /// Generates search requests against Bandcamp's public search page.
    /// Bandcamp does not have a formal API — we search via the /search endpoint
    /// and parse the HTML response.
    /// </summary>
    public class BandcampRequestGenerator : IIndexerRequestGenerator
    {
        private const string SearchUrlTemplate = "{0}/search?q={1}&item_type=a";

        private readonly BandcampIndexerSettings _settings;
        private readonly Logger _logger;

        public BandcampRequestGenerator(BandcampIndexerSettings settings, Logger logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            // Bandcamp has no RSS feed — return empty chain
            return new IndexerPageableRequestChain();
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            if (string.IsNullOrWhiteSpace(_settings.Cookies))
            {
                _logger.Debug("Bandcamp cookies not configured, skipping album search");
                return pageableRequests;
            }

            var query = $"{searchCriteria.CleanArtistQuery}+{searchCriteria.CleanAlbumQuery}";
            pageableRequests.Add(BuildSearchRequests(query));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            if (string.IsNullOrWhiteSpace(_settings.Cookies))
            {
                _logger.Debug("Bandcamp cookies not configured, skipping artist search");
                return pageableRequests;
            }

            var query = searchCriteria.CleanArtistQuery;
            pageableRequests.Add(BuildSearchRequests(query));

            return pageableRequests;
        }

        private IEnumerable<IndexerRequest> BuildSearchRequests(string query)
        {
            var baseUrl = _settings.BaseUrl.Trim().TrimEnd('/');
            var searchUrl = string.Format(SearchUrlTemplate, baseUrl, Uri.EscapeDataString(query));

            _logger.Debug("Bandcamp search URL: {0}", searchUrl);

            var request = new IndexerRequest(searchUrl, HttpAccept.Html);

            // Inject cookies from settings
            if (!string.IsNullOrWhiteSpace(_settings.Cookies))
            {
                request.HttpRequest.Headers.Set("Cookie", _settings.Cookies);
            }

            // Browser-like headers to get full search results
            request.HttpRequest.Headers.Set("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.HttpRequest.Headers.Set("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.HttpRequest.Headers.Set("Accept-Language", "en-US,en;q=0.9");

            yield return request;
        }
    }
}
