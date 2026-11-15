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
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return path;
    }
}

public sealed class BrowserWindowClosedException(string message) : Exception(message);

internal sealed class PlaywrightBrowserSessionFactory : IBrowserSessionFactory
{
    public async Task<IBrowserSession> LaunchAsync(string profilePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var playwright = await Playwright.CreateAsync();
        IBrowserContext? context = null;
        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(profilePath, new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false,
                AcceptDownloads = false,
                ServiceWorkers = ServiceWorkerPolicy.Block,
                Timeout = 30000,
            });
            cancellationToken.ThrowIfCancellationRequested();
            return new PlaywrightBrowserSession(playwright, context);
        }
        catch
        {
            try { if (context != null) await context.DisposeAsync(); }
            finally { playwright.Dispose(); }
            throw;
        }
    }
}

internal sealed class PlaywrightBrowserSession(IPlaywright playwright, IBrowserContext context) : IBrowserSession
{
    private volatile bool _closed;

    public async Task OpenLoginPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.Close += (_, _) => _closed = true;
        await context.RouteAsync("**/*", route =>
        {
            var request = route.Request;
            var allowed = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                (uri.Host == "bandcamp.com" ||
                    (!request.IsNavigationRequest && (uri.Host.EndsWith(".bcbits.com", StringComparison.Ordinal) ||
                        uri.Host == "bcbits.com")) ||
                    ((!request.IsNavigationRequest || request.Frame.ParentFrame != null) &&
                        uri.Host is "www.google.com" or "www.gstatic.com" or "www.recaptcha.net" &&
                        uri.AbsolutePath.StartsWith("/recaptcha/", StringComparison.Ordinal)));
            return allowed ? route.ContinueAsync() : route.AbortAsync();
        });
        var page = context.Pages.FirstOrDefault() ?? throw Closed();
        page.Close += (_, _) => _closed = true;
        try
        {
            await page.GotoAsync("https://bandcamp.com/login", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
            }).WaitAsync(cancellationToken);
        }
        catch (PlaywrightException) when (_closed)
        {
            throw Closed();
        }
    }

    public async Task<string?> GetIdentityCookieAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed) throw Closed();
        try
        {
            var cookies = await context.CookiesAsync(["https://bandcamp.com"]);
            var identities = cookies.Where(cookie => cookie.Name == "identity" &&
                cookie.Domain is "bandcamp.com" or ".bandcamp.com" && cookie.Path == "/" && cookie.Secure &&
                !string.IsNullOrWhiteSpace(cookie.Value)).ToList();
            return identities.Count == 1 ? identities[0].Value : null;
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
        try { await context.DisposeAsync(); }
        finally { playwright.Dispose(); }
    }
}
