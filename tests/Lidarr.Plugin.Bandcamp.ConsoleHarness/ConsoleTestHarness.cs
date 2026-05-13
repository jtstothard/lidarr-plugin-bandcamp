using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Bandcamp;

namespace Lidarr.Plugin.Bandcamp.ConsoleHarness
{
    /// <summary>
    /// Console test harness for validating Bandcamp search against the real endpoint.
    /// Usage: dotnet run -- [cookies]
    /// Alternatively set BANDCAMP_COOKIES environment variable.
    /// </summary>
    public static class Program
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static int Main(string[] args)
        {
            // Configure NLog for console output
            var config = new NLog.Config.LoggingConfiguration();
            var consoleTarget = new NLog.Targets.ConsoleTarget("console")
            {
                Layout = "${time} [${level:uppercase=true}] ${message} ${exception:format=tostring}"
            };
            config.AddTarget(consoleTarget);
            config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, consoleTarget);
            LogManager.Configuration = config;

            // Get cookies from args or env
            var cookies = args.Length > 0
                ? args[0]
                : Environment.GetEnvironmentVariable("BANDCAMP_COOKIES") ?? "";

            if (string.IsNullOrWhiteSpace(cookies))
            {
                Console.WriteLine("Usage: dotnet run -- <cookies>");
                Console.WriteLine("   Or: export BANDCAMP_COOKIES=<cookies>");
                Console.WriteLine();
                Console.WriteLine("Provide your Bandcamp session cookies (identity cookie).");
                return 1;
            }

            Console.WriteLine("=== Bandcamp Search Console Test Harness ===");
            Console.WriteLine();

            try
            {
                return TestSearch(cookies);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL: {ex.Message}");
                Logger.Error(ex, "Unhandled exception");
                return 2;
            }
        }

        private static int TestSearch(string cookies)
        {
            var searchTerm = "radiohead";
            var url = $"https://bandcamp.com/search?q={searchTerm}&item_type=a";
            Console.WriteLine($"Search URL: {url}");
            Console.WriteLine($"Cookie length: {cookies.Length} chars");
            Console.WriteLine();

            // Use raw HttpClient for the test
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            using var client = new System.Net.Http.HttpClient(handler);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", cookies);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            Console.WriteLine("Sending request...");
            var response = client.SendAsync(request).Result;
            var content = response.Content.ReadAsStringAsync().Result;

            Console.WriteLine($"Response status: {(int)response.StatusCode} {response.StatusCode}");
            Console.WriteLine($"Response length: {content.Length} chars");
            Console.WriteLine();

            // Show first 500 chars
            Console.WriteLine("--- First 500 chars ---");
            Console.WriteLine(content.Substring(0, Math.Min(500, content.Length)));
            Console.WriteLine("--- End ---");
            Console.WriteLine();

            // Detect JS challenge page
            if (content.Contains("cf-challenge") || content.Contains("challenge-platform") ||
                content.Contains("Just a moment") || content.Contains("Checking your browser"))
            {
                Console.WriteLine("⚠️  WARNING: Response appears to be a JS challenge page (Cloudflare).");
                Console.WriteLine("   This typically means the cookies are invalid or expired.");
                Console.WriteLine("   Try exporting fresh cookies from your browser.");
                return 3;
            }

            // Check for searchresult blocks
            var resultCount = Regex.Matches(content, @"class=""searchresult").Count;
            Console.WriteLine($"Search result blocks found: {resultCount}");

            if (resultCount == 0)
            {
                Console.WriteLine("No searchresult blocks found in response.");
                Console.WriteLine("This could mean:");
                Console.WriteLine("  1. Bandcamp changed their HTML structure");
                Console.WriteLine("  2. Cookies are not valid");
                Console.WriteLine("  3. Search endpoint returned unexpected format");
                return 4;
            }

            // Parse with our parser
            Console.WriteLine();
            Console.WriteLine("--- Parsing with BandcampParser ---");

            var settings = new BandcampIndexerSettings { Cookies = cookies };
            var logger = LogManager.GetCurrentClassLogger();
            var parser = new BandcampParser(settings, logger);

            var httpRequest = new NzbDrone.Common.Http.HttpRequest(url);
            var httpResponse = new HttpResponse(httpRequest, new HttpHeader(), content);
            var indexerRequest = new IndexerRequest(httpRequest);
            var indexerResponse = new IndexerResponse(indexerRequest, httpResponse);

            var results = parser.ParseResponse(indexerResponse);

            Console.WriteLine($"Parser returned {results.Count} results:");
            Console.WriteLine();

            foreach (var release in results)
            {
                Console.WriteLine($"  Title:  {release.Title}");
                Console.WriteLine($"  Artist: {release.Artist}");
                Console.WriteLine($"  Album:  {release.Album}");
                Console.WriteLine($"  Date:   {release.PublishDate:yyyy-MM-dd}");
                Console.WriteLine($"  URL:    {release.InfoUrl}");
                Console.WriteLine($"  Size:   {release.Size / (1024.0 * 1024.0):F1} MB");
                Console.WriteLine();
            }

            Console.WriteLine($"=== Test complete: {results.Count} results parsed ===");
            return results.Count > 0 ? 0 : 5;
        }
    }
}
