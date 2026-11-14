using System;
using System.IO;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Download.Clients.Bandcamp.Session;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    /// <summary>
    /// A session that dies after Phase 1 is renewed once and the item retried from Phase 1.
    /// </summary>
    public class BandcampDownloadClientSessionRenewalFixture : IDisposable
    {
        private const string OldCookie = "identity=old-cookie";
        private const string NewCookie = "identity=new-cookie";
        private const string DownloadPageUrl = "https://bandcamp.com/download?type=album&id=111";

        private const string ValidHomepage =
            @"<html><body><div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:12345678,&quot;isLoggedIn&quot;:true}}}""></div></body></html>";

        private const string DeadHomepage =
            @"<html><body><div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:null,&quot;isLoggedIn&quot;:false}}}""></div></body></html>";

        private const string DownloadPage =
            @"<html><head><script type=""text/javascript"">var pagedata = {
                ""digital_items"": [{
                    ""item_id"": 111,
                    ""item_type"": ""album"",
                    ""title"": ""Fresh"",
                    ""downloads"": {
                        ""flac"": { ""url"": ""https://bandcamp.com/statdownload/album/111?sig=flac"", ""size_mb"": 12 }
                    }
                }]
            };</script></head><body></body></html>";

        private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "bandcamp-renewal-tests", Guid.NewGuid().ToString("N"));
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private int _downloadPageRequests;

        [Fact]
        public async Task DistinctNamedOwnersKeepSeparateSessionsEvenWhenBothAreValid()
        {
            var owners = new List<DownloadClientDefinition>
            {
                new() { Id = 70, Name = "Personal", Implementation = nameof(BandcampDownloadClient), Settings = new BandcampDownloadSettings { Cookies = OldCookie } },
                new() { Id = 71, Name = "Other", Implementation = nameof(BandcampDownloadClient), Settings = new BandcampDownloadSettings { Cookies = NewCookie } }
            };
            var repository = new Mock<IDownloadClientRepository>();
            repository.Setup(r => r.All()).Returns(owners);
            repository.Setup(r => r.Find(It.IsAny<int>())).Returns((int id) => owners.Single(o => o.Id == id));
            var http = Http((request, cookie) => Text(ValidHomepage, request));
            var api = new BandcampApiClient(new BandcampHttpClient(http.Object, _logger), _logger);
            var registry = new BandcampSessionProviderRegistry(api, null, new BandcampSessionStore(repository.Object), _logger, repository.Object);
            var personal = registry.GetNamed("Personal");
            var other = registry.GetNamed("Other");
            Assert.Equal(OldCookie, (await personal.GetSessionAsync())!.Cookies);
            Assert.Equal(NewCookie, (await other.GetSessionAsync())!.Cookies);
            Assert.Equal(OldCookie, (await personal.GetSessionAsync())!.Cookies);
            Assert.NotEqual(personal.DefinitionId, other.DefinitionId);
            repository.Verify(r => r.SetFields(It.IsAny<DownloadClientDefinition>(), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()), Times.Never);
        }

        [Fact]
        public async Task ChangingOwnerToReferenceDuringRecoveryRetainsItsLegacySecrets()
        {
            var settings = new BandcampDownloadSettings { Cookies = OldCookie, Email = "owner@example.test", Password = "secret" };
            var owner = new DownloadClientDefinition { Id = 70, Name = "Owner", Implementation = nameof(BandcampDownloadClient), Settings = settings };
            var repository = new Mock<IDownloadClientRepository>();
            repository.Setup(r => r.All()).Returns(new List<DownloadClientDefinition> { owner });
            repository.Setup(r => r.Find(70)).Returns(owner);
            var writes = 0;
            repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Callback(() => writes++);
            var http = Http((request, cookie) =>
            {
                owner.Settings = new BandcampDownloadSettings
                {
                    Cookies = OldCookie, Email = "owner@example.test", Password = "secret", AuthConfiguration = "Different account"
                };
                return Text(ValidHomepage, request);
            });
            var api = new BandcampApiClient(new BandcampHttpClient(http.Object, _logger), _logger);
            var registry = new BandcampSessionProviderRegistry(api, null, new BandcampSessionStore(repository.Object), _logger, repository.Object);
            Assert.Null(await registry.GetNamed("Owner").ReplaceIdentityAsync(NewCookie));
            Assert.Equal(0, writes);
            Assert.Equal(OldCookie, ((BandcampDownloadSettings)owner.Settings).Cookies);
            Assert.Equal("Different account", ((BandcampDownloadSettings)owner.Settings).AuthConfiguration);
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("duplicate")]
        [InlineData("chain")]
        public async Task InvalidNamedSelectionRefusesRatherThanUsingAnotherAccountsLegacyCookie(string scenario)
        {
            var owner = new DownloadClientDefinition
            {
                Id = 70, Name = "Owner", Implementation = nameof(BandcampDownloadClient),
                Settings = new BandcampDownloadSettings { Cookies = NewCookie, AuthConfiguration = scenario == "chain" ? "Another" : "" }
            };
            var definitions = scenario == "missing" ? new List<DownloadClientDefinition>() : new List<DownloadClientDefinition> { owner };
            if (scenario == "duplicate")
            {
                definitions.Add(new DownloadClientDefinition { Id = 71, Name = "Owner", Implementation = nameof(BandcampDownloadClient), Settings = new BandcampDownloadSettings { Cookies = "identity=different-account" } });
            }

            var repository = new Mock<IDownloadClientRepository>();
            repository.Setup(r => r.All()).Returns(definitions);
            repository.Setup(r => r.Find(70)).Returns(owner);
            var http = Http((request, cookie) => throw new InvalidOperationException("Must refuse before HTTP"));
            var api = new BandcampApiClient(new BandcampHttpClient(http.Object, _logger), _logger);
            var registry = new BandcampSessionProviderRegistry(api, null, new BandcampSessionStore(repository.Object), _logger, repository.Object);
            var client = CreateClient(http, registry);
            Settings(client).AuthConfiguration = "Owner";
            Assert.Contains(client.Test().Errors, failure => failure.PropertyName == "AuthConfiguration");
            var error = await Assert.ThrowsAsync<DownloadClientAuthenticationException>(() => client.Download(CreateRemoteAlbum(), Mock.Of<IIndexer>()));
            Assert.Contains("exact unique name", error.Message);
            Assert.Equal(OldCookie, Settings(client).Cookies);
            Assert.Equal(BandcampSessionProblem.ConfigurationMissing, registry.Find(client.Definition.Id)!.State.Problem);
        }

        [Fact]
        public async Task NamedOwnerRenewalRepairsSearchAndRealDownloadWithoutConsumerCookieWrites()
        {
            var owner = new DownloadClientDefinition
            {
                Id = 70, Name = "Fan account", Enable = false, Implementation = nameof(BandcampDownloadClient),
                Settings = new BandcampDownloadSettings { Cookies = OldCookie, Email = "fan@example.test", Password = "secret", DownloadPath = "/owner" }
            };
            var saved = System.Text.Json.JsonSerializer.Serialize((BandcampDownloadSettings)owner.Settings);
            var repository = new Mock<IDownloadClientRepository>();
            repository.Setup(r => r.All()).Returns(() => new List<DownloadClientDefinition> { owner });
            repository.Setup(r => r.Find(70)).Returns(() =>
            {
                owner.Settings = System.Text.Json.JsonSerializer.Deserialize<BandcampDownloadSettings>(saved)!;
                return owner;
            });
            var writes = 0;
            repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
                .Callback<DownloadClientDefinition, Expression<Func<DownloadClientDefinition, object>>[]>((d, fields) =>
                {
                    Assert.Equal(70, d.Id);
                    saved = System.Text.Json.JsonSerializer.Serialize((BandcampDownloadSettings)d.Settings);
                    writes++;
                });
            var http = Http((request, cookie) =>
            {
                if (request.Url.FullUri == "https://bandcamp.com")
                {
                    return Text(cookie == NewCookie ? ValidHomepage : DeadHomepage, request);
                }

                if (request.Url.FullUri == DownloadPageUrl)
                {
                    Assert.Equal(NewCookie, cookie);
                    return Text(DownloadPage, request);
                }

                if (request.Url.FullUri == "https://bandcamp.com/api/fancollection/1/collection_items")
                {
                    Assert.Equal(NewCookie, cookie);
                    return Text(@"{""redownload_urls"":{""a111"":""https://bandcamp.com/download?type=album&id=111""},""items"":[{""item_id"":111,""item_type"":""album"",""sale_item_type"":""a"",""sale_item_id"":111,""token"":""tok"",""item_url"":""https://fresh.bandcamp.com/album/fresh"",""item_title"":""Fresh"",""band_name"":""Fresh""}]}", request);
                }

                return Files(request);
            });
            var api = new BandcampApiClient(new BandcampHttpClient(http.Object, _logger), _logger);
            var registry = new BandcampSessionProviderRegistry(api, null, new BandcampSessionStore(repository.Object), _logger, repository.Object);
            var selectedBeforeRenewal = registry.GetNamed("Fan account");
            Assert.Null(await selectedBeforeRenewal.GetSessionAsync());
            Assert.NotNull(await selectedBeforeRenewal.ReplaceIdentityAsync(NewCookie));
            Assert.Equal(NewCookie, (await registry.GetNamed("Fan account").GetSessionAsync())!.Cookies);

            var client = CreateClient(http, registry);
            Settings(client).AuthConfiguration = "Fan account";
            var indexerSettings = new NzbDrone.Core.Indexers.Bandcamp.BandcampIndexerSettings
            {
                AuthConfiguration = "Fan account", Cookies = "identity=other-legacy-account", EarlyReleaseLimit = 8
            };
            var indexer = new NzbDrone.Core.Indexers.Bandcamp.BandcampIndexer(http.Object, api,
                Mock.Of<IIndexerStatusService>(), Mock.Of<IConfigService>(), Mock.Of<NzbDrone.Core.Parser.IParsingService>(), _logger, registry)
            {
                Definition = new IndexerDefinition { Id = 72, Name = "Search", Enable = true, Settings = indexerSettings }
            };
            var releases = await indexer.Fetch(new NzbDrone.Core.IndexerSearch.Definitions.ArtistSearchCriteria { Artist = new Artist { Name = "Fresh" } });
            Assert.NotEmpty(releases);
            var id = await client.Download(CreateRemoteAlbum(), Mock.Of<IIndexer>());
            var item = await WaitForFinished(client, id);
            Assert.Equal(DownloadItemStatus.Completed, item.Status);
            var output = Directory.GetFiles(_tempRoot, "*.flac", SearchOption.AllDirectories).Single();
            Assert.Equal(new byte[] { 0x66, 0x4C, 0x61, 0x43, 0, 0, 0, 0x22 }, File.ReadAllBytes(output));
            Assert.Equal(1, writes);
            Assert.Equal(OldCookie, Settings(client).Cookies);
            Assert.Equal("identity=other-legacy-account", indexerSettings.Cookies);
            Assert.Equal(8, indexerSettings.EarlyReleaseLimit);
            Assert.False(owner.Enable);
            var persisted = System.Text.Json.JsonSerializer.Deserialize<BandcampDownloadSettings>(saved)!;
            Assert.Equal("fan@example.test", persisted.Email);
            Assert.Equal("secret", persisted.Password);
            Assert.Equal("/owner", persisted.DownloadPath);

            // An owner client/factory may still hold its pre-renewal settings snapshot.
            var staleOwner = new BandcampDownloadSettings { Cookies = OldCookie };
            Assert.Equal(NewCookie, (await registry.Get(70, staleOwner).GetSessionAsync())?.Cookies);

            // Recreate the registry from persisted settings, as after a process restart.
            var restarted = new BandcampSessionProviderRegistry(api, null, new BandcampSessionStore(repository.Object), _logger, repository.Object);
            Assert.Equal(NewCookie, (await restarted.GetNamed("Fan account").GetSessionAsync())!.Cookies);
        }

        [Fact]
        public async Task GetItems_SessionDiesAfterPhase1AndRenews_ItemRetriesAndCompletes()
        {
            BandcampDownloadClient client = null!;
            var homepageCallsWithOldCookie = 0;

            var http = Http((request, cookie) =>
            {
                switch (request.Url.FullUri)
                {
                    case "https://bandcamp.com":
                        if (cookie == OldCookie)
                        {
                            return Text(Interlocked.Increment(ref homepageCallsWithOldCookie) == 1 ? ValidHomepage : DeadHomepage, request);
                        }

                        return Text(ValidHomepage, request);
                    case DownloadPageUrl:
                        Interlocked.Increment(ref _downloadPageRequests);
                        if (cookie == OldCookie)
                        {
                            // The session dies mid-download; a fresh session becomes available.
                            Settings(client).Cookies = NewCookie;
                            return Text("<html></html>", request);
                        }

                        return Text(DownloadPage, request);
                    default:
                        return Files(request);
                }
            });

            client = CreateClient(http);

            var downloadId = await client.Download(CreateRemoteAlbum(), Mock.Of<IIndexer>());
            var item = await WaitForFinished(client, downloadId);

            Assert.Equal(DownloadItemStatus.Completed, item.Status);
            Assert.Equal(2, _downloadPageRequests);
        }

        [Fact]
        public async Task GetItems_RetryAfterRenewalAlsoFails_ItemFailsWithoutSecondRenewal()
        {
            BandcampDownloadClient client = null!;
            var homepageCallsWithOldCookie = 0;

            var http = Http((request, cookie) =>
            {
                switch (request.Url.FullUri)
                {
                    case "https://bandcamp.com":
                        if (cookie == OldCookie)
                        {
                            return Text(Interlocked.Increment(ref homepageCallsWithOldCookie) == 1 ? ValidHomepage : DeadHomepage, request);
                        }

                        return Text(ValidHomepage, request);
                    case DownloadPageUrl:
                        Interlocked.Increment(ref _downloadPageRequests);
                        if (cookie == OldCookie)
                        {
                            Settings(client).Cookies = NewCookie;
                        }

                        return Text("<html></html>", request);
                    default:
                        return Files(request);
                }
            });

            client = CreateClient(http);

            var downloadId = await client.Download(CreateRemoteAlbum(), Mock.Of<IIndexer>());
            var item = await WaitForFinished(client, downloadId);

            Assert.Equal(DownloadItemStatus.Failed, item.Status);
            Assert.Equal(2, _downloadPageRequests);
        }

        [Fact]
        public async Task GetItems_SessionDiesAfterPhase1AndCannotBeRenewed_ItemFails()
        {
            var homepageCalls = 0;

            var http = Http((request, cookie) =>
            {
                switch (request.Url.FullUri)
                {
                    case "https://bandcamp.com":
                        return Text(Interlocked.Increment(ref homepageCalls) == 1 ? ValidHomepage : DeadHomepage, request);
                    case DownloadPageUrl:
                        Interlocked.Increment(ref _downloadPageRequests);
                        return Text("<html></html>", request);
                    default:
                        return Files(request);
                }
            });

            var client = CreateClient(http);

            var downloadId = await client.Download(CreateRemoteAlbum(), Mock.Of<IIndexer>());
            var item = await WaitForFinished(client, downloadId);

            Assert.Equal(DownloadItemStatus.Failed, item.Status);
            Assert.Equal(1, _downloadPageRequests);
        }

        [Fact]
        public async Task GetItems_NonAuthFailureWithLiveSession_ItemFailsWithoutRetry()
        {
            var http = Http((request, cookie) =>
            {
                switch (request.Url.FullUri)
                {
                    case "https://bandcamp.com":
                        return Text(ValidHomepage, request);
                    case DownloadPageUrl:
                        Interlocked.Increment(ref _downloadPageRequests);
                        return Text("<html></html>", request);
                    default:
                        return Files(request);
                }
            });

            var client = CreateClient(http);

            var downloadId = await client.Download(CreateRemoteAlbum(), Mock.Of<IIndexer>());
            var item = await WaitForFinished(client, downloadId);

            Assert.Equal(DownloadItemStatus.Failed, item.Status);
            Assert.Equal(1, _downloadPageRequests);
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

        private static BandcampDownloadSettings Settings(BandcampDownloadClient client)
        {
            return (BandcampDownloadSettings)client.Definition.Settings;
        }

        private static Mock<IHttpClient> Http(Func<HttpRequest, string, HttpResponse> respond)
        {
            var http = new Mock<IHttpClient>();
            http
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync((HttpRequest request) => respond(request, request.Headers.GetSingleValue("Cookie")));
            return http;
        }

        private static HttpResponse Files(HttpRequest request)
        {
            return request.Url.FullUri switch
            {
                "https://bandcamp.com/statdownload/album/111?sig=flac&.format=flac&.json=true" =>
                    Text(@"{""url"":""https://files.example.com/fresh.flac""}", request),
                "https://files.example.com/fresh.flac" => new HttpResponse(
                    request,
                    new HttpHeader { { "Content-Type", "audio/flac" } },
                    new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22 },
                    HttpStatusCode.OK),
                _ => Text("{}", request)
            };
        }

        private static HttpResponse Text(string content, HttpRequest request)
        {
            return new HttpResponse(request, new HttpHeader(), content, HttpStatusCode.OK);
        }

        private static RemoteAlbum CreateRemoteAlbum()
        {
            return new RemoteAlbum
            {
                Release = new ReleaseInfo
                {
                    Title = "Fresh - Fresh [FLAC]",
                    Album = "Fresh",
                    DownloadUrl = DownloadPageUrl + "#format=flac"
                }
            };
        }

        private static async Task<DownloadClientItem> WaitForFinished(BandcampDownloadClient client, string downloadId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var item = client.GetItems().Single(i => i.DownloadId == downloadId);
                if (item.Status == DownloadItemStatus.Completed || item.Status == DownloadItemStatus.Failed)
                {
                    return item;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Download did not finish in time");
                }

                await Task.Delay(20);
            }
        }

        private BandcampDownloadClient CreateClient(Mock<IHttpClient> http, IBandcampSessionProviderRegistry? registry = null)
        {
            var bandcampHttp = new BandcampHttpClient(http.Object, _logger);
            var apiClient = new BandcampApiClient(bandcampHttp, _logger);
            var proxy = new BandcampDownloadProxy(apiClient, bandcampHttp, _logger);
            var queue = new DownloadTaskQueue(proxy, new BandcampDownloadRegistry(), _logger);

            var client = new BandcampDownloadClient(
                queue,
                registry ?? new BandcampSessionProviderRegistry(apiClient, null, null, _logger),
                Mock.Of<IAlbumService>(),
                Mock.Of<IReleaseService>(),
                Mock.Of<ITrackService>(),
                Mock.Of<IConfigService>(),
                Mock.Of<IDiskProvider>(),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ILocalizationService>(),
                _logger);

            client.Definition = new DownloadClientDefinition
            {
                Id = 1,
                Name = "Bandcamp",
                Settings = new BandcampDownloadSettings
                {
                    Cookies = OldCookie,
                    DownloadPath = _tempRoot
                }
            };

            return client;
        }
    }
}
