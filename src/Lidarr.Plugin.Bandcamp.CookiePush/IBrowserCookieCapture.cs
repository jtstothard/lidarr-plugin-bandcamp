namespace Lidarr.Plugin.Bandcamp.CookiePush;

public interface IBrowserCookieCapture
{
    Task<string> CaptureAsync(CancellationToken cancellationToken);
}
