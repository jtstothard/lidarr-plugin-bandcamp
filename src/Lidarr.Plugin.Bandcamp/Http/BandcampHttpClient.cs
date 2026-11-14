using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.TPL;

namespace NzbDrone.Core.Http.Bandcamp
{
    /// <summary>
    /// HTTP client wrapper for Bandcamp requests with rate limiting,
    /// browser-like user-agent, cookie injection, and credential-safe logging.
    /// </summary>
    public class BandcampHttpClient
    {
        private const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private const string BrowserAccept =
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

        private static readonly TimeSpan RateLimitInterval = TimeSpan.FromSeconds(2);

        private readonly AsyncLocal<Func<string, string, Task>?> _identityObserver = new();
        private readonly AsyncLocal<Action<string?>?> _rateLimitObserver = new();
        private readonly AsyncLocal<Func<bool>?> _canRequest = new();

        internal IDisposable ObserveSession(Func<string, string, Task> observer, Action<string?> rateLimitObserver, Func<bool> canRequest)
        {
            var previous = _identityObserver.Value;
            var previousRateLimit = _rateLimitObserver.Value;
            var previousCanRequest = _canRequest.Value;
            _identityObserver.Value = observer;
            _rateLimitObserver.Value = rateLimitObserver;
            _canRequest.Value = canRequest;
            return new ObservationScope(() =>
            {
                _identityObserver.Value = previous;
                _rateLimitObserver.Value = previousRateLimit;
                _canRequest.Value = previousCanRequest;
            });
        }

        private sealed class ObservationScope : IDisposable
        {
            private Action? _restore;

            internal ObservationScope(Action restore) => _restore = restore;

            public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
        }

        private readonly Func<HttpRequest, Task<HttpResponse>> _execute;
        private readonly Logger _logger;

        // The only public constructor is the host composition path. Do not inject IHttpClient:
        // its private ExecuteRequestAsync unconditionally traces signed URLs/error bodies.
        public BandcampHttpClient(BandcampHttpDispatcher dispatcher, IRateLimitService rateLimitService, Logger logger)
        {
            _execute = async request =>
            {
                if (request.RateLimit != TimeSpan.Zero)
                    await rateLimitService.WaitAndPulseAsync(request.Url.Host, request.RateLimitKey, request.RateLimit).ConfigureAwait(false);
                // Another consumer may have received 429 while this request waited for its slot.
                if (_canRequest.Value?.Invoke() == false) throw new BandcampRateLimitedException();
                // No host cookie jar, interceptors, redirects or request/response string logging.
                // Managed dispatcher retains proxy, TLS validation, timeout and streaming behavior.
                return await dispatcher.GetResponseAsync(request, new CookieContainer()).ConfigureAwait(false);
            };
            _logger = logger;
        }

        // Fixture transport seam; never selected by the host container.
        internal BandcampHttpClient(IHttpClient httpClient, Logger logger)
        {
            _execute = httpClient.ExecuteAsync;
            _logger = logger;
        }

        /// <summary>
        /// Creates an HttpRequestBuilder pre-configured for Bandcamp with browser-like
        /// headers, cookie injection, and rate limiting.
        /// </summary>
        /// <param name="url">Full URL to request.</param>
        /// <param name="cookies">Raw cookie string from browser (e.g. "identity=xxx; js=1").</param>
        public HttpRequestBuilder CreateRequestBuilder(string url, string cookies)
        {
            var builder = new HttpRequestBuilder(url)
            {
                RateLimit = RateLimitInterval
            };

            // Set RateLimitKey via PostProcess since HttpRequestBuilder doesn't expose it directly
            builder.PostProcess = req =>
            {
                req.RateLimitKey = "bandcamp";
                // Never let the host jar or a redirect expand the account credential scope.
                req.StoreRequestCookie = false;
                req.StoreResponseCookie = false;
                req.AllowAutoRedirect = false;
                req.LogHttpError = false;
                req.LogResponseContent = false;
            };

            // Browser-like headers so Bandcamp serves full pages
            builder.Headers.Set("User-Agent", BrowserUserAgent);
            builder.Headers.Set("Accept", BrowserAccept);
            builder.Headers.Set("Accept-Language", "en-US,en;q=0.9");

            // Inject cookies via the Cookie header (not via builder.Cookies dictionary)
            // so they're sent as-is without per-cookie parsing.
            // Users may paste just the identity cookie value, or the full cookie string.
            // If it doesn't contain '=', assume it's just the identity value.
            var cookieHeader = NormalizeCookieHeader(cookies);
            if (BandcampIdentityCookie.IsAccountOrigin(url) && !string.IsNullOrWhiteSpace(cookieHeader))
            {
                builder.Headers.Set("Cookie", cookieHeader);
            }

            return builder;
        }

        public static string NormalizeCookieHeader(string cookies)
        {
            if (string.IsNullOrWhiteSpace(cookies))
            {
                return string.Empty;
            }

            var cookieHeader = cookies.Trim();
            return cookieHeader.Contains('=') ? cookieHeader : $"identity={cookieHeader}";
        }

        /// <summary>
        /// Executes an HTTP GET request and returns the raw HttpResponse.
        /// Use .Content on the response to access the string body.
        /// Logs method and status without URLs, bodies or credential values.
        /// </summary>
        public async Task<HttpResponse> ExecuteAsync(HttpRequest request, bool observeIdentity = true)
        {
            if (_canRequest.Value?.Invoke() == false) throw new BandcampRateLimitedException();
            // URLs may contain signed download credentials; log the method only.
            _logger.Debug("Bandcamp request: {0}", request.Method);

            try
            {
                var response = await _execute(request);
                if (response.HasHttpError && (int)response.StatusCode != 429 && !request.SuppressHttpError &&
                    (request.SuppressHttpErrorStatusCodes == null || !request.SuppressHttpErrorStatusCodes.Contains(response.StatusCode)))
                    throw new System.IO.IOException("Bandcamp HTTP request failed");
                if (observeIdentity && (int)response.StatusCode == 429 &&
                    (!request.SuppressHttpError || _rateLimitObserver.Value != null))
                {
                    var values = response.Headers.GetValues("Retry-After");
                    _rateLimitObserver.Value?.Invoke(values?.Length == 1 ? values[0] : null);
                    throw new BandcampRateLimitedException();
                }

                if (observeIdentity)
                {
                    await ObserveIdentityAsync(request, response).ConfigureAwait(false);
                }

                _logger.Debug("Bandcamp response: {0} ({1} bytes)",
                    (int)response.StatusCode,
                    response.ResponseData?.Length ?? 0);

                return response;
            }
            catch (BandcampRateLimitedException) { throw; }
            catch (OperationCanceledException)
            {
                throw new OperationCanceledException("Bandcamp request cancelled");
            }
            catch (Exception)
            {
                _logger.Debug("Bandcamp request failed; check network and service availability");
                // Upstream exceptions can embed cookies, bodies and signed URLs. No inner exception.
                throw new System.IO.IOException("Bandcamp request failed; check network and service availability");
            }
        }

        internal static string? GetIdentityCandidate(HttpResponse response)
        {
            var request = response.Request;
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 ||
                !BandcampIdentityCookie.IsAccountOrigin(request.Url.FullUri))
            {
                return null;
            }

            var sent = request.Headers.GetValues("Cookie");
            if (sent?.Length != 1 || BandcampIdentityCookie.Identity(sent[0]) == null)
            {
                return null;
            }

            var candidate = BandcampIdentityCookie.Candidate(response.Headers.GetValues("Set-Cookie"));
            return candidate == BandcampIdentityCookie.Identity(sent[0]) ? null : candidate;
        }

        private async Task ObserveIdentityAsync(HttpRequest request, HttpResponse response)
        {
            if (!BandcampIdentityCookie.IsAccountOrigin(request.Url.FullUri)) return;
            var candidate = GetIdentityCandidate(response);
            if (candidate == null) return;
            var sent = request.Headers.GetValues("Cookie");
            if (sent?.Length != 1) return;

            var observer = _identityObserver.Value;
            if (observer != null)
            {
                await observer(sent[0], candidate).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Executes an HTTP request and returns the raw response (for non-string content).
        /// </summary>
        public Task<HttpResponse> ExecuteRawAsync(HttpRequest request) => ExecuteAsync(request);
    }

    internal sealed class BandcampRateLimitedException : System.IO.IOException
    {
        public BandcampRateLimitedException() : base("Bandcamp rate limited requests; wait before trying again; do not change credentials") { }
    }
}
