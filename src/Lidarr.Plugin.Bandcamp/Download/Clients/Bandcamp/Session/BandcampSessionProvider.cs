using System;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Session provider for one client definition. The session is the
    /// <see cref="BandcampDownloadSettings.Cookies"/> value, validated by the live fan_id lookup.
    /// When that cookie is dead and Email/Password are set, the provider logs in (one login at a
    /// time across concurrent callers) and writes the fresh cookie back to the settings.
    /// Bad credentials are never retried until the email or password changes.
    ///
    /// Extension points for later work:
    ///   - <see cref="GetSessionAsync"/> is the one path for "give me a usable session"; a
    ///     validation cache (using <see cref="State"/> and <see cref="Clock"/>) and
    ///     renewal-by-login slot in there.
    ///   - <see cref="ValidateAsync"/> is the one live check; it records <see cref="State"/>
    ///     for health reporting.
    ///   - <see cref="UpdateSettings"/> is where a settings change is observed (e.g. to reset
    ///     cooldowns when credentials change, or to write a renewed cookie back).
    /// </summary>
    public class BandcampSessionProvider : IBandcampSessionProvider
    {
        /// <summary>How long a successful validation is reused before a fresh live lookup.</summary>
        public static readonly TimeSpan ValidationCacheDuration = TimeSpan.FromMinutes(15);

        private readonly BandcampApiClient _apiClient;
        private readonly BandcampLoginClient? _loginClient;
        private readonly IBandcampSessionStore? _sessionStore;
        private readonly Logger _logger;
        private readonly object _loginLock = new();
        private Task<BandcampSession?>? _loginInFlight;
        private volatile string? _rejectedCredentials;
        private volatile LoginRecord? _lastLogin;
        private volatile BandcampDownloadSettings _settings;
        private volatile BandcampSessionState _state = BandcampSessionState.Initial;
        private volatile CachedSession? _cached;

        public BandcampSessionProvider(
            int definitionId,
            BandcampDownloadSettings settings,
            BandcampApiClient apiClient,
            Logger logger,
            Func<DateTime>? clock = null,
            BandcampLoginClient? loginClient = null,
            IBandcampSessionStore? sessionStore = null)
        {
            DefinitionId = definitionId;
            _settings = settings;
            _apiClient = apiClient;
            _loginClient = loginClient;
            _sessionStore = sessionStore;
            _logger = logger;
            Clock = clock ?? (() => DateTime.UtcNow);
        }

        public int DefinitionId { get; }

        public BandcampSessionState State => _state;

        public BandcampLoginOutcome? LastLoginOutcome => _lastLogin?.Outcome;

        public string? LastLoginMessage => _lastLogin?.Message;

        protected Func<DateTime> Clock { get; }

        protected BandcampDownloadSettings Settings => _settings;

        /// <summary>
        /// Points the provider at the client definition's latest settings. Lidarr builds a new
        /// settings object whenever the definition is saved, so the client hands it over on use.
        /// </summary>
        public virtual void UpdateSettings(BandcampDownloadSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Returns the last validated session while it is younger than
        /// <see cref="ValidationCacheDuration"/> and the configured cookie is unchanged;
        /// otherwise runs a live validation.
        /// </summary>
        public virtual Task<BandcampSession?> GetSessionAsync(CancellationToken cancellationToken = default)
        {
            var cached = _cached;
            if (cached != null &&
                cached.Session.Cookies == _settings.Cookies &&
                Clock() - cached.ValidatedAtUtc < ValidationCacheDuration)
            {
                return Task.FromResult<BandcampSession?>(cached.Session);
            }

            return ValidateOrLoginAsync(cancellationToken);
        }

        /// <summary>
        /// Live re-validation of the current settings' cookie (which picks up a cookie replaced
        /// since the session was handed out). Only when that cookie is dead, and credentials are
        /// set, does a login run, so non-auth failures never trigger a needless login.
        /// </summary>
        public virtual Task<BandcampSession?> RenewAsync(CancellationToken cancellationToken = default)
        {
            return ValidateOrLoginAsync(cancellationToken);
        }

        private async Task<BandcampSession?> ValidateOrLoginAsync(CancellationToken cancellationToken)
        {
            var settings = _settings;
            var deadCookie = settings.Cookies;
            var session = await ValidateAsync(cancellationToken).ConfigureAwait(false);
            if (session != null || _loginClient == null || !HasCredentials(settings))
            {
                return session;
            }

            return await LoginOnceAsync(settings, deadCookie, cancellationToken).ConfigureAwait(false);
        }

        private Task<BandcampSession?> LoginOnceAsync(BandcampDownloadSettings settings, string deadCookie, CancellationToken cancellationToken)
        {
            lock (_loginLock)
            {
                // Join a login already in progress.
                if (_loginInFlight != null)
                {
                    return _loginInFlight;
                }

                // Another caller already logged in and replaced the cookie we saw fail.
                if (settings.Cookies != deadCookie)
                {
                    return ValidateAsync(cancellationToken);
                }

                var task = Task.Run(() => LoginAsync(settings));
                _loginInFlight = task;
                task.ContinueWith(
                    _ =>
                    {
                        lock (_loginLock)
                        {
                            _loginInFlight = null;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                return task;
            }
        }

        private async Task<BandcampSession?> LoginAsync(BandcampDownloadSettings settings)
        {
            var credentials = CredentialKey(settings);
            if (credentials == _rejectedCredentials)
            {
                _logger.Debug("Bandcamp session [{0}]: credentials were rejected before; not retrying until they change", DefinitionId);
                return null;
            }

            _logger.Debug("Bandcamp session [{0}]: session invalid, logging in", DefinitionId);
            var result = await _loginClient!.LoginAsync(settings.Email, settings.Password).ConfigureAwait(false);
            _lastLogin = new LoginRecord(result.Outcome, result.Message);

            if (result.Outcome == BandcampLoginOutcome.BadCredentials)
            {
                _rejectedCredentials = credentials;
            }

            if (result.Outcome != BandcampLoginOutcome.Success || result.Cookies == null)
            {
                _logger.Debug("Bandcamp session [{0}]: login failed ({1})", DefinitionId, result.Outcome);
                return null;
            }

            // Write back: the in-memory settings first (every holder of this definition's
            // settings object sees it at once), then the persisted definition.
            settings.Cookies = result.Cookies;
            try
            {
                _sessionStore?.SaveCookies(DefinitionId, result.Cookies);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Bandcamp session [{0}]: logged in but could not save the new session to settings", DefinitionId);
            }

            return await ValidateAsync().ConfigureAwait(false);
        }

        private static bool HasCredentials(BandcampDownloadSettings settings)
        {
            return !string.IsNullOrWhiteSpace(settings.Email) && !string.IsNullOrWhiteSpace(settings.Password);
        }

        /// <summary>A hash, so rejected credentials aren't kept around in plain text.</summary>
        private static string CredentialKey(BandcampDownloadSettings settings)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(settings.Email + "\0" + settings.Password);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }

        public virtual async Task<BandcampSession?> ValidateAsync(CancellationToken cancellationToken = default)
        {
            var cookies = _settings.Cookies;

            if (string.IsNullOrWhiteSpace(cookies))
            {
                _cached = null;
                SetState(BandcampSessionStatus.Missing);
                return null;
            }

            long? fanId;
            try
            {
                fanId = await _apiClient.ResolveFanIdAsync(cookies).ConfigureAwait(false);
            }
            catch
            {
                _cached = null;
                SetState(BandcampSessionStatus.Error);
                throw;
            }

            if (fanId == null)
            {
                _cached = null;
                _logger.Debug("Bandcamp session [{0}]: fan_id lookup returned no fan; session invalid", DefinitionId);
                SetState(BandcampSessionStatus.Invalid);
                return null;
            }

            var session = new BandcampSession(cookies, fanId.Value);
            _cached = new CachedSession(session, Clock());
            SetState(BandcampSessionStatus.Valid);
            return session;
        }

        protected void SetState(BandcampSessionStatus status)
        {
            _state = new BandcampSessionState(status, Clock());
        }

        private sealed class LoginRecord
        {
            public LoginRecord(BandcampLoginOutcome outcome, string? message)
            {
                Outcome = outcome;
                Message = message;
            }

            public BandcampLoginOutcome Outcome { get; }

            public string? Message { get; }
        }

        private sealed class CachedSession
        {
            public CachedSession(BandcampSession session, DateTime validatedAtUtc)
            {
                Session = session;
                ValidatedAtUtc = validatedAtUtc;
            }

            public BandcampSession Session { get; }

            public DateTime ValidatedAtUtc { get; }
        }
    }
}
