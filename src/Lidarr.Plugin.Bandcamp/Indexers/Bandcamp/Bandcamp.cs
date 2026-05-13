using System;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
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
    }
}
