using Xunit;

namespace Lidarr.Plugin.Bandcamp.CookiePush.Test;

public sealed class PlaywrightCookieCaptureFixture
{
    private const string Cookie = "browser-cookie-that-must-stay-secret";

    [Fact]
    public async Task Capture_uses_a_fresh_temporary_profile_and_deletes_it_after_success()
    {
        var session = new FakeBrowserSession(null, Cookie);
        var factory = new FakeBrowserSessionFactory(session);
        var capture = new PlaywrightCookieCapture(factory);

        var cookie = await capture.CaptureAsync(CancellationToken.None);

        Assert.Equal(Cookie, cookie);
        Assert.True(factory.ProfileDirectoryExistedAtLaunch);
        Assert.False(Directory.Exists(factory.ProfilePath));
        Assert.True(session.Disposed);
        Assert.Equal("https://bandcamp.com/login", session.OpenedUrl);
        Assert.Equal(2, session.CookieReadCount);
    }

    [Fact]
    public async Task Capture_deletes_profile_and_disposes_session_when_cookie_read_fails()
    {
        var session = new FailingBrowserSession();
        var factory = new FakeBrowserSessionFactory(session);
        var capture = new PlaywrightCookieCapture(factory);

        await Assert.ThrowsAsync<BrowserWindowClosedException>(() => capture.CaptureAsync(CancellationToken.None));

        Assert.False(Directory.Exists(factory.ProfilePath));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Capture_deletes_profile_when_launch_fails()
    {
        var factory = new FailingBrowserSessionFactory();
        var capture = new PlaywrightCookieCapture(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.CaptureAsync(CancellationToken.None));

        Assert.False(Directory.Exists(factory.ProfilePath));
    }

    [Fact]
    public async Task Capture_deletes_profile_on_cancellation()
    {
        var session = new FakeBrowserSession((string?)null);
        var factory = new FakeBrowserSessionFactory(session);
        var capture = new PlaywrightCookieCapture(factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => capture.CaptureAsync(cancellation.Token));

        Assert.False(Directory.Exists(factory.ProfilePath));
        Assert.True(session.Disposed);
    }

    private sealed class FakeBrowserSessionFactory(IBrowserSession session) : IBrowserSessionFactory
    {
        public string? ProfilePath { get; private set; }

        public bool ProfileDirectoryExistedAtLaunch { get; private set; }

        public Task<IBrowserSession> LaunchAsync(string profilePath, CancellationToken cancellationToken)
        {
            ProfilePath = profilePath;
            ProfileDirectoryExistedAtLaunch = Directory.Exists(profilePath);
            Assert.Empty(Directory.GetFileSystemEntries(profilePath));
            return Task.FromResult(session);
        }
    }

    private sealed class FailingBrowserSessionFactory : IBrowserSessionFactory
    {
        public string? ProfilePath { get; private set; }

        public Task<IBrowserSession> LaunchAsync(string profilePath, CancellationToken cancellationToken)
        {
            ProfilePath = profilePath;
            throw new InvalidOperationException("Browser launch failed");
        }
    }

    private sealed class FailingBrowserSession : IBrowserSession
    {
        public bool Disposed { get; private set; }

        public Task OpenLoginPageAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> GetIdentityCookieAsync(CancellationToken cancellationToken)
        {
            throw new BrowserWindowClosedException("The login window closed before Bandcamp login completed. No changes were made.");
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeBrowserSession(params string?[] cookies) : IBrowserSession
    {
        private int _cookieReadCount;

        public int CookieReadCount => _cookieReadCount;

        public bool Disposed { get; private set; }

        public string? OpenedUrl { get; private set; }

        public Task OpenLoginPageAsync(CancellationToken cancellationToken)
        {
            OpenedUrl = "https://bandcamp.com/login";
            return Task.CompletedTask;
        }

        public Task<string?> GetIdentityCookieAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(cookies[_cookieReadCount++]);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
