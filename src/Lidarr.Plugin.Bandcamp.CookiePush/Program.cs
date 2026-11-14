using Lidarr.Plugin.Bandcamp.CookiePush;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;
try
{
    return await CookiePushCommand.RunAsync(
        args,
        Console.IsInputRedirected ? Console.In : TextReader.Null,
        Console.Out,
        Console.Error,
        Environment.GetEnvironmentVariable,
        new HttpClientHandler(),
        new PlaywrightCookieCapture(),
        cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
