using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Bandcamp;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Parser.Model;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Indexers.Bandcamp
{
    public class BandcampParserFixture
    {
        private readonly BandcampIndexerSettings _settings;
        private readonly Logger _logger;

        public BandcampParserFixture()
        {
            _settings = new BandcampIndexerSettings();
            _logger = LogManager.GetCurrentClassLogger();
        }

        private static string LoadFixture(string filename)
        {
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var fixturePath = Path.Combine(
                assemblyDir,
                "Indexers",
                "Bandcamp",
                "BandcampParserFixtureData",
                filename);

            if (!File.Exists(fixturePath))
            {
                throw new FileNotFoundException($"Test fixture not found: {fixturePath}");
            }

            return File.ReadAllText(fixturePath);
        }

        private IndexerResponse CreateResponse(string html, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var httpRequest = new HttpRequest("https://bandcamp.com/search?q=test&item_type=a");
            var httpResponse = new HttpResponse(httpRequest, new HttpHeader(), html, statusCode);
            var indexerRequest = new IndexerRequest(httpRequest);
            return new IndexerResponse(indexerRequest, httpResponse);
        }

        [Fact]
        public void ParseResponse_ValidHtml_ReturnsReleaseInfoList()
        {
            // Arrange
            var html = LoadFixture("search_results.html");
            var response = CreateResponse(html);
            var parser = new BandcampParser(_settings, _logger);

            // Act
            var results = parser.ParseResponse(response);

            // Assert
            Assert.NotEmpty(results);
            Assert.Equal(3, results.Count);

            // Results are ordered by PublishDate descending
            // In Rainbows (2007) > Dummy (1994) — but our HTML has 1997, 2007, 1994
            // Descending order: In Rainbows (2007), OK Computer (1997), Dummy (1994)
            var firstResult = results[0];
            Assert.Contains("In Rainbows", firstResult.Title);
            Assert.Equal("Radiohead", firstResult.Artist);
            Assert.Equal("In Rainbows", firstResult.Album);
            Assert.Equal("FLAC", firstResult.Codec);
            Assert.Equal("FLAC", firstResult.Container);
            Assert.Equal(nameof(BandcampDownloadProtocol), firstResult.DownloadProtocol);
            Assert.True(firstResult.PublishDate > DateTime.MinValue);

            // Verify OK Computer
            var secondResult = results[1];
            Assert.Contains("OK Computer", secondResult.Title);
            Assert.Equal("Radiohead", secondResult.Artist);

            // Verify Dummy
            var thirdResult = results[2];
            Assert.Contains("Dummy", thirdResult.Title);
            Assert.Equal("Portishead", thirdResult.Artist);
        }

        [Fact]
        public void ParseResponse_EmptyResults_ReturnsEmptyList()
        {
            // Arrange
            var html = LoadFixture("empty_results.html");
            var response = CreateResponse(html);
            var parser = new BandcampParser(_settings, _logger);

            // Act
            var results = parser.ParseResponse(response);

            // Assert
            Assert.Empty(results);
        }

        [Fact]
        public void ParseResponse_MalformedHtml_SkipsBadResults()
        {
            // Arrange
            var html = LoadFixture("malformed_results.html");
            var response = CreateResponse(html);
            var parser = new BandcampParser(_settings, _logger);

            // Act
            var results = parser.ParseResponse(response);

            // Assert
            // Should parse the 2 valid results with HTML-encoded characters
            // and skip the block with no heading URL and the non-bandcamp URL block
            Assert.NotEmpty(results);

            // First valid result should have HTML-decoded title
            var decodedResult = results.FirstOrDefault(r => r.Album.Contains("Blue"));
            Assert.NotNull(decodedResult);
            Assert.Equal("The \"Blue\" Album (Deluxe Edition)", decodedResult.Album);
            Assert.Equal("Artist & Friends", decodedResult.Artist);

            // Second valid result should handle special chars
            var cafeResult = results.FirstOrDefault(r => r.Album.Contains("Caf"));
            Assert.NotNull(cafeResult);
            Assert.Equal("DJ Näme", cafeResult.Artist);

            // Blocks without proper bandcamp URLs should be skipped
            // The "no heading" block has no ItemUrlRegex match -> null
            // The "broken URL" block has no bandcamp.com/album/ match -> null
            var brokenCount = results.Count(r => r.Album == "Unknown Album" || r.Title.Contains("No Heading"));
            Assert.Equal(0, brokenCount);
        }

        [Fact]
        public void ParseResponse_NonOkStatus_ThrowsIndexerException()
        {
            // Arrange
            var html = "<html><body>Forbidden</body></html>";
            var response = CreateResponse(html, HttpStatusCode.Forbidden);
            var parser = new BandcampParser(_settings, _logger);

            // Act & Assert
            Assert.Throws<IndexerException>(() => parser.ParseResponse(response));
        }

        [Fact]
        public void ParseResponse_EmptyContent_ReturnsEmptyList()
        {
            // Arrange
            var response = CreateResponse("");
            var parser = new BandcampParser(_settings, _logger);

            // Act
            var results = parser.ParseResponse(response);

            // Assert
            Assert.Empty(results);
        }

        [Fact]
        public void ParseResponse_SingleResult_ReturnsOneRelease()
        {
            // Arrange
            var html = @"<html><body>
<li class=""searchresult data-item=""album"">
  <div class=""heading"">
    <a href=""https://testartist.bandcamp.com/album/test-album"">Test Album</a>
  </div>
  <div class=""subhead"">
    by Test Artist
  </div>
  <div class=""released"">released December 1, 2023</div>
</li>
</body></html>";
            var response = CreateResponse(html);
            var parser = new BandcampParser(_settings, _logger);

            // Act
            var results = parser.ParseResponse(response);

            // Assert
            Assert.Single(results);
            Assert.Equal("Test Artist", results[0].Artist);
            Assert.Equal("Test Album", results[0].Album);
            Assert.Equal("Test Artist - Test Album", results[0].Title);
            Assert.Equal(new DateTime(2023, 12, 1), results[0].PublishDate.Date);
        }

        [Fact]
        public void ParseResponse_ResultWithNoDate_UsesCurrentDate()
        {
            // Arrange
            var html = @"<html><body>
<li class=""searchresult data-item=""album"">
  <div class=""heading"">
    <a href=""https://nodate.bandcamp.com/album/no-date-album"">No Date Album</a>
  </div>
  <div class=""subhead"">
    by No Date Artist
  </div>
</li>
</body></html>";
            var response = CreateResponse(html);
            var parser = new BandcampParser(_settings, _logger);

            // Act
            var results = parser.ParseResponse(response);

            // Assert
            Assert.Single(results);
            // When no date is found, parser uses DateTime.UtcNow
            Assert.True(results[0].PublishDate.Date <= DateTime.UtcNow.Date);
            Assert.True(results[0].PublishDate > DateTime.MinValue);
        }

        [Fact]
        public void ParseResponse_ReleaseInfo_HasExpectedDefaults()
        {
            // Arrange
            var html = @"<html><body>
<li class=""searchresult data-item=""album"">
  <div class=""heading"">
    <a href=""https://defaults.bandcamp.com/album/default-test"">Default Test</a>
  </div>
  <div class=""subhead"">
    by Default Artist
  </div>
  <div class=""released"">released June 1, 2024</div>
</li>
</body></html>";
            var response = CreateResponse(html);
            var parser = new BandcampParser(_settings, _logger);

            // Act
            var results = parser.ParseResponse(response);

            // Assert
            Assert.Single(results);
            var release = results[0];
            Assert.Equal("FLAC", release.Codec);
            Assert.Equal("FLAC", release.Container);
            Assert.Equal(nameof(BandcampDownloadProtocol), release.DownloadProtocol);
            Assert.Equal(release.InfoUrl, release.DownloadUrl);
            Assert.True(release.Size > 0); // ~30MB per track * 10 tracks
            Assert.False(string.IsNullOrEmpty(release.Guid));
        }
    }
}
