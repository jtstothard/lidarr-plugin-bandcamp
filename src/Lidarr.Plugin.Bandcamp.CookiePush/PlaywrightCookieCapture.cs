using Microsoft.Playwright;

namespace Lidarr.Plugin.Bandcamp.CookiePush;

public interface IBrowserSessionFactory
{
    Task<IBrowserSession> LaunchAsync(string profilePath, CancellationToken cancellationToken);
}

public interface IBrowserSession : IAsyncDisposable
{
    Task OpenLoginPageAsync(CancellationToken cancellationToken);

    Task<string?> GetIdentityCookieAsync(CancellationToken cancellationToken);
}

public sealed class PlaywrightCookieCapture : IBrowserCookieCapture
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private readonly IBrowserSessionFactory _sessionFactory;

    public PlaywrightCookieCapture()
        : this(new PlaywrightBrowserSessionFactory())
    {
    }

    public PlaywrightCookieCapture(IBrowserSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory;
    }

    public async Task<string> CaptureAsync(CancellationToken cancellationToken)
    {
        var profilePath = CreateProfileDirectory();
        try
        {
            await using var session = await _sessionFactory.LaunchAsync(profilePath, cancellationToken);
            await session.OpenLoginPageAsync(cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cookie = await session.GetIdentityCookieAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(cookie))
                {
                    return cookie;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        finally
        {
            Directory.Delete(profilePath, recursive: true);
        }
    }

    private static string CreateProfileDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bandcamp-cookie-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}

public sealed class BrowserWindowClosedException(string message) : Exception(message);

internal sealed class PlaywrightBrowserSessionFactory : IBrowserSessionFactory
{
    public async Task<IBrowserSession> LaunchAsync(string profilePath, CancellationToken cancellationToken)
    {
        var playwright = await Playwright.CreateAsync();
        try
        {
            var context = await playwright.Chromium.LaunchPersistentContextAsync(profilePath, new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false,
            });
            return new PlaywrightBrowserSession(playwright, context);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }
}

internal sealed class PlaywrightBrowserSession(IPlaywright playwright, IBrowserContext context) : IBrowserSession
{
    private volatile bool _closed;

    public async Task OpenLoginPageAsync(CancellationToken cancellationToken)
    {
        context.Close += (_, _) => _closed = true;
        var page = context.Pages.FirstOrDefault() ?? throw Closed();
        try
        {
            await page.GotoAsync("https://bandcamp.com/login", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
            });
        }
        catch (PlaywrightException) when (_closed)
        {
            throw Closed();
        }
    }

    public async Task<string?> GetIdentityCookieAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cookies = await context.CookiesAsync(["https://bandcamp.com"]);
            return cookies.FirstOrDefault(cookie =>
                string.Equals(cookie.Name, "identity", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(cookie.Value))?.Value;
        }
        catch (PlaywrightException) when (_closed)
        {
            throw Closed();
        }
    }

    private static BrowserWindowClosedException Closed()
    {
        return new BrowserWindowClosedException("The login window closed before Bandcamp login completed. No changes were made.");
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        playwright.Dispose();
    }
}
