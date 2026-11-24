// Qualification harness for #51 (2026-11-24). It calls the production CookiePushCommand and
// PlaywrightCookieCapture with the production browser factory. The only additions are
// observation, credential autofill on the trusted Bandcamp login page, and a bounded
// cancellation (the same token Ctrl-C cancels in Program.cs). It never prints the identity,
// a prefix or a hash, the credentials, the API key, or the temporary profile path.
//
//   QualCheck cancel   opens the isolated login, waits 8 s, then cancels before login.
//   QualCheck login    autofills the Bandcamp login once and lets the helper finish.
//
// Environment: LIDARR_URL, LIDARR_API_KEY (helper), QUAL_OWNER, QUAL_FAN_ID,
// LOGIN_USERNAME, LOGIN_PASSWORD (login mode only), QUAL_HUMAN_WAIT_SECONDS (how long to
// leave a visible CAPTCHA or verification prompt for a person before cancelling; default 0).
using System.Reflection;
using System.Runtime.InteropServices;
using Lidarr.Plugin.Bandcamp.CookiePush;
using Microsoft.Playwright;

var mode = args.Length == 1 && args[0] is "login" or "cancel" ? args[0] : throw new ArgumentException("mode: login|cancel");
var owner = Environment.GetEnvironmentVariable("QUAL_OWNER") ?? throw new ArgumentException("QUAL_OWNER");
var fan = Environment.GetEnvironmentVariable("QUAL_FAN_ID") ?? throw new ArgumentException("QUAL_FAN_ID");
var humanWait = int.TryParse(Environment.GetEnvironmentVariable("QUAL_HUMAN_WAIT_SECONDS"), out var w) ? w : 0;

static HashSet<string> Profiles() =>
    Directory.GetDirectories(Path.GetTempPath(), "bandcamp-cookie-push-*").ToHashSet();

var before = Profiles();
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine("UTC_START=" + DateTime.UtcNow.ToString("O"));
Console.WriteLine("MODE=" + mode);
Console.WriteLine("RUNTIME=" + RuntimeInformation.FrameworkDescription);
Console.WriteLine("OS=" + RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture);
Console.WriteLine("DISPLAY_SET=" + !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")));
Console.WriteLine("PREEXISTING_PROFILES=" + before.Count);

var factory = new ObservingFactory(mode, stop, humanWait, before);
var code = await CookiePushCommand.RunAsync(
    ["--client-name", owner, "--fan-id", fan], TextReader.Null, Console.Out, Console.Error,
    Environment.GetEnvironmentVariable, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
    new PlaywrightCookieCapture(factory), stop.Token);

Console.WriteLine("HELPER_EXIT=" + code);
Console.WriteLine("PROFILE_SEEN_DURING_RUN=" + factory.ProfileSeen);
Console.WriteLine("NEW_PROFILES_REMAINING=" + Profiles().Except(before).Count());
Console.WriteLine("UTC_END=" + DateTime.UtcNow.ToString("O"));
return code;

sealed class ObservingFactory(string mode, CancellationTokenSource stop, int humanWait, HashSet<string> before) : IBrowserSessionFactory
{
    readonly IBrowserSessionFactory inner = (IBrowserSessionFactory)Activator.CreateInstance(
        typeof(PlaywrightCookieCapture).Assembly.GetType("Lidarr.Plugin.Bandcamp.CookiePush.PlaywrightBrowserSessionFactory")!, nonPublic: true)!;

    public bool ProfileSeen { get; private set; }

    public async Task<IBrowserSession> LaunchAsync(string path, CancellationToken ct)
    {
        ProfileSeen = Directory.Exists(path) && !before.Contains(path);
        var session = await inner.LaunchAsync(path, ct);
        Console.WriteLine("HEADED_BROWSER_LAUNCHED");
        var context = (IBrowserContext)session.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(f => f.FieldType == typeof(IBrowserContext)).GetValue(session)!;
        return new ObservingSession(session, context, mode, stop, humanWait);
    }

    sealed class ObservingSession(IBrowserSession inner, IBrowserContext context, string mode, CancellationTokenSource stop, int humanWait) : IBrowserSession
    {
        public async Task OpenLoginPageAsync(CancellationToken ct)
        {
            await inner.OpenLoginPageAsync(ct);
            var page = context.Pages[0];
            var u = new Uri(page.Url);
            if (u.Scheme != "https" || u.Host != "bandcamp.com" || u.AbsolutePath != "/login") throw new InvalidOperationException("unexpected login page");
            Console.WriteLine("LOGIN_PAGE_LOADED");
            if (mode == "cancel")
            {
                await page.WaitForTimeoutAsync(8000);
                Console.WriteLine("CANCEL_REQUESTED_BEFORE_LOGIN");
                stop.Cancel();
                return;
            }

            var necessary = page.GetByRole(AriaRole.Button, new() { Name = "Accept necessary only", Exact = true });
            if (await necessary.CountAsync() > 0 && await necessary.IsVisibleAsync()) await necessary.ClickAsync();
            await page.Locator("#username-field").FillAsync(Environment.GetEnvironmentVariable("LOGIN_USERNAME")!);
            await page.Locator("#password-field").FillAsync(Environment.GetEnvironmentVariable("LOGIN_PASSWORD")!);
            await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
            Console.WriteLine("LOGIN_SUBMITTED_ONCE");

            // Observe for up to 20 s: identity issued, CAPTCHA challenge, verification prompt or login error.
            bool challenge = false, verification = false, error = false, identity = false;
            for (var i = 0; i < 40 && !identity; i++)
            {
                await page.WaitForTimeoutAsync(500);
                identity = (await context.CookiesAsync(["https://bandcamp.com"])).Any(c => c.Name == "identity" && c.Value.Length > 0);
                foreach (var frame in await page.Locator("iframe[src*='recaptcha'][src*='bframe'], iframe[title*='challenge']").AllAsync())
                    challenge |= await frame.IsVisibleAsync();
                verification |= await page.Locator("#twofactor-field").IsVisibleAsync();
                error |= await page.Locator(".error, .error-text, [role=alert]").First.IsVisibleAsync();
                if (challenge || verification) break;
            }

            Console.WriteLine("IDENTITY_ISSUED_WITHIN_20S=" + identity);
            Console.WriteLine("CAPTCHA_CHALLENGE_VISIBLE=" + challenge);
            Console.WriteLine("VERIFICATION_PROMPT_VISIBLE=" + verification);
            Console.WriteLine("LOGIN_ERROR_VISIBLE=" + error);
            if (!identity)
            {
                Console.WriteLine("WAITING_FOR_PERSON_SECONDS=" + humanWait);
                stop.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, humanWait)));
            }
        }

        public Task<string?> GetIdentityCookieAsync(CancellationToken ct) => inner.GetIdentityCookieAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
