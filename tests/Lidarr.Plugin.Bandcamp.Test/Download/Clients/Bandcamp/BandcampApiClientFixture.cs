using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    public class BandcampApiClientFixture
    {
        private readonly Mock<IHttpClient> _innerHttpClientMock;
        private readonly BandcampHttpClient _bandcampHttpClient;
        private readonly Logger _logger;

        public BandcampApiClientFixture()
        {
            _innerHttpClientMock = new Mock<IHttpClient>();
            _logger = LogManager.GetCurrentClassLogger();
            _bandcampHttpClient = new BandcampHttpClient(_innerHttpClientMock.Object, _logger);
        }

        /// <summary>
        /// Creates a BandcampApiClient with a real BandcampHttpClient backed by a mocked IHttpClient.
        /// </summary>
        private BandcampApiClient CreateClient()
        {
            return new BandcampApiClient(_bandcampHttpClient, _logger);
        }

        /// <summary>
        /// Creates an HttpResponse with string content for simulating Bandcamp page responses.
        /// </summary>
        private static HttpResponse CreateStringResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var request = new HttpRequest("https://bandcamp.com");
            return new HttpResponse(request, new HttpHeader(), content, statusCode);
        }

        /// <summary>
        /// Sets up the mock IHttpClient to return the given response for any GetAsync call.
        /// </summary>
        private void SetupGetAsync(HttpResponse response)
        {
            _innerHttpClientMock
                .Setup(c => c.GetAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync(response);
        }

        /// <summary>
        /// Sets up the mock IHttpClient to return the given response for any ExecuteAsync call.
        /// </summary>
        private void SetupExecuteAsync(HttpResponse response)
        {
            _innerHttpClientMock
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync(response);
        }

        #region ResolveFanIdAsync Tests

        [Fact]
        public async Task ResolveFanIdAsync_ValidPagedata_ReturnsFanId()
        {
            // Arrange — simulate a Bandcamp homepage with data-blob containing fanId
            var html = @"<html><head></head><body>
                <div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:12345678,&quot;isLoggedIn&quot;:true}}}""></div>
                </body></html>";

            SetupGetAsync(CreateStringResponse(html));
            var client = CreateClient();

            // Act
            var fanId = await client.ResolveFanIdAsync("identity=testcookie");

            // Assert
            Assert.NotNull(fanId);
            Assert.Equal(12345678, fanId.Value);
        }

        [Fact]
        public async Task ResolveFanIdAsync_NoFanIdInPage_ReturnsNull()
        {
            // Arrange — page without fan_id (e.g., not logged in)
            var html = @"
                <html><head>
                <script type=""text/javascript"">
                var pagedata = {
                    ""some_other_data"": ""value""
                };
                </script>
                </head><body></body></html>";

            SetupGetAsync(CreateStringResponse(html));
            var client = CreateClient();

            // Act
            var fanId = await client.ResolveFanIdAsync("identity=testcookie");

            // Assert
            Assert.Null(fanId);
        }

        [Fact]
        public async Task ResolveFanIdAsync_EmptyPage_ReturnsNull()
        {
            // Arrange — empty response
            SetupGetAsync(CreateStringResponse(""));
            var client = CreateClient();

            // Act
            var fanId = await client.ResolveFanIdAsync("identity=testcookie");

            // Assert
            Assert.Null(fanId);
        }

        [Fact]
        public async Task ResolveFanIdAsync_LargeFanId_ParsesCorrectly()
        {
            // Arrange — very large fan_id (64-bit) in data-blob format
            var html = @"<html><head></head><body>
                <div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:9999999999999,&quot;isLoggedIn&quot;:true}}}""></div>
                </body></html>";

            SetupGetAsync(CreateStringResponse(html));
            var client = CreateClient();

            // Act
            var fanId = await client.ResolveFanIdAsync("identity=testcookie");

            // Assert
            Assert.NotNull(fanId);
            Assert.Equal(9999999999999, fanId.Value);
        }

        #endregion

        #region GetDownloadPageDataAsync Tests (Pagedata Extraction)

        [Fact]
        public async Task GetDownloadPageDataAsync_ValidPagedata_ReturnsDownloadItems()
        {
            // Arrange — simulate a download page with pagedata containing download URLs
            var downloadUrl = "https://bandcamp.com/statdownload/album/12345?sig=abc123&token=xyz";
            var html = $@"
                <html><head>
                <script type=""text/javascript"">
                var pagedata = {{
                    ""download_items"": [{{
                        ""item_id"": 99999,
                        ""item_type"": ""album"",
                        ""title"": ""Test Album"",
                        ""downloads"": {{
                            ""flac"": {{
                                ""url"": ""{downloadUrl}"",
                                ""size_mb"": 150
                            }},
                            ""mp3-v0"": {{
                                ""url"": ""https://bandcamp.com/statdownload/album/12345?sig=mp3sig"",
                                ""size_mb"": 50
                            }}
                        }}
                    }}]
                }};
                </script>
                </head><body></body></html>";

            SetupGetAsync(CreateStringResponse(html));
            var client = CreateClient();

            // Act
            var result = await client.GetDownloadPageDataAsync("identity=testcookie", "https://bandcamp.com/download?type=album&id=12345");

            // Assert
            Assert.NotNull(result);
            Assert.Single(result.DownloadItems);

            var item = result.DownloadItems[0];
            Assert.Equal(99999, item.ItemId);
            Assert.Equal("album", item.ItemType);
            Assert.Equal("Test Album", item.Title);
            Assert.True(item.DownloadUrls.ContainsKey("flac"));
            Assert.Equal(downloadUrl, item.DownloadUrls["flac"]);
            Assert.True(item.DownloadSizes.ContainsKey("flac"));
            Assert.Equal(150L * 1024 * 1024, item.DownloadSizes["flac"]);
            Assert.True(item.DownloadUrls.ContainsKey("mp3-v0"));
            Assert.True(item.DownloadSizes.ContainsKey("mp3-v0"));
            Assert.Equal(50L * 1024 * 1024, item.DownloadSizes["mp3-v0"]);
        }

        [Fact]
        public async Task GetDownloadPageDataAsync_NoPagedata_ReturnsNull()
        {
            // Arrange — page without pagedata script block
            var html = @"<html><body><p>Download page</p></body></html>";

            SetupGetAsync(CreateStringResponse(html));
            var client = CreateClient();

            // Act
            var result = await client.GetDownloadPageDataAsync("identity=testcookie", "https://bandcamp.com/download?type=album&id=12345");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetDownloadPageDataAsync_MultipleDownloadItems_ParsesAll()
        {
            // Arrange — page with multiple download items (e.g., album + bonus track)
            var html = @"
                <html><head>
                <script type=""text/javascript"">
                var pagedata = {
                    ""download_items"": [
                        {
                            ""item_id"": 111,
                            ""item_type"": ""album"",
                            ""title"": ""Main Album"",
                            ""downloads"": {
                                ""flac"": { ""url"": ""https://example.com/album.flac.zip"" }
                            }
                        },
                        {
                            ""item_id"": 222,
                            ""item_type"": ""track"",
                            ""title"": ""Bonus Track"",
                            ""downloads"": {
                                ""flac"": { ""url"": ""https://example.com/bonus.flac"" }
                            }
                        }
                    ]
                };
                </script>
                </head><body></body></html>";

            SetupGetAsync(CreateStringResponse(html));
            var client = CreateClient();

            // Act
            var result = await client.GetDownloadPageDataAsync("identity=testcookie", "https://bandcamp.com/download?type=album&id=111");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.DownloadItems.Count);
            Assert.Equal("Main Album", result.DownloadItems[0].Title);
            Assert.Equal("Bonus Track", result.DownloadItems[1].Title);
            Assert.Equal("album", result.DownloadItems[0].ItemType);
            Assert.Equal("track", result.DownloadItems[1].ItemType);
        }

        [Fact]
        public async Task GetDownloadPageDataAsync_RootDownloadUrl_Parsed()
        {
            // Arrange — page with a root-level download_url
            var html = @"
                <html><head>
                <script type=""text/javascript"">
                var pagedata = {
                    ""download_url"": ""https://bandcamp.com/download direct"",
                    ""download_items"": []
                };
                </script>
                </head><body></body></html>";

            SetupGetAsync(CreateStringResponse(html));
            var client = CreateClient();

            // Act
            var result = await client.GetDownloadPageDataAsync("identity=testcookie", "https://bandcamp.com/download?type=album&id=1");

            // Assert
            Assert.NotNull(result);
            Assert.Equal("https://bandcamp.com/download direct", result.DownloadUrl);
            Assert.Empty(result.DownloadItems);
        }

        [Fact]
        public async Task GetDownloadPageDataAsync_MalformedPagedata_ReturnsNull()
        {
            // Arrange — pagedata block with invalid JSON
            var html = @"
                <html><head>
                <script type=""text/javascript"">
                var pagedata = {this is not valid json!!!;
                </script>
                </head><body></body></html>";

            SetupGetAsync(CreateStringResponse(html));
            var client = CreateClient();

            // Act
            var result = await client.GetDownloadPageDataAsync("identity=testcookie", "https://bandcamp.com/download?type=album&id=1");

            // Assert — gracefully returns null on parse failure
            Assert.Null(result);
        }

        #endregion

        #region ResolveStatdownloadUrlAsync Tests

        [Fact]
        public async Task ResolveStatdownloadUrlAsync_JsonWithUrl_ReturnsResolvedUrl()
        {
            // Arrange — statdownload response with JSON containing URL
            var statResponse = @"{""url"":""https://pop.s3.amazonaws.com/bandcamp/download/12345/album.zip?AWSAccessKeyId=AKIA&Signature=abc""}";

            SetupGetAsync(CreateStringResponse(statResponse));
            var client = CreateClient();

            // Act
            var result = await client.ResolveStatdownloadUrlAsync(
                "identity=testcookie",
                "https://bandcamp.com/statdownload/album/12345?sig=abc",
                "FLAC");

            // Assert
            Assert.NotNull(result);
            Assert.Contains("s3.amazonaws.com", result);
            Assert.Contains("album.zip", result);
        }

        [Fact]
        public async Task ResolveStatdownloadUrlAsync_JsonWithEscapedSlashes_UnescapesCorrectly()
        {
            // Arrange — statdownload response with escaped forward slashes
            var statResponse = @"{""url"":""https:\/\/example.com\/path\/to\/file.zip""}";

            SetupGetAsync(CreateStringResponse(statResponse));
            var client = CreateClient();

            // Act
            var result = await client.ResolveStatdownloadUrlAsync(
                "identity=testcookie",
                "https://bandcamp.com/statdownload/album/12345",
                "FLAC");

            // Assert
            Assert.NotNull(result);
            Assert.Equal("https://example.com/path/to/file.zip", result);
        }

        [Fact]
        public async Task ResolveStatdownloadUrlAsync_JsonWithEscapedAmpersands_UnescapesCorrectly()
        {
            // Arrange — statdownload response with URL-encoded ampersands
            var statResponse = @"{""url"":""https://example.com/path\u0026param=value""}";

            SetupGetAsync(CreateStringResponse(statResponse));
            var client = CreateClient();

            // Act
            var result = await client.ResolveStatdownloadUrlAsync(
                "identity=testcookie",
                "https://bandcamp.com/statdownload/album/12345",
                "FLAC");

            // Assert
            Assert.NotNull(result);
            Assert.Contains("&param=value", result);
        }

        [Fact]
        public async Task ResolveStatdownloadUrlAsync_DirectUrl_ReturnsUrl()
        {
            // Arrange — response is just a plain URL (some Bandcamp responses)
            var directUrl = "https://pop.s3.amazonaws.com/bandcamp/download/12345/album.zip?sig=xyz";

            SetupGetAsync(CreateStringResponse(directUrl));
            var client = CreateClient();

            // Act
            var result = await client.ResolveStatdownloadUrlAsync(
                "identity=testcookie",
                "https://bandcamp.com/statdownload/album/12345",
                "FLAC");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(directUrl.Trim(), result);
        }

        [Fact]
        public async Task ResolveStatdownloadUrlAsync_InvalidResponse_ReturnsNull()
        {
            // Arrange — garbled response with no URL
            SetupGetAsync(CreateStringResponse("error: not found"));
            var client = CreateClient();

            // Act
            var result = await client.ResolveStatdownloadUrlAsync(
                "identity=testcookie",
                "https://bandcamp.com/statdownload/album/12345",
                "FLAC");

            // Assert
            Assert.Null(result);
        }

        #endregion

        #region DownloadFileAsync Tests

        [Fact]
        public async Task DownloadFileAsync_HtmlResponse_ThrowsDownloadException()
        {
            // Arrange — Bandcamp returns HTML instead of ZIP (expired URL)
            var htmlResponse = "<html><body>Error: download expired</body></html>";
            var request = new HttpRequest("https://example.com/file.zip");

            // Create response with text/html content type
            var headers = new HttpHeader { { "Content-Type", "text/html; charset=utf-8" } };
            var response = new HttpResponse(request, headers, htmlResponse, HttpStatusCode.OK);

            _innerHttpClientMock
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync(response);

            var client = CreateClient();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<DownloadException>(
                () => client.DownloadFileAsync("identity=testcookie", "https://example.com/file.zip"));

            Assert.Contains("HTML response", ex.Message);
        }

        #endregion

        #region GetCollectionAsync Tests

        [Fact]
        public async Task GetCollectionAsync_ValidResponse_ReturnsItems()
        {
            // Arrange
            var collectionJson = @"{
                ""items"": [
                    {
                        ""item_id"": 100,
                        ""item_type"": ""album"",
                        ""band_id"": 200,
                        ""token"": ""abc123"",
                        ""item_url"": ""https://test.bandcamp.com/album/test-album"",
                        ""item_title"": ""Test Album"",
                        ""band_name"": ""Test Artist""
                    },
                    {
                        ""item_id"": 101,
                        ""item_type"": ""track"",
                        ""band_id"": 201,
                        ""token"": ""def456"",
                        ""url"": ""https://other.bandcamp.com/track/test-track"",
                        ""title"": ""Test Track"",
                        ""artist"": ""Other Artist""
                    }
                ]
            }";

            SetupGetAsync(CreateStringResponse(collectionJson));
            var client = CreateClient();

            // Act
            var items = await client.GetCollectionAsync("identity=testcookie", 12345678);

            // Assert
            Assert.Equal(2, items.Count);

            Assert.Equal(100, items[0].ItemId);
            Assert.Equal("album", items[0].ItemType);
            Assert.Equal(200, items[0].BandId);
            Assert.Equal("abc123", items[0].Token);
            Assert.Equal("https://test.bandcamp.com/album/test-album", items[0].ItemUrl);
            Assert.Equal("Test Album", items[0].Title);
            Assert.Equal("Test Artist", items[0].BandName);

            Assert.Equal(101, items[1].ItemId);
            Assert.Equal("track", items[1].ItemType);
            Assert.Equal("https://other.bandcamp.com/track/test-track", items[1].ItemUrl);
            Assert.Equal("Test Track", items[1].Title);
            Assert.Equal("Other Artist", items[1].BandName);
        }

        [Fact]
        public async Task GetCollectionAsync_RedownloadUrls_Metadata_AndDownloadableState_AreParsed()
        {
            // Arrange
            var collectionJson = @"{
                ""redownload_urls"": {
                    ""a555"": ""https://bandcamp.com/download?type=album&id=555""
                },
                ""items"": [
                    {
                        ""item_id"": 555,
                        ""item_type"": ""album"",
                        ""band_id"": 200,
                        ""sale_item_type"": ""a"",
                        ""sale_item_id"": 555,
                        ""token"": ""abc123"",
                        ""item_url"": ""https://fresh.bandcamp.com/album/fresh"",
                        ""item_title"": ""Fresh"",
                        ""band_name"": ""Fresh"",
                        ""num_tracks"": 8,
                        ""release_date"": ""2024-01-02T00:00:00Z""
                    },
                    {
                        ""item_id"": 777,
                        ""item_type"": ""album"",
                        ""band_id"": 201,
                        ""sale_item_type"": ""a"",
                        ""sale_item_id"": 777,
                        ""token"": ""def456"",
                        ""item_url"": ""https://fresh.bandcamp.com/album/no-download"",
                        ""item_title"": ""No Download"",
                        ""band_name"": ""Fresh""
                    }
                ]
            }";

            SetupGetAsync(CreateStringResponse(collectionJson));
            var client = CreateClient();

            // Act
            var items = await client.GetCollectionAsync("identity=testcookie", 12345678);

            // Assert
            Assert.Equal(2, items.Count);

            var downloadable = items[0];
            Assert.Equal("a", downloadable.SaleItemType);
            Assert.Equal(555, downloadable.SaleItemId);
            Assert.Equal("https://bandcamp.com/download?type=album&id=555", downloadable.DownloadPageUrl);
            Assert.Equal(8, downloadable.TrackCount);
            Assert.Equal("2024-01-02T00:00:00Z", downloadable.ReleaseDate);
            Assert.True(downloadable.IsDownloadable);

            var nonDownloadable = items[1];
            Assert.False(nonDownloadable.IsDownloadable);
            Assert.Null(nonDownloadable.DownloadPageUrl);
        }

        [Fact]
        public async Task GetCollectionAsync_EmptyItems_ReturnsEmptyList()
        {
            // Arrange
            var collectionJson = @"{""items"": []}";

            SetupGetAsync(CreateStringResponse(collectionJson));
            var client = CreateClient();

            // Act
            var items = await client.GetCollectionAsync("identity=testcookie", 12345678);

            // Assert
            Assert.Empty(items);
        }

        [Fact]
        public async Task GetDownloadableCollectionAsync_SkipsNonDownloadable_AndDeduplicatesByDownloadPage()
        {
            // Arrange
            var firstPage = @"{
                ""redownload_urls"": {
                    ""a100"": ""https://bandcamp.com/download?type=album&id=100"",
                    ""a101"": ""https://bandcamp.com/download?type=album&id=100""
                },
                ""items"": [
                    {
                        ""item_id"": 100,
                        ""item_type"": ""album"",
                        ""sale_item_type"": ""a"",
                        ""sale_item_id"": 100,
                        ""token"": ""tok-100"",
                        ""item_title"": ""Fresh"",
                        ""band_name"": ""Fresh""
                    },
                    {
                        ""item_id"": 101,
                        ""item_type"": ""album"",
                        ""sale_item_type"": ""a"",
                        ""sale_item_id"": 101,
                        ""token"": ""tok-101"",
                        ""item_title"": ""Fresh Duplicate"",
                        ""band_name"": ""Fresh""
                    },
                    {
                        ""item_id"": 102,
                        ""item_type"": ""album"",
                        ""sale_item_type"": ""a"",
                        ""sale_item_id"": 102,
                        ""token"": ""tok-102"",
                        ""item_title"": ""No Download"",
                        ""band_name"": ""Fresh""
                    }
                ]
            }";

            var secondPage = @"{""items"": []}";

            _innerHttpClientMock
                .SetupSequence(c => c.GetAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync(CreateStringResponse(firstPage))
                .ReturnsAsync(CreateStringResponse(secondPage));

            var client = CreateClient();

            // Act
            var items = await client.GetDownloadableCollectionAsync("identity=testcookie", 12345678, maxPages: 5);

            // Assert
            Assert.Single(items);
            Assert.Equal(100, items[0].ItemId);
            Assert.Equal("https://bandcamp.com/download?type=album&id=100", items[0].DownloadPageUrl);
        }

        [Fact]
        public async Task GetCollectionAsync_InvalidJson_ReturnsEmptyList()
        {
            // Arrange — garbled response should not throw
            SetupGetAsync(CreateStringResponse("not valid json"));
            var client = CreateClient();

            // Act
            var items = await client.GetCollectionAsync("identity=testcookie", 12345678);

            // Assert — gracefully returns empty list
            Assert.Empty(items);
        }

        [Fact]
        public async Task GetCollectionAsync_TralbumsKey_ReturnsItems()
        {
            // Arrange — response uses "tralbums" instead of "items"
            var collectionJson = @"{
                ""tralbums"": [
                    {
                        ""item_id"": 300,
                        ""item_type"": ""album"",
                        ""band_id"": 400,
                        ""token"": ""xyz789"",
                        ""item_url"": ""https://tralbum.bandcamp.com/album/test"",
                        ""item_title"": ""Tralbum Test""
                    }
                ]
            }";

            SetupGetAsync(CreateStringResponse(collectionJson));
            var client = CreateClient();

            // Act
            var items = await client.GetCollectionAsync("identity=testcookie", 12345678);

            // Assert
            Assert.Single(items);
            Assert.Equal(300, items[0].ItemId);
            Assert.Equal("Tralbum Test", items[0].Title);
        }

        #endregion

        #region Credential Leakage Tests

        [Fact]
        public void NoCookieValuesInSourceFiles()
        {
            // Grep all download client source files for patterns that could log cookie values.
            // We look for logger calls that pass variables containing cookie data directly.
            // Safe patterns (allowed):
            //   - "cookies are required", "cookies may be", "cookies from browser"
            //   - "cookies not configured", "session cookies"
            //   - "Cookie" header injection in BandcampHttpClient (Set("Cookie", cookies))
            //   - PrivacyLevel.Password on field definition
            //   - IsNullOrWhiteSpace checks
            // Unsafe patterns (flagged):
            //   - _logger.Xxx("{0}", cookies) where cookies holds the actual value
            //   - Logging Settings.Cookies as an argument

            var srcDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(BandcampApiClientFixture).Assembly.Location)!,
                    "..", "..", "..", "..", "..",
                    "src", "Lidarr.Plugin.Bandcamp"));

            if (!System.IO.Directory.Exists(srcDir))
            {
                // Skip if not running from standard project layout
                return;
            }

            var sourceFiles = System.IO.Directory.GetFiles(srcDir, "*.cs", System.IO.SearchOption.AllDirectories);
            var violations = new System.Collections.Generic.List<string>();

            foreach (var file in sourceFiles)
            {
                var lines = System.IO.File.ReadAllLines(file);
                var fileName = System.IO.Path.GetFileName(file);

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();

                    // Skip comments
                    if (line.StartsWith("//") || line.StartsWith("///") || line.StartsWith("*"))
                    {
                        continue;
                    }

                    // Skip non-logging lines
                    if (!line.Contains("_logger.") && !line.Contains("Logger."))
                    {
                        continue;
                    }

                    // Check for logger calls that pass a variable named 'cookies' as a format argument
                    // This pattern catches: _logger.Xxx("...", cookies) or _logger.Xxx("...", item.Cookies)
                    // but not: _logger.Xxx("cookies are required") (string literal, not variable)
                    var lowerLine = line.ToLowerInvariant();

                    // Flag if a logger call uses a cookies variable as an argument (not in a string literal)
                    // We check for the pattern: logger method call where 'cookies' or 'Cookies' appears
                    // outside of a string literal
                    if ((lowerLine.Contains("cookies") || lowerLine.Contains("cookie")) &&
                        !lowerLine.Contains("cookies are") &&
                        !lowerLine.Contains("cookies may") &&
                        !lowerLine.Contains("cookies from") &&
                        !lowerLine.Contains("cookies not") &&
                        !lowerLine.Contains("session cookies") &&
                        !lowerLine.Contains("cookie auth") &&
                        !lowerLine.Contains("cookie test") &&
                        !lowerLine.Contains("test cookie") &&
                        !lowerLine.Contains("validat") &&
                        !lowerLine.Contains("fielddefinition") &&
                        !lowerLine.Contains("privacy") &&
                        !lowerLine.Contains("fieldtype") &&
                        !lowerLine.Contains("helptext") &&
                        !line.Contains("Set(\"Cookie\"", StringComparison.OrdinalIgnoreCase) &&
                        !lowerLine.Contains("are not set") &&
                        !lowerLine.Contains("is not set"))
                    {
                        // Additional check: is 'cookies' used as a method argument vs in a string?
                        // If the line has _logger.Xxx(... cookies ...) where cookies is a variable reference
                        // (not inside quotes), that's a violation.
                        // Simpler: if we see "cookies)" or "Cookies)" or "cookies," it's being passed as an arg
                        if (line.Contains("cookies)", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("Cookies)", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("cookies,", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("Cookies,", StringComparison.OrdinalIgnoreCase))
                        {
                            violations.Add($"{fileName}:{i + 1}: {line}");
                        }
                    }
                }
            }

            Assert.Empty(violations);
        }

        [Fact]
        public void CookieFieldUsesPrivacyLevelPassword()
        {
            // Verify that the Cookies field in BandcampDownloadSettings is marked with PrivacyLevel.Password
            var settingsType = typeof(BandcampDownloadSettings);
            var cookiesProp = settingsType.GetProperty("Cookies");
            Assert.NotNull(cookiesProp);

            var attr = cookiesProp!.GetCustomAttributes(false)
                .OfType<NzbDrone.Core.Annotations.FieldDefinitionAttribute>()
                .FirstOrDefault();

            Assert.NotNull(attr);
            Assert.Equal(PrivacyLevel.Password, attr!.Privacy);
        }

        #endregion
    }
}
