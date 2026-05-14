using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Bandcamp;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Indexers.Bandcamp
{
    public class BandcampIndexerFixture
    {
        private readonly Mock<IHttpClient> _httpClientMock;
        private readonly BandcampIndexer _subject;

        public BandcampIndexerFixture()
        {
            _httpClientMock = new Mock<IHttpClient>();
            var logger = LogManager.GetCurrentClassLogger();
            var bandcampHttpClient = new BandcampHttpClient(_httpClientMock.Object, logger);
            var apiClient = new BandcampApiClient(bandcampHttpClient, logger);

            _subject = new BandcampIndexer(
                _httpClientMock.Object,
                apiClient,
                Mock.Of<IIndexerStatusService>(),
                Mock.Of<IConfigService>(),
                Mock.Of<IParsingService>(),
                logger);

            _subject.Definition = new IndexerDefinition
            {
                Id = 42,
                Name = "Bandcamp",
                Priority = 25,
                Settings = new BandcampIndexerSettings
                {
                    Cookies = "testcookie"
                }
            };
        }

        [Fact]
        public async Task Fetch_AlbumSearch_UsesCollectionRedownloads_AndReturnsOneReleasePerSizedFormat()
        {
            _httpClientMock
                .Setup(c => c.GetAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync((HttpRequest request) =>
                {
                    return request.Url.FullUri switch
                    {
                        "https://bandcamp.com" => CreateStringResponse(
                            @"<html><body><div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:12345678,&quot;isLoggedIn&quot;:true}}}""></div></body></html>",
                            request),
                        "https://bandcamp.com/api/fancollection/1/collection_items" => CreateStringResponse(
                            @"{
                                ""redownload_urls"": {
                                    ""a111"": ""https://bandcamp.com/download?type=album&id=111""
                                },
                                ""items"": [
                                    {
                                        ""item_id"": 111,
                                        ""item_type"": ""album"",
                                        ""sale_item_type"": ""a"",
                                        ""sale_item_id"": 111,
                                        ""token"": ""tok-111"",
                                        ""item_url"": ""https://fresh.bandcamp.com/album/fresh"",
                                        ""item_title"": ""Fresh"",
                                        ""band_name"": ""Fresh"",
                                        ""release_date"": ""2024-01-02T00:00:00Z""
                                    },
                                    {
                                        ""item_id"": 222,
                                        ""item_type"": ""album"",
                                        ""sale_item_type"": ""a"",
                                        ""sale_item_id"": 222,
                                        ""token"": ""tok-222"",
                                        ""item_url"": ""https://other.bandcamp.com/album/not-fresh"",
                                        ""item_title"": ""Other Album"",
                                        ""band_name"": ""Other Artist"",
                                        ""release_date"": ""2023-01-02T00:00:00Z""
                                    }
                                ]
                            }",
                            request),
                        "https://bandcamp.com/download?type=album&id=111" => CreateStringResponse(
                            @"<html><head><script type=""text/javascript"">var pagedata = {
                                ""digital_items"": [{
                                    ""item_id"": 111,
                                    ""item_type"": ""album"",
                                    ""title"": ""Fresh"",
                                    ""downloads"": {
                                        ""flac"": { ""url"": ""https://bandcamp.com/statdownload/album/111?sig=flac"", ""size_mb"": 123 },
                                        ""mp3-320"": { ""url"": ""https://bandcamp.com/statdownload/album/111?sig=mp3"", ""size_mb"": 88 },
                                        ""ogg-vorbis"": { ""url"": ""https://bandcamp.com/statdownload/album/111?sig=ogg"", ""size_mb"": 0 }
                                    }
                                }]
                            };</script></head><body></body></html>",
                            request),
                        _ => CreateStringResponse(@"{""items"": []}", request)
                    };
                });

            var criteria = new AlbumSearchCriteria
            {
                Artist = new Artist { Name = "Fresh" },
                AlbumTitle = "Fresh"
            };

            var results = await _subject.Fetch(criteria);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.StartsWith("42_bandcamp-111-", r.Guid));
            Assert.All(results, r => Assert.Equal("Bandcamp", r.Indexer));
            Assert.All(results, r => Assert.Equal(nameof(BandcampDownloadProtocol), r.DownloadProtocol));
            Assert.All(results, r => Assert.Equal("Fresh", r.Artist));
            Assert.All(results, r => Assert.Equal("Fresh", r.Album));
            Assert.DoesNotContain(results, r => r.Title.Contains("OGG", StringComparison.OrdinalIgnoreCase));

            var flac = Assert.Single(results.Where(r => r.Title.Contains("[FLAC]", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(123L * 1024 * 1024, flac.Size);
            Assert.Equal("FLAC", flac.Codec);
            Assert.Equal("FLAC", flac.Container);
            Assert.Contains("#format=flac", flac.DownloadUrl, StringComparison.OrdinalIgnoreCase);

            var mp3 = Assert.Single(results.Where(r => r.Title.Contains("[MP3 320]", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(88L * 1024 * 1024, mp3.Size);
            Assert.Equal("MP3", mp3.Codec);
            Assert.Equal("MP3", mp3.Container);
            Assert.Contains("#format=mp3-320", mp3.DownloadUrl, StringComparison.OrdinalIgnoreCase);
        }

        private static HttpResponse CreateStringResponse(string content, HttpRequest request, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new HttpResponse(request, new HttpHeader(), content, statusCode);
        }
    }
}
