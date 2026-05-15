using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
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
                            @"<html><body><div id=""pagedata"" data-blob=""{&quot;digital_items&quot;:[{&quot;item_id&quot;:111,&quot;item_type&quot;:&quot;album&quot;,&quot;title&quot;:&quot;Fresh&quot;,&quot;downloads&quot;:{&quot;mp3-v0&quot;:{&quot;url&quot;:&quot;https://bandcamp.com/statdownload/album/111?sig=v0&quot;,&quot;size_mb&quot;:&quot;41.2MB&quot;},&quot;flac&quot;:{&quot;url&quot;:&quot;https://bandcamp.com/statdownload/album/111?sig=flac&quot;,&quot;size_mb&quot;:&quot;123MB&quot;},&quot;mp3-320&quot;:{&quot;url&quot;:&quot;https://bandcamp.com/statdownload/album/111?sig=mp3&quot;,&quot;size_mb&quot;:&quot;88.5MB&quot;},&quot;vorbis&quot;:{&quot;url&quot;:&quot;https://bandcamp.com/statdownload/album/111?sig=ogg&quot;,&quot;size_mb&quot;:&quot;28MB&quot;}}}]}""></div></body></html>",
                            request),
                        "https://fresh.bandcamp.com/album/fresh" => CreateStringResponse(
                            @"<html><body><script data-tralbum=""{&quot;trackinfo&quot;:[{&quot;duration&quot;:113.078},{&quot;duration&quot;:87.1942},{&quot;duration&quot;:41.3711},{&quot;duration&quot;:147.081},{&quot;duration&quot;:151.816},{&quot;duration&quot;:84.0},{&quot;duration&quot;:89.0},{&quot;duration&quot;:93.0},{&quot;duration&quot;:110.0},{&quot;duration&quot;:140.0},{&quot;duration&quot;:178.958}]}""></script></body></html>",
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

            Assert.Equal(4, results.Count);
            Assert.All(results, r => Assert.StartsWith("42_bandcamp-https://fresh.bandcamp.com/album/fresh-", r.Guid));
            Assert.All(results, r => Assert.Equal("Bandcamp", r.Indexer));
            Assert.All(results, r => Assert.Equal(nameof(BandcampDownloadProtocol), r.DownloadProtocol));
            Assert.All(results, r => Assert.Equal("Fresh", r.Artist));
            Assert.All(results, r => Assert.Equal("Fresh", r.Album));
            Assert.DoesNotContain(results, r => r.Title.Contains("OGG", StringComparison.OrdinalIgnoreCase));

            var v0 = Assert.Single(results.Where(r => r.Title.Contains("[MP3 VBR V0]", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal((long)(41.2 * 1024 * 1024), v0.Size);
            Assert.Equal("MP3", v0.Codec);
            Assert.Equal("MP3", v0.Container);
            Assert.Contains("#format=mp3-v0", v0.DownloadUrl, StringComparison.OrdinalIgnoreCase);

            var flac = Assert.Single(results.Where(r => r.Title.Contains("[FLAC]", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(123L * 1024 * 1024, flac.Size);
            Assert.Equal("FLAC", flac.Codec);
            Assert.Equal("FLAC", flac.Container);
            Assert.Contains("#format=flac", flac.DownloadUrl, StringComparison.OrdinalIgnoreCase);

            var vorbis = Assert.Single(results.Where(r => r.Title.Contains("[Vorbis Q6]", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(28L * 1024 * 1024, vorbis.Size);
            Assert.Equal("OGG", vorbis.Codec);
            Assert.Equal("OGG", vorbis.Container);
            Assert.Contains("#format=vorbis", vorbis.DownloadUrl, StringComparison.OrdinalIgnoreCase);

            var mp3 = Assert.Single(results.Where(r => r.Title.Contains("[MP3 320]", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal((long)(88.5 * 1024 * 1024), mp3.Size);
            Assert.Equal("MP3", mp3.Codec);
            Assert.Equal("MP3", mp3.Container);
            Assert.Contains("#format=mp3-320", mp3.DownloadUrl, StringComparison.OrdinalIgnoreCase);

            _httpClientMock.Verify(c => c.ExecuteAsync(It.Is<HttpRequest>(r =>
                r.Url.FullUri == "https://bandcamp.com/download?type=album&id=111")), Times.Once);
            _httpClientMock.Verify(c => c.ExecuteAsync(It.Is<HttpRequest>(r =>
                r.Url.FullUri == "https://fresh.bandcamp.com/album/fresh")), Times.Once);
        }

        [Fact]
        public async Task Fetch_AlbumSearch_NormalizesParserHostileBandcampTitles()
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
                                    ""a111"": ""https://bandcamp.com/download?type=album&id=111""
                                },
                                ""items"": [
                                    {
                                        ""item_id"": 111,
                                        ""item_type"": ""album"",
                                        ""sale_item_type"": ""a"",
                                        ""sale_item_id"": 111,
                                        ""token"": ""tok-111"",
                                        ""item_url"": ""https://artist.bandcamp.com/album/album-title"",
                                        ""item_title"": ""Artist Name - Album Title - Single"",
                                        ""band_name"": ""Artist Name"",
                                        ""release_date"": null
                                    }
                                ]
                            }",
                            request),
                        "https://bandcamp.com/download?type=album&id=111" => CreateStringResponse(
                            @"<html><body><div id=""pagedata"" data-blob=""{&quot;digital_items&quot;:[{&quot;item_id&quot;:111,&quot;item_type&quot;:&quot;album&quot;,&quot;title&quot;:&quot;Album Title&quot;,&quot;downloads&quot;:{&quot;flac&quot;:{&quot;url&quot;:&quot;https://bandcamp.com/statdownload/album/111?sig=flac&quot;,&quot;size_mb&quot;:&quot;123MB&quot;}}}]}""></div></body></html>",
                            request),
                        "https://artist.bandcamp.com/album/album-title" => CreateStringResponse(
                            @"<html><body><script data-tralbum=""{&quot;trackinfo&quot;:[{&quot;duration&quot;:200.0}]}""></script></body></html>",
                            request),
                        _ => CreateStringResponse(@"{""items"": []}", request)
                    };
                });

            var criteria = new AlbumSearchCriteria
            {
                Artist = new Artist { Name = "Artist Name" },
                AlbumTitle = "Album Title"
            };

            var result = Assert.Single(await _subject.Fetch(criteria));

            Assert.Equal("Artist Name - Album Title [FLAC]", result.Title);
            Assert.Equal("Artist Name", result.Artist);
            Assert.Equal("Album Title", result.Album);
        }

        [Theory]
        [InlineData("Artist Name", "Artist Name - Album Title", "FLAC", "Artist Name - Album Title [FLAC]", "Artist Name", "Album Title")]
        [InlineData("Artist Name", "\"Album Title\"", "FLAC", "Artist Name - Album Title [FLAC]", "Artist Name", "Album Title")]
        [InlineData("Artist Name", "Album Title - Single", "FLAC", "Artist Name - Album Title [FLAC]", "Artist Name", "Album Title")]
        [InlineData("Artist Name", "Album Title (EP)", "FLAC", "Artist Name - Album Title [FLAC]", "Artist Name", "Album Title")]
        [InlineData("Bartees Strange", "Province / Ever New", "FLAC", "Bartees Strange - Province / Ever New [FLAC]", "Bartees Strange", "Province / Ever New")]
        [InlineData("Troye Sivan", "We're My OTP", "MP3 320", "Troye Sivan - We're My OTP [MP3 320]", "Troye Sivan", "We're My OTP")]
        public void BuildReleaseTitle_ShouldProduceParserFriendlyTitles(string artistName,
                                                                        string albumTitle,
                                                                        string formatLabel,
                                                                        string expectedTitle,
                                                                        string expectedArtist,
                                                                        string expectedAlbum)
        {
            var title = BandcampIndexer.BuildReleaseTitle(artistName, albumTitle, formatLabel);

            Assert.Equal(expectedTitle, title);

            var parsed = Parser.ParseAlbumTitle(title);

            Assert.NotNull(parsed);
            Assert.Equal(expectedArtist, parsed!.ArtistName);
            Assert.Equal(expectedAlbum, parsed.AlbumTitle);
        }

        [Theory]
        [InlineData("Artist Name", "Artist Name - Album Title", "Album Title")]
        [InlineData("Artist Name", "Album Title - Single", "Album Title")]
        [InlineData("Artist Name", "Album Title [EP]", "Album Title")]
        [InlineData("Artist Name", "\"Album Title\"", "Album Title")]
        public void NormalizeAlbumTitle_ShouldStripParserHostileNoise(string artistName, string albumTitle, string expected)
        {
            var normalized = BandcampIndexer.NormalizeAlbumTitle(artistName, albumTitle);

            Assert.Equal(expected, normalized);
        }

        [Fact]
        public async Task Fetch_AlbumSearch_WithMultipleReleasesSameTitle_FiltersByTrackCount()
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
                                    ""a222"": ""https://bandcamp.com/download?type=album&id=222""
                                },
                                ""items"": [
                                    {
                                        ""item_id"": 111,
                                        ""item_type"": ""album"",
                                        ""sale_item_type"": ""a"",
                                        ""sale_item_id"": 111,
                                        ""token"": ""tok-111"",
                                        ""item_url"": ""https://martha.bandcamp.com/album/please-dont-take-me-back-single"",
                                        ""item_title"": ""Please Don't Take Me Back"",
                                        ""band_name"": ""Martha"",
                                        ""num_tracks"": 2,
                                        ""release_date"": null
                                    },
                                    {
                                        ""item_id"": 222,
                                        ""item_type"": ""album"",
                                        ""sale_item_type"": ""a"",
                                        ""sale_item_id"": 222,
                                        ""token"": ""tok-222"",
                                        ""item_url"": ""https://martha.bandcamp.com/album/please-dont-take-me-back-album"",
                                        ""item_title"": ""Please Don't Take Me Back"",
                                        ""band_name"": ""Martha"",
                                        ""num_tracks"": 11,
                                        ""release_date"": null
                                    }
                                ]
                            }",
                            request),
                        "https://bandcamp.com/download?type=album&id=111" => CreateStringResponse(
                            @"<html><body><div id=""pagedata"" data-blob=""{&quot;digital_items&quot;:[{&quot;item_id&quot;:111,&quot;item_type&quot;:&quot;album&quot;,&quot;title&quot;:&quot;Please Don't Take Me Back&quot;,&quot;downloads&quot;:{&quot;flac&quot;:{&quot;url&quot;:&quot;https://bandcamp.com/statdownload/album/111?sig=flac&quot;,&quot;size_mb&quot;:&quot;25MB&quot;}}}]}""></div></body></html>",
                            request),
                        "https://martha.bandcamp.com/album/please-dont-take-me-back-single" => CreateStringResponse(
                            @"<html><body><script data-tralbum=""{&quot;trackinfo&quot;:[{&quot;duration&quot;:200.0},{&quot;duration&quot;:180.0}]}""></script></body></html>",
                            request),
                        _ => CreateStringResponse(@"{""items"": []}", request)
                    };
                });

            var criteria = new AlbumSearchCriteria
            {
                Artist = new Artist { Name = "Martha" },
                AlbumTitle = "Please Don't Take Me Back"
            };

            // Create a mock album with release specifying expected track count
            var album = new Album
            {
                Title = "Please Don't Take Me Back",
                AlbumReleases = new LazyLoaded<List<AlbumRelease>>(new List<AlbumRelease>
                {
                    new AlbumRelease
                    {
                        TrackCount = 2,
                        Title = "Please Don't Take Me Back"
                    }
                })
            };
            criteria.Albums = new List<Album> { album };

            var results = await _subject.Fetch(criteria);

            // Should only return the 2-track single, not the 11-track album
            Assert.Single(results);
            Assert.Equal("Martha", results[0].Artist);
            Assert.Equal("Please Don't Take Me Back", results[0].Album);
            Assert.Contains("[2 tracks]", results[0].Title);
            Assert.DoesNotContain("[11 tracks]", results[0].Title);
        }

        private static HttpResponse CreateStringResponse(string content, HttpRequest request, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new HttpResponse(request, new HttpHeader(), content, statusCode);
        }
    }
}
