using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Moq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Common.Http;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Http;

public class BandcampSecretHygieneFixture
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public BandcampSecretHygieneFixture(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    private static readonly string[] Secrets = { "password-53-very-private", "verification-53-private", "generated-53-private", "identity-53-private" };

    private void AssertSafe(string captured)
    {
        _output.WriteLine(captured);
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, captured, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret[..12], captured, StringComparison.OrdinalIgnoreCase);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
            Assert.DoesNotContain(hash, captured, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(hash[..12], captured, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task LoginAndSessionDiagnosticsNeverEchoSentinelResponseBodiesOrCredentials()
    {
        using var factory = new LogFactory();
        var target = new MemoryTarget { Layout = "${message} ${exception:format=tostring}" };
        var config = new LoggingConfiguration(); config.AddRule(LogLevel.Trace, LogLevel.Fatal, target); factory.Configuration = config;
        var logger = factory.GetLogger("secret-test");
        var http = new Mock<IHttpClient>();
        http.Setup(h => h.ExecuteAsync(It.IsAny<HttpRequest>())).ReturnsAsync((HttpRequest r) => new HttpResponse(r, new HttpHeader(), string.Join(" ", Secrets), HttpStatusCode.OK));
        var client = new BandcampHttpClient(http.Object, logger);
        var login = await new NzbDrone.Core.Download.Clients.Bandcamp.Session.BandcampLoginClient(client, logger).LoginAsync("private@example.test", Secrets[0], Secrets[1]);
        Assert.NotEqual(NzbDrone.Core.Download.Clients.Bandcamp.Session.BandcampLoginOutcome.Success, login.Outcome);
        var api = new NzbDrone.Core.Download.Clients.Bandcamp.BandcampApiClient(client, logger);
        var provider = new NzbDrone.Core.Download.Clients.Bandcamp.Session.BandcampSessionProvider(1,
            new NzbDrone.Core.Download.Clients.Bandcamp.BandcampDownloadSettings { Cookies = Secrets[3], Password = Secrets[0], Email = "private@example.test", VerificationCode = Secrets[1] }, api, logger);
        Assert.Null(await provider.GetSessionAsync());
        await api.FindPurchaseByUrlAsync(Secrets[3], 1, "https://artist.bandcamp.com/album/title?token=" + Secrets[2]);
        await api.GetDownloadPageDataAsync(Secrets[3], "https://bandcamp.com/download?token=" + Secrets[2]);
        AssertSafe(string.Join("\n", target.Logs) + login.Message + Newtonsoft.Json.JsonConvert.SerializeObject(provider.State));
    }

    [Fact]
    public async Task DownloadFailureDoesNotExposeSignedAlbumUrlInLogsOrException()
    {
        using var factory = new LogFactory();
        var target = new MemoryTarget { Layout = "${message} ${exception:format=tostring}" };
        var config = new LoggingConfiguration(); config.AddRule(LogLevel.Trace, LogLevel.Fatal, target); factory.Configuration = config;
        var logger = factory.GetLogger("secret-test");
        var http = new Mock<IHttpClient>();
        http.Setup(h => h.ExecuteAsync(It.IsAny<HttpRequest>())).ReturnsAsync((HttpRequest r) => new HttpResponse(r, new HttpHeader(), "{\"items\":[]}", HttpStatusCode.OK));
        var client = new BandcampHttpClient(http.Object, logger);
        var api = new NzbDrone.Core.Download.Clients.Bandcamp.BandcampApiClient(client, logger);
        var provider = new Mock<NzbDrone.Core.Download.Clients.Bandcamp.Session.IBandcampSessionProvider>();
        var session = new NzbDrone.Core.Download.Clients.Bandcamp.Session.BandcampSession("identity=" + Secrets[3], 1);
        provider.Setup(p => p.GetSessionAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(session);
        provider.Setup(p => p.RenewAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(session);
        var item = new NzbDrone.Core.Download.Clients.Bandcamp.BandcampDownloadItem { DownloadId = "safe-id", AlbumUrl = "https://artist.bandcamp.com/album/title?token=" + Secrets[2], SessionProvider = provider.Object };
        var exception = await Record.ExceptionAsync(() => new NzbDrone.Core.Download.Clients.Bandcamp.BandcampDownloadProxy(api, client, logger).ExecuteDownloadAsync(item, default));
        Assert.NotNull(exception);
        AssertSafe(string.Join("\n", target.Logs) + exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureNeverExposesRequestOrUpstreamException(bool raw)
    {
        using var factory = new LogFactory();
        var target = new MemoryTarget { Layout = "${message} ${exception:format=tostring}" };
        var config = new LoggingConfiguration(); config.AddRule(LogLevel.Trace, LogLevel.Fatal, target); factory.Configuration = config;
        var http = new Mock<IHttpClient>();
        http.Setup(h => h.ExecuteAsync(It.IsAny<HttpRequest>())).ThrowsAsync(new InvalidOperationException(string.Join(" ", Secrets.Concat(Secrets.Select(s => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))))))));
        var client = new BandcampHttpClient(http.Object, factory.GetLogger("secret-test"));
        var request = client.CreateRequestBuilder("https://bandcamp.com/download?token=" + Secrets[2], Secrets[3]).Build();
        var exception = await Record.ExceptionAsync(async () => { if (raw) await client.ExecuteRawAsync(request); else await client.ExecuteAsync(request); });
        Assert.NotNull(exception);
        AssertSafe(string.Join("\n", target.Logs) + exception);
    }

    [Theory]
    [InlineData("https://bandcamp.com/api/fancollection/1/collection_items", true)]
    [InlineData("http://bandcamp.com", false)]
    [InlineData("https://artist.bandcamp.com/album/title", false)]
    [InlineData("https://bcbits.com/archive.zip", false)]
    [InlineData("https://bandcamp.com.evil.test", false)]
    [InlineData("https://unrelated.test", false)]
    [InlineData("https://bandcamp.com:444", false)]
    public async Task IdentityOnlyFlowsToAccountHttpsOrigin(string url, bool allowed)
    {
        var http = new Mock<IHttpClient>();
        HttpRequest? sent = null;
        http.Setup(h => h.ExecuteAsync(It.IsAny<HttpRequest>())).ReturnsAsync((HttpRequest r) =>
        {
            sent = r;
            return new HttpResponse(r, new HttpHeader { { "Content-Type", "application/zip" } }, new byte[] { 1, 2 }, HttpStatusCode.OK);
        });
        var logger = LogManager.GetCurrentClassLogger();
        var client = new BandcampHttpClient(http.Object, logger);
        using var destination = new System.IO.MemoryStream();
        await new NzbDrone.Core.Download.Clients.Bandcamp.BandcampApiClient(client, logger).DownloadFileAsync(Secrets[3], url, destination);
        var request = Assert.IsType<HttpRequest>(sent);
        Assert.Equal(allowed, request.Headers.GetValues("Cookie")?.Any() == true);
        Assert.False(request.AllowAutoRedirect); Assert.False(request.StoreRequestCookie); Assert.False(request.StoreResponseCookie);
    }
}
