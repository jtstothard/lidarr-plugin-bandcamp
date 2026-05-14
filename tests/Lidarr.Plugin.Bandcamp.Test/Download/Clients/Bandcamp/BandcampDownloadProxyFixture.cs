using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    public class BandcampDownloadProxyFixture : IDisposable
    {
        private readonly Mock<IHttpClient> _httpClientMock;
        private readonly BandcampDownloadProxy _subject;
        private readonly string _tempRoot;

        public BandcampDownloadProxyFixture()
        {
            _httpClientMock = new Mock<IHttpClient>();
            var logger = LogManager.GetCurrentClassLogger();
            var bandcampHttpClient = new BandcampHttpClient(_httpClientMock.Object, logger);
            var apiClient = new BandcampApiClient(bandcampHttpClient, logger);
            _subject = new BandcampDownloadProxy(apiClient, bandcampHttpClient, logger);
            _tempRoot = Path.Combine(Path.GetTempPath(), "bandcamp-proxy-tests", Guid.NewGuid().ToString("N"));
        }

        [Fact]
        public async Task ExecuteDownloadAsync_DownloadPageUrlWithFormatFragment_DoesNotQueryCollection()
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
                        "https://bandcamp.com/download?type=album&id=111" => CreateStringResponse(
                            @"<html><head><script type=""text/javascript"">var pagedata = {
                                ""digital_items"": [{
                                    ""item_id"": 111,
                                    ""item_type"": ""album"",
                                    ""title"": ""Fresh"",
                                    ""downloads"": {
                                        ""flac"": { ""url"": ""https://bandcamp.com/statdownload/album/111?sig=flac"", ""size_mb"": 12 }
                                    }
                                }]
                            };</script></head><body></body></html>",
                            request),
                        "https://bandcamp.com/statdownload/album/111?sig=flac&.format=flac&.json=true" => CreateStringResponse(
                            @"{""url"":""https://files.example.com/fresh.flac""}",
                            request),
                        _ => CreateStringResponse(@"{""items"": []}", request)
                    };
                });

            var fileRequest = new HttpRequest("https://files.example.com/fresh.flac");
            var fileHeaders = new HttpHeader { { "Content-Type", "audio/flac" } };
            var fileBytes = new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22 };
            var fileResponse = new HttpResponse(fileRequest, fileHeaders, fileBytes, HttpStatusCode.OK);

            _httpClientMock
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync(fileResponse);

            var item = new BandcampDownloadItem
            {
                AlbumUrl = "https://bandcamp.com/download?type=album&id=111#format=flac",
                Title = "Fresh - Fresh [FLAC]",
                OutputPath = _tempRoot,
                MediaFormat = "FLAC"
            };

            typeof(BandcampDownloadItem)
                .GetProperty("Cookies", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                !.SetValue(item, "identity=testcookie");

            await _subject.ExecuteDownloadAsync(item, CancellationToken.None);

            var files = Directory.GetFiles(_tempRoot);
            Assert.Single(files);
            Assert.EndsWith(".flac", files[0], StringComparison.OrdinalIgnoreCase);

            _httpClientMock.Verify(c => c.GetAsync(It.Is<HttpRequest>(r =>
                r.Url.FullUri.Contains("/api/fancollection/1/collection_items", StringComparison.OrdinalIgnoreCase))), Times.Never);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }

        private static HttpResponse CreateStringResponse(string content, HttpRequest request, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new HttpResponse(request, new HttpHeader(), content, statusCode);
        }
    }
}
