using Lidarr.Plugin.Bandcamp.CookiePush;

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await CookiePushCommand.RunAsync(
    args, Console.IsInputRedirected ? Console.In : TextReader.Null,
    Console.Out, Console.Error, Environment.GetEnvironmentVariable,
    new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
    new PlaywrightCookieCapture(), cancellation.Token);
