using System;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Indexers.Bandcamp
{
    /// <summary>
    /// Lidarr indexer for Bandcamp — supports search (no RSS).
    /// Uses cookie-based authentication for accessing Bandcamp's catalog.
    /// </summary>
    public class BandcampIndexer : HttpIndexerBase<BandcampIndexerSettings>
    {
        public override string Name => "Bandcamp";
        public override string Protocol => nameof(BandcampDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 50;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(2);

        public BandcampIndexer(IHttpClient httpClient,
                               IIndexerStatusService indexerStatusService,
                               IConfigService configService,
                               IParsingService parsingService,
                               Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new BandcampRequestGenerator(Settings, _logger);
        }

        public override IParseIndexerResponse GetParser()
        {
            return new BandcampParser(Settings, _logger);
        }

        /// <summary>
        /// Bandcamp is search-only and intentionally has no RSS/recent feed.
        /// Lidarr's base HttpIndexerBase test always probes GetRecentRequests(),
        /// which produces the misleading "No rss feed query available" failure for
        /// search-only plugins. Validate by running a small real artist search instead.
        /// </summary>
        protected override async Task<ValidationFailure> TestConnection()
        {
            if (Settings.Cookies.IsNullOrWhiteSpace())
            {
                return new ValidationFailure("Cookies", "Bandcamp identity cookie is required for search.");
            }

            try
            {
                var parser = GetParser();
                var generator = GetRequestGenerator();
                var criteria = new ArtistSearchCriteria
                {
                    Artist = new Artist
                    {
                        Name = "Radiohead"
                    }
                };

                var firstRequest = generator.GetSearchRequests(criteria).GetAllTiers().FirstOrDefault()?.FirstOrDefault();

                if (firstRequest == null)
                {
                    return new ValidationFailure(string.Empty, "No Bandcamp search query could be generated.");
                }

                var releases = await FetchPage(firstRequest, parser).ConfigureAwait(false);

                if (releases.Empty())
                {
                    return new ValidationFailure(string.Empty, "Bandcamp search completed, but no results were parsed. Check the identity cookie and Bandcamp response format.");
                }

                _logger.Debug("Bandcamp indexer: Search test passed with {0} parsed result(s)", releases.Count);
                return default!;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Bandcamp indexer search test failed");
                return new ValidationFailure(string.Empty, "Unable to connect to Bandcamp indexer. " + ex.Message);
            }
        }
    }
}
