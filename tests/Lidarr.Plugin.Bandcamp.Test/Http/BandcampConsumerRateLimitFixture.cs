using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Download.Clients.Bandcamp.Session;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Http;

public class BandcampConsumerRateLimitFixture
{
    [Theory]
    [InlineData(false, "120", 120)]
    [InlineData(false, "Sat, 14 Nov 2026 12:02:00 GMT", 120)]
    [InlineData(false, "invalid", 300)]
    [InlineData(true, "120", 120)]
    [InlineData(true, "Sat, 14 Nov 2026 12:02:00 GMT", 120)]
    [InlineData(true, "invalid", 300)]
    public async Task CollectionAndFile429BlockBothConsumersWithoutLoginAndRecoverWhenDue(bool file, string retryAfter, int waitSeconds)
    {
        var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var initial = now;
        var requests = 0;
        var limited = false;
        var transport = new Mock<IHttpClient>();
        transport.Setup(h => h.ExecuteAsync(It.IsAny<HttpRequest>())).ReturnsAsync((HttpRequest request) =>
        {
            requests++;
            if (request.Url.FullUri == "https://bandcamp.com")
                return new HttpResponse(request, new HttpHeader(), "var pagedata = {\"fan_id\":123};", HttpStatusCode.OK);
            return new HttpResponse(request, new HttpHeader { { "Retry-After", retryAfter }, { "Content-Type", "application/zip" } },
                limited ? "slow down" : "{\"items\":[]}", limited ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
        });
        var logger = LogManager.GetCurrentClassLogger();
        var client = new BandcampHttpClient(transport.Object, logger);
        var api = new BandcampApiClient(client, logger);
        var settings = new BandcampDownloadSettings { Cookies = "identity=prior", Email = "fan@example.test", Password = "password" };
        var owner = new BandcampSessionProvider(7, settings, api, logger, () => now, new BandcampLoginClient(client, logger));
        var search = new BandcampSharedSessionProvider(() => owner);
        var download = new BandcampSharedSessionProvider(() => owner);
        var session = (await search.GetSessionAsync())!;
        session.RateLimitObserver!("600");
        session.RateLimitObserver!("120");
        Assert.Contains(initial.AddSeconds(600).ToString("yyyy-MM-dd HH:mm:ss"), search.State.Problem!.Description);
        now = initial.AddSeconds(600);
        session = (await search.GetSessionAsync())!;
        initial = now;
        if (retryAfter.EndsWith("GMT", StringComparison.Ordinal))
            retryAfter = now.AddSeconds(waitSeconds).ToString("r");
        limited = true;
        using (api.ObserveSession(session))
        {
            using var destination = new MemoryStream();
            Assert.NotNull(await Record.ExceptionAsync(async () =>
            {
                if (file) await api.DownloadFileAsync(session.Cookies, "https://bcbits.com/archive", destination);
                else await api.GetCollectionAsync(session.Cookies, session.FanId);
            }));
            Assert.Equal(BandcampSessionStatus.RateLimited, search.State.Status);
            Assert.Equal(search.State.Status, download.State.Status);
            Assert.Same(search.State.Problem, download.State.Problem);
            Assert.Contains(initial.AddSeconds(waitSeconds).ToString("yyyy-MM-dd HH:mm:ss"), search.State.Problem!.Description);
            var count = requests;
            Assert.Null(await search.GetSessionAsync());
            Assert.Null(await download.RenewAsync());
            Assert.Null(await download.ReplaceIdentityAsync("identity=candidate"));
            // Even a session snapshot already handed out cannot issue the next file/collection request.
            Assert.NotNull(await Record.ExceptionAsync(() => api.GetCollectionAsync(session.Cookies, session.FanId)));
            now = initial.AddSeconds(waitSeconds - 1);
            Assert.Null(await search.GetSessionAsync());
            Assert.Equal(count, requests);
        }
        limited = false;
        now = initial.AddSeconds(waitSeconds);
        var recovered = (await download.GetSessionAsync())!;
        Assert.Equal("identity=prior", recovered.Cookies);
        using (api.ObserveSession(recovered)) await api.GetCollectionAsync(recovered.Cookies, recovered.FanId);
        Assert.Equal(BandcampSessionStatus.Valid, search.State.Status);
        Assert.Null(owner.LastLoginOutcome);
        Assert.Equal("identity=prior", settings.Cookies);
        Assert.Equal(5, requests); // initial validation, 429, due validation, recovered collection
    }
}
