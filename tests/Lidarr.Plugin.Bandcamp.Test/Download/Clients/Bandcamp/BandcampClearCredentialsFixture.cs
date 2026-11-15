using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Moq;
using NLog;
using Newtonsoft.Json;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Download.Clients.Bandcamp.Session;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
using NzbDrone.Core.RemotePathMappings;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp;

public class BandcampClearCredentialsFixture
{
    [Fact]
    public async System.Threading.Tasks.Task ClearedOwnerCannotSupplyCachedIdentityOrAcceptStaleRecoveryAfterRestart()
    {
        var saved = new BandcampDownloadSettings { Cookies = "identity=private", DownloadPath = "/music" };
        var repository = new Mock<IDownloadClientRepository>();
        DownloadClientDefinition Read() => new() { Id = 7, Name = "Owner", Implementation = nameof(BandcampDownloadClient), Settings = JsonConvert.DeserializeObject<BandcampDownloadSettings>(JsonConvert.SerializeObject(saved))! };
        repository.Setup(r => r.Find(7)).Returns(Read);
        repository.Setup(r => r.All()).Returns(() => new System.Collections.Generic.List<DownloadClientDefinition> { Read() });
        repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
            .Callback<DownloadClientDefinition, Expression<Func<DownloadClientDefinition, object>>[]>((d, _) => saved = (BandcampDownloadSettings)d.Settings);
        var logger = LogManager.GetCurrentClassLogger();
        var http = new Mock<IHttpClient>();
        http.Setup(h => h.ExecuteAsync(It.IsAny<HttpRequest>())).ReturnsAsync((HttpRequest r) => new HttpResponse(r, new HttpHeader(), LoginFixtures.Read("homepage_logged_in.html"), System.Net.HttpStatusCode.OK));
        var api = new BandcampApiClient(new BandcampHttpClient(http.Object, logger), logger);
        var store = new BandcampSessionStore(repository.Object);
        BandcampSessionProviderRegistry Registry() => new(api, null, store, logger, repository.Object);
        var registry = Registry(); var owner = registry.GetNamed("Owner");
        Assert.NotNull(await owner.GetSessionAsync());
        var old = saved;
        Assert.True(registry.ClearLocalCredentials(7));
        Assert.Null(await owner.GetSessionAsync());
        Assert.Null(await Registry().GetNamed("Owner").GetSessionAsync());
        Assert.False(store.TryReplaceCookies(7, old.Cookies, old.CredentialKey(), "identity=stale-recovery"));
        Assert.Empty(saved.Cookies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexerClearRemovesOnlyItsLegacyCookieAndPreservesSharedSelection(bool storageFails)
    {
        var saved = new NzbDrone.Core.Indexers.Bandcamp.BandcampIndexerSettings { Cookies = "identity=private", AuthConfiguration = "Owner", EarlyReleaseLimit = 5 };
        var repository = new Mock<NzbDrone.Core.Indexers.IIndexerRepository>();
        repository.Setup(r => r.Find(9)).Returns(() => new NzbDrone.Core.Indexers.IndexerDefinition { Id = 9, Implementation = "BandcampIndexer", Settings = saved });
        repository.Setup(r => r.SetFields(It.IsAny<NzbDrone.Core.Indexers.IndexerDefinition>(), It.IsAny<Expression<Func<NzbDrone.Core.Indexers.IndexerDefinition, object>>[]>()))
            .Callback<NzbDrone.Core.Indexers.IndexerDefinition, Expression<Func<NzbDrone.Core.Indexers.IndexerDefinition, object>>[]>((d, _) => { if (storageFails) throw new Exception("identity=private"); saved = (NzbDrone.Core.Indexers.Bandcamp.BandcampIndexerSettings)d.Settings; });
        var logger = LogManager.GetCurrentClassLogger();
        var indexer = new NzbDrone.Core.Indexers.Bandcamp.BandcampIndexer(Mock.Of<IHttpClient>(), new BandcampApiClient(new BandcampHttpClient(Mock.Of<IHttpClient>(), logger), logger), Mock.Of<NzbDrone.Core.Indexers.IIndexerStatusService>(), Mock.Of<IConfigService>(), Mock.Of<NzbDrone.Core.Parser.IParsingService>(), logger, repository: repository.Object);
        indexer.Definition = new NzbDrone.Core.Indexers.IndexerDefinition { Id = 9, Settings = saved };
        var result = JsonConvert.SerializeObject(indexer.RequestAction("clearLocalCredentials", new Dictionary<string, string>()));
        Assert.Contains(storageFails ? "StorageFailure" : "does not revoke", result);
        Assert.DoesNotContain("identity=private", result);
        Assert.Equal(storageFails ? "identity=private" : "", saved.Cookies);
        Assert.Equal("Owner", saved.AuthConfiguration); Assert.Equal(5, saved.EarlyReleaseLimit);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ClearActionErasesOnlySelectedLocalSavedCopiesAndReportsStorageFailure(bool consumer, bool storageFails)
    {
        var saved = new BandcampDownloadSettings { Email = "private@example.test", Password = "password-secret", Cookies = "identity=private", VerificationCode = "code-private", DownloadPath = "/music", AuthConfiguration = consumer ? "Other owner" : "", ChallengeUntilUtc = DateTime.UtcNow.AddHours(6), ChallengeProblem = "CaptchaRequired" };
        var repository = new Mock<IDownloadClientRepository>();
        repository.Setup(r => r.Find(7)).Returns(() => new DownloadClientDefinition { Id = 7, Implementation = nameof(BandcampDownloadClient), Settings = JsonConvert.DeserializeObject<BandcampDownloadSettings>(JsonConvert.SerializeObject(saved))! });
        repository.Setup(r => r.SetFields(It.IsAny<DownloadClientDefinition>(), It.IsAny<Expression<Func<DownloadClientDefinition, object>>[]>()))
            .Callback<DownloadClientDefinition, Expression<Func<DownloadClientDefinition, object>>[]>((d, _) => { if (storageFails) throw new Exception("identity=private"); saved = (BandcampDownloadSettings)d.Settings; });
        var logger = LogManager.GetCurrentClassLogger();
        var registry = new BandcampSessionProviderRegistry(new BandcampApiClient(new BandcampHttpClient(Mock.Of<IHttpClient>(), logger), logger), null, new BandcampSessionStore(repository.Object), logger, repository.Object);
        var client = new BandcampDownloadClient(Mock.Of<IBandcampDownloadQueue>(), registry, Mock.Of<IAlbumService>(), Mock.Of<IReleaseService>(), Mock.Of<ITrackService>(), Mock.Of<IConfigService>(), Mock.Of<IDiskProvider>(), Mock.Of<IRemotePathMappingService>(), Mock.Of<ILocalizationService>(), logger);
        client.Definition = new DownloadClientDefinition { Id = 7, Name = "Selected", Settings = saved };
        var result = JsonConvert.SerializeObject(client.RequestAction("clearLocalCredentials", new Dictionary<string, string>()));
        Assert.Contains(storageFails ? "StorageFailure" : "does not revoke", result);
        Assert.DoesNotContain("identity=private", result);
        Assert.Equal(storageFails ? "password-secret" : "", saved.Password);
        Assert.Equal(storageFails ? "identity=private" : "", saved.Cookies);
        Assert.Equal(storageFails ? "private@example.test" : "", saved.Email);
        Assert.Equal(storageFails ? "code-private" : "", saved.VerificationCode);
        if (!storageFails) { Assert.Null(saved.ChallengeUntilUtc); Assert.Null(saved.ChallengeProblem); }
        Assert.Equal("/music", saved.DownloadPath);
        Assert.Equal(consumer ? "Other owner" : "", saved.AuthConfiguration);
        repository.Verify(r => r.Find(It.Is<int>(id => id != 7)), Times.Never);
        repository.Verify(r => r.All(), Times.Never);
    }
}
