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
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
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
                                    ""a111"": ""https://bandcamp.com/download?type=album&id=111"",
                                    ""a222"": ""https://bandcamp.com/download?type=album&id=111""
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
                                        ""release_date"": null
                                    },
                                    {
                                        ""item_id"": 222,
                                        ""item_type"": ""album"",
                                        ""sale_item_type"": ""a"",
                                        ""sale_item_id"": 222,
                                        ""token"": ""tok-222"",
                                        ""item_url"": ""https://fresh.bandcamp.com/album/fresh"",
                                        ""item_title"": ""Fresh"",
                                        ""band_name"": ""Fresh"",
                                        ""release_date"": null
                                    }
                                ]
                            }",
                            request),
                        "https://bandcamp.com/download?type=album&id=111" => CreateStringResponse(
                            @"<html><body><div id=""pagedata"" data-blob=""{&quot;digital_items&quot;:[{&quot;item_id&quot;:111,&quot;item_type&quot;:&quot;album&quot;,&quot;title&quot;:&quot;Fresh&quot;,&quot;downloads&quot;:{&quot;flac&quot;:{&quot;url&quot;:&quot;https://bandcamp.com/statdownload/album/111?sig=flac&quot;,&quot;size_mb&quot;:&quot;123MB&quot;},&quot;mp3-320&quot;:{&quot;url&quot;:&quot;https://bandcamp.com/statdownload/album/111?sig=mp3&quot;,&quot;size_mb&quot;:&quot;88.5MB&quot;},&quot;vorbis&quot;:{&quot;url&quot;:&quot;https://bandcamp.com/statdownload/album/111?sig=ogg&quot;,&quot;size_mb&quot;:&quot;0MB&quot;}}}]}""></div></body></html>",
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
            Assert.All(results, r => Assert.StartsWith("42_bandcamp-https://fresh.bandcamp.com/album/fresh-", r.Guid));
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
            Assert.Equal((long)(88.5 * 1024 * 1024), mp3.Size);
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
