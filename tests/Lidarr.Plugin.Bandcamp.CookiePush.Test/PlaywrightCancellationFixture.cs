using Microsoft.Playwright;
using Moq;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.CookiePush.Test;

public sealed class PlaywrightCancellationFixture
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_launch_waits_for_settlement_and_cleans_profile(bool launchFails)
    {
        var launch = new TaskCompletionSource<IBrowserContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? profilePath = null;
        var chromium = new Mock<IBrowserType>();
        chromium.Setup(b => b.LaunchPersistentContextAsync(It.IsAny<string>(), It.IsAny<BrowserTypeLaunchPersistentContextOptions>()))
            .Returns((string path, BrowserTypeLaunchPersistentContextOptions options) =>
            {
                profilePath = path;
                Assert.Equal(30000, options.Timeout);
                started.SetResult();
                return launch.Task;
            });
        var playwright = new Mock<IPlaywright>();
        playwright.SetupGet(p => p.Chromium).Returns(chromium.Object);
        var context = new Mock<IBrowserContext>();
        using var cancellation = new CancellationTokenSource();
        var capture = new PlaywrightCookieCapture(new PlaywrightBrowserSessionFactory(() => Task.FromResult(playwright.Object)))
            .CaptureAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.False(capture.IsCompleted);
        Assert.True(Directory.Exists(profilePath));
        if (launchFails) launch.SetException(new PlaywrightException("Launch aborted"));
        else launch.SetResult(context.Object);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.False(Directory.Exists(profilePath));
        context.Verify(c => c.DisposeAsync(), launchFails ? Times.Never() : Times.Once());
        playwright.Verify(p => p.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Cancelling_navigation_closes_context_and_awaits_operation_before_capture_cleanup()
    {
        var navigation = new TaskCompletionSource<IResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = new Mock<IPage>();
        page.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()))
            .Returns(() => { started.SetResult(); return navigation.Task; });
        var context = new Mock<IBrowserContext>();
        context.SetupGet(c => c.Pages).Returns(new[] { page.Object });
        context.Setup(c => c.CloseAsync(It.IsAny<BrowserContextCloseOptions>()))
            .Returns(() => { closing.SetResult(); return Task.CompletedTask; });
        var playwright = new Mock<IPlaywright>();
        var factory = new SessionFactory(new PlaywrightBrowserSession(playwright.Object, context.Object));
        using var cancellation = new CancellationTokenSource();
        var capture = new PlaywrightCookieCapture(factory).CaptureAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            await closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(capture.IsCompleted);
            Assert.True(Directory.Exists(factory.ProfilePath));
            context.Verify(c => c.DisposeAsync(), Times.Never);
        }
        finally
        {
            navigation.TrySetException(new PlaywrightException("Navigation aborted"));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.False(Directory.Exists(factory.ProfilePath));
        context.Verify(c => c.DisposeAsync(), Times.Once);
        playwright.Verify(p => p.Dispose(), Times.Once);
    }

    private sealed class SessionFactory(IBrowserSession session) : IBrowserSessionFactory
    {
        public string? ProfilePath { get; private set; }
        public Task<IBrowserSession> LaunchAsync(string profilePath, CancellationToken cancellationToken)
        {
            ProfilePath = profilePath;
            return Task.FromResult(session);
        }
    }
}
