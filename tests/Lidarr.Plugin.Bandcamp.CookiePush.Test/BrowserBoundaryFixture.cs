using Microsoft.Playwright;
using Moq;
using Xunit;
namespace Lidarr.Plugin.Bandcamp.CookiePush.Test;
public class BrowserBoundaryFixture
{
    [Theory]
    [InlineData("https://evil.example/", true)]
    [InlineData("http://bandcamp.com/login", true)]
    [InlineData("https://artist.bandcamp.com/", true)]
    [InlineData("https://bandcamp.com/login", false)]
    public async Task Browser_boundary_blocks_navigation_outside_exact_https_bandcamp(string url, bool blocked)
    {
        Func<IRoute, Task>? routing = null;
        var context = new Mock<IBrowserContext>();
        var page = new Mock<IPage>();
        context.SetupGet(c => c.Pages).Returns(new[] { page.Object });
        context.Setup(c => c.RouteAsync("**/*", It.IsAny<Func<IRoute, Task>>(), It.IsAny<BrowserContextRouteOptions>()))
            .Callback<string, Func<IRoute, Task>, BrowserContextRouteOptions>((_, callback, _) => routing = callback)
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        await using var session = new PlaywrightBrowserSession(Mock.Of<IPlaywright>(), context.Object);
        await session.OpenLoginPageAsync(CancellationToken.None);
        Assert.NotNull(routing);
        var route = new Mock<IRoute>();
        var request = new Mock<IRequest>();
        request.SetupGet(r => r.Url).Returns(url);
        request.SetupGet(r => r.IsNavigationRequest).Returns(true);
        request.SetupGet(r => r.Frame).Returns(Mock.Of<IFrame>());
        route.SetupGet(r => r.Request).Returns(request.Object);
        await routing!(route.Object);
        route.Verify(r => r.AbortAsync(It.IsAny<string>()), blocked ? Times.Once() : Times.Never());
        route.Verify(r => r.ContinueAsync(It.IsAny<RouteContinueOptions>()), blocked ? Times.Never() : Times.Once());
    }
    [Fact]
    public async Task Closing_login_page_stops_capture_instead_of_polling_forever()
    {
        var context = new Mock<IBrowserContext>(); var page = new Mock<IPage>();
        context.SetupGet(c => c.Pages).Returns(new[] { page.Object });
        context.Setup(c => c.CookiesAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(Array.Empty<BrowserContextCookiesResult>());
        await using var session = new PlaywrightBrowserSession(Mock.Of<IPlaywright>(), context.Object);
        await session.OpenLoginPageAsync(CancellationToken.None);
        page.Raise(p => p.Close += null, page.Object, page.Object);
        await Assert.ThrowsAsync<BrowserWindowClosedException>(() => session.GetIdentityCookieAsync(CancellationToken.None));
    }
    [Theory]
    [InlineData(".evil.example", "/", true)]
    [InlineData(".bandcamp.com", "/", false)]
    [InlineData(".bandcamp.com", "/login", true)]
    public async Task Capture_rejects_unscoped_or_insecure_identity_from_browser_api(string domain, string path, bool secure)
    {
        var context = new Mock<IBrowserContext>();
        context.Setup(c => c.CookiesAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new[] {
            new BrowserContextCookiesResult { Name = "identity", Value = "sentinel", Domain = domain, Path = path, Secure = secure }
        });
        await using var session = new PlaywrightBrowserSession(Mock.Of<IPlaywright>(), context.Object);
        Assert.Null(await session.GetIdentityCookieAsync(CancellationToken.None));
    }
}
