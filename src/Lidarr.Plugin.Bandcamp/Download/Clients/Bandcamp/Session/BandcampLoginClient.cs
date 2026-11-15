using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Http.Bandcamp;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>Typed result of one login attempt.</summary>
    public enum BandcampLoginOutcome
    {
        /// <summary>Logged in; an identity cookie was issued.</summary>
        Success,

        /// <summary>Bandcamp rejected the email or password.</summary>
        BadCredentials,

        /// <summary>Bandcamp wants an emailed verification code (new device).</summary>
        VerificationRequired,

        /// <summary>Bandcamp wants a captcha solved; only a pasted cookie can recover.</summary>
        CaptchaRequired,

        /// <summary>Unclassified login failure; does not establish a network outage.</summary>
        Error,

        RateLimited,

        UnrecognizedResponse,

        LoginPageRejected,

        LoginCallbackRejected,

        SessionNotIssued,

        RequestFailed
    }

    public sealed class BandcampLoginResult
    {
        private BandcampLoginResult(BandcampLoginOutcome outcome, string? cookies, string? message, string? retryAfter = null)
        {
            Outcome = outcome;
            Cookies = cookies;
            Message = message;
            RetryAfter = retryAfter;
        }

        public BandcampLoginOutcome Outcome { get; }

        /// <summary>Gets the cookie string ("identity=...") on success, otherwise null. Never log this.</summary>
        public string? Cookies { get; }

        /// <summary>Gets a non-secret diagnostic for failures.</summary>
        public string? Message { get; }

        public string? RetryAfter { get; }

        public static BandcampLoginResult Success(string cookies) => new(BandcampLoginOutcome.Success, cookies, null);

        public static BandcampLoginResult Failed(BandcampLoginOutcome outcome, string? message = null, string? retryAfter = null) => new(outcome, null, message, retryAfter);
    }

    /// <summary>
    /// Email + password to identity cookie, using Bandcamp's web login form:
    /// GET https://bandcamp.com/login (crumb + pre-login cookies), then a form POST to
    /// https://bandcamp.com/login_cb with the page's csrf-token as X-CSRF-TOKEN, which answers JSON ({"ok":true} or {"ok":false,"errors":[...]})
    /// and sets the identity cookie on success. Goes through BandcampHttpClient for rate
    /// limiting and credential-safe logging.
    /// </summary>
    public class BandcampLoginClient
    {
        private const string LoginPageUrl = "https://bandcamp.com/login";
        private const string LoginCallbackUrl = "https://bandcamp.com/login_cb";

        private static readonly Regex CrumbsRegex = new(
            @"id=""js-crumbs-data""\s+data-crumbs=""([^""]+)""",
            RegexOptions.Compiled);

        private static readonly Regex CsrfTokenRegex = new(
            @"<meta\s+name=""csrf-token""\s+content=""([^""]+)""",
            RegexOptions.Compiled);

        private readonly BandcampHttpClient _httpClient;
        private readonly Logger _logger;

        public BandcampLoginClient(BandcampHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public virtual async Task<BandcampLoginResult> LoginAsync(string email, string password, string? verificationCode = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var pageBuilder = _httpClient.CreateRequestBuilder(LoginPageUrl, string.Empty);
                pageBuilder.SuppressHttpError = true;
                var page = await _httpClient.ExecuteAsync(pageBuilder.Build()).ConfigureAwait(false);

                if ((int)page.StatusCode == 429)
                {
                    var values = page.Headers?.GetValues("Retry-After");
                    return BandcampLoginResult.Failed(BandcampLoginOutcome.RateLimited,
                        retryAfter: values?.Length == 1 ? values[0] : null);
                }

                if (page.StatusCode != HttpStatusCode.OK)
                {
                    return BandcampLoginResult.Failed(BandcampLoginOutcome.LoginPageRejected, "Bandcamp could not load the login page");
                }

                var preLoginCookies = string.Join("; ", page.GetCookies().Select(kv => $"{kv.Key}={kv.Value}"));
                var crumb = ExtractLoginCrumb(page.Content ?? string.Empty);
                cancellationToken.ThrowIfCancellationRequested();

                var builder = _httpClient.CreateRequestBuilder(LoginCallbackUrl, preLoginCookies);
                builder.SuppressHttpError = true;
                builder.Headers.Set("Accept", "application/json, text/javascript, */*; q=0.01");
                builder.Headers.Set("X-Requested-With", "XMLHttpRequest");
                builder.Headers.Set("Referer", LoginPageUrl);

                // Bandcamp's own login form sends the page's csrf-token meta as this header.
                var csrf = CsrfTokenRegex.Match(page.Content ?? string.Empty);
                if (csrf.Success)
                {
                    builder.Headers.Set("X-CSRF-TOKEN", WebUtility.HtmlDecode(csrf.Groups[1].Value));
                }
                builder.Post()
                    .AddFormParameter("user.name", email)
                    .AddFormParameter("login.password", password)
                    .AddFormParameter("login.twofactor", verificationCode ?? string.Empty)
                    .AddFormParameter("login.twofactor_remember", string.Empty)
                    .AddFormParameter("login.from", string.Empty);

                if (crumb != null)
                {
                    builder.AddFormParameter("login.crumb", crumb);
                }

                var response = await _httpClient.ExecuteAsync(builder.Build()).ConfigureAwait(false);

                if ((int)response.StatusCode == 429)
                {
                    var values = response.Headers?.GetValues("Retry-After");
                    return BandcampLoginResult.Failed(BandcampLoginOutcome.RateLimited,
                        retryAfter: values?.Length == 1 ? values[0] : null);
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Debug("Bandcamp login: login_cb returned HTTP {0}", (int)response.StatusCode);
                    return BandcampLoginResult.Failed(BandcampLoginOutcome.LoginCallbackRejected, "Bandcamp login submission returned an unsuccessful HTTP status");
                }

                var result = Classify(response.Content ?? string.Empty);
                if (result != null)
                {
                    return result;
                }

                var identity = response.GetCookies().TryGetValue("identity", out var value) ? value : null;
                if (string.IsNullOrWhiteSpace(identity))
                {
                    _logger.Debug("Bandcamp login: login_cb reported ok but set no identity cookie");
                    return BandcampLoginResult.Failed(BandcampLoginOutcome.SessionNotIssued, "Bandcamp accepted login but issued no identity cookie");
                }

                return BandcampLoginResult.Success("identity=" + identity);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The exception text can echo request details, so it stays out of the
                // user-facing message and the log; only its type is logged.
                _logger.Debug("Bandcamp login: request failed ({0})", ex.GetType().Name);
                return BandcampLoginResult.Failed(BandcampLoginOutcome.RequestFailed, "Bandcamp login request could not be completed");
            }
        }

        private static string? ExtractLoginCrumb(string html)
        {
            var match = CrumbsRegex.Match(html);
            if (!match.Success)
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(match.Groups[1].Value));
                return doc.RootElement.TryGetProperty("login_cb", out var crumb) ? crumb.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Returns a failure result, or null when the body says ok.</summary>
        private BandcampLoginResult? Classify(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return BandcampLoginResult.Failed(BandcampLoginOutcome.UnrecognizedResponse);
                }

                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    return null;
                }

                var fields = new List<string>();
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    foreach (var error in errors.EnumerateArray())
                    {
                        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.String)
                        {
                            fields.Add(field.GetString()!.ToLowerInvariant());
                        }
                    }
                }

                if (fields.Any(f => f.Contains("captcha")))
                {
                    return BandcampLoginResult.Failed(BandcampLoginOutcome.CaptchaRequired, "Bandcamp requires a captcha");
                }

                if (fields.Any(f => f.Contains("twofactor") || f.Contains("verification")))
                {
                    return BandcampLoginResult.Failed(BandcampLoginOutcome.VerificationRequired, "Bandcamp requires a verification code");
                }

                if (fields.Any(f => f.Contains("password") || f.Contains("user.name") || f.Contains("username") || f.Contains("email")))
                {
                    return BandcampLoginResult.Failed(BandcampLoginOutcome.BadCredentials, "Bandcamp rejected the email or password");
                }

                _logger.Debug("Bandcamp login: unrecognised login_cb response");
                return BandcampLoginResult.Failed(BandcampLoginOutcome.UnrecognizedResponse, "Unrecognized login response from Bandcamp");
            }
            catch (JsonException)
            {
                _logger.Debug("Bandcamp login: login_cb response was not JSON");
                return BandcampLoginResult.Failed(BandcampLoginOutcome.UnrecognizedResponse, "Unrecognized login response from Bandcamp");
            }
        }
    }
}
