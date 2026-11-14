using Lidarr.Plugin.Bandcamp.CookiePush;

return await CookiePushCommand.RunAsync(
    args,
    Console.IsInputRedirected ? Console.In : TextReader.Null,
    Console.Out,
    Console.Error,
    Environment.GetEnvironmentVariable,
    new HttpClientHandler(),
    new PlaywrightCookieCapture());
