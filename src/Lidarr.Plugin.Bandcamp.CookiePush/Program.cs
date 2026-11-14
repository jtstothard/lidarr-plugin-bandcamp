using Lidarr.Plugin.Bandcamp.CookiePush;

return await CookiePushCommand.RunAsync(
    args,
    Console.In,
    Console.Out,
    Console.Error,
    Environment.GetEnvironmentVariable,
    new HttpClientHandler());
