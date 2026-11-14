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
    /// time per credentials across concurrent callers) and writes the fresh cookie back to the
    /// settings. Bad credentials are never retried until the email or password changes.
    /// </summary>
    public class BandcampSessionProvider : IBandcampSessionProvider
    {
        /// <summary>How long a successful validation is reused before a fresh live lookup.</summary>
        public static readonly TimeSpan ValidationCacheDuration = TimeSpan.FromMinutes(15);

        /// <summary>After a login challenge, at most one login attempt runs per this window.</summary>
        public static readonly TimeSpan ChallengeCooldown = TimeSpan.FromHours(6);

        private readonly BandcampApiClient _apiClient;
        private readonly BandcampLoginClient? _loginClient;
        private readonly IBandcampSessionStore? _sessionStore;
        private readonly Logger _logger;
        private readonly object _loginLock = new();
        private InFlightLogin? _loginInFlight;
        private volatile string? _rejectedCredentials;
        private volatile LoginRecord? _lastLogin;
        private volatile ChallengeRecord? _challenge;
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
            var session = await ValidateAsync(settings, cancellationToken).ConfigureAwait(false);
            if (session != null || _loginClient == null || !settings.HasCredentials())
            {
                return session;
            }

            return await LoginOnceAsync(settings, deadCookie, cancellationToken).ConfigureAwait(false);
        }

        private async Task<BandcampSession?> LoginOnceAsync(BandcampDownloadSettings settings, string deadCookie, CancellationToken cancellationToken)
        {
            var credentials = settings.CredentialKey();
            while (true)
            {
                Task<BandcampSession?> task;
                bool joined;
                lock (_loginLock)
                {
                    var inFlight = _loginInFlight;
                    if (inFlight != null)
                    {
                        // Join a login for the same credentials; otherwise wait for it to finish
                        // and then log in with ours, so no caller gets another account's result.
                        task = inFlight.Task;
                        joined = inFlight.CredentialKey == credentials;
                    }
                    else if (settings.Cookies != deadCookie)
                    {
                        // Another caller already logged in and replaced the cookie we saw fail.
                        task = ValidateAsync(settings, cancellationToken);
                        joined = true;
                    }
                    else
                    {
                        task = Task.Run(() => LoginAsync(settings, credentials, cancellationToken), cancellationToken);
                        _loginInFlight = new InFlightLogin(credentials, task);
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
                        joined = true;
                    }
                }

                if (joined)
                {
                    return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The other login's failure is its own caller's to report.
                }
            }
        }

        private async Task<BandcampSession?> LoginAsync(BandcampDownloadSettings settings, string credentials, CancellationToken cancellationToken)
        {
            if (credentials == _rejectedCredentials)
            {
                _logger.Debug("Bandcamp session [{0}]: credentials were rejected before; not retrying until they change", DefinitionId);
                SetState(BandcampSessionStatus.Invalid, BandcampSessionProblem.BadCredentials);
                return null;
            }

            // A verification code answers a verification challenge, so it skips that cooldown
            // (but not a captcha's, which a code can't solve).
            var code = settings.VerificationCode?.Trim();
            var hasCode = !string.IsNullOrEmpty(code);
            var challenge = _challenge;
            var codeAnswersChallenge = hasCode && challenge?.Problem == BandcampSessionProblem.VerificationNeeded;
            if (!codeAnswersChallenge && challenge != null && Clock() - challenge.AtUtc < ChallengeCooldown)
            {
                _logger.Debug("Bandcamp session [{0}]: login challenge pending; not retrying until the cooldown ends", DefinitionId);
                SetState(BandcampSessionStatus.Invalid, challenge.Problem);
                return null;
            }

            _logger.Debug("Bandcamp session [{0}]: session invalid, logging in", DefinitionId);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _loginClient!.LoginAsync(settings.Email, settings.Password, hasCode ? code : null, cancellationToken).ConfigureAwait(false);
            _lastLogin = new LoginRecord(result.Outcome, result.Message);

            if (hasCode)
            {
                ClearVerificationCode(settings);
            }

            var problem = result.Outcome switch
            {
                BandcampLoginOutcome.BadCredentials => BandcampSessionProblem.BadCredentials,
                BandcampLoginOutcome.VerificationRequired => BandcampSessionProblem.VerificationNeeded,
                BandcampLoginOutcome.CaptchaRequired => BandcampSessionProblem.CaptchaRequired,
                _ => null
            };
            if (result.Outcome == BandcampLoginOutcome.BadCredentials)
            {
                _rejectedCredentials = credentials;
            }
            else if (problem != null)
            {
                _challenge = new ChallengeRecord(problem, Clock());
            }
            else if (result.Outcome == BandcampLoginOutcome.Success)
            {
                _challenge = null;
            }

            if (problem != null)
            {
                SetState(BandcampSessionStatus.Invalid, problem);
            }

            if (result.Outcome != BandcampLoginOutcome.Success || result.Cookies == null)
            {
                _logger.Debug("Bandcamp session [{0}]: login failed ({1})", DefinitionId, result.Outcome);
                return null;
            }

            // Write back: the in-memory settings first (every holder of this definition's
            // settings object sees it at once), then the persisted definition.
            lock (_loginLock)
            {
                settings.Cookies = result.Cookies;
            }

            try
            {
                _sessionStore?.SaveCookies(DefinitionId, result.Cookies);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Bandcamp session [{0}]: logged in but could not save the new session to settings", DefinitionId);
            }

            return await ValidateAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The code is single-use: clear it in memory and in the saved definition.</summary>
        private void ClearVerificationCode(BandcampDownloadSettings settings)
        {
            lock (_loginLock)
            {
                settings.VerificationCode = string.Empty;
            }

            try
            {
                _sessionStore?.ClearVerificationCode(DefinitionId);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Bandcamp session [{0}]: could not clear the verification code from settings", DefinitionId);
            }
        }

        public virtual Task<BandcampSession?> ValidateAsync(CancellationToken cancellationToken = default)
        {
            return ValidateAsync(_settings, cancellationToken);
        }

        /// <summary>Validates the cookie of the given settings snapshot (the one a login wrote to).</summary>
        private async Task<BandcampSession?> ValidateAsync(BandcampDownloadSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cookies = settings.Cookies;

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

            cancellationToken.ThrowIfCancellationRequested();
            if (fanId == null)
            {
                _cached = null;
                _logger.Debug("Bandcamp session [{0}]: fan_id lookup returned no fan; session invalid", DefinitionId);
                SetState(BandcampSessionStatus.Invalid, BandcampSessionProblem.CookieExpired);
                return null;
            }

            var session = new BandcampSession(cookies, fanId.Value);
            _cached = new CachedSession(session, Clock());
            SetState(BandcampSessionStatus.Valid);
            return session;
        }

        protected void SetState(BandcampSessionStatus status, BandcampSessionProblem? problem = null)
        {
            _state = new BandcampSessionState(status, Clock(), problem);
        }

        private sealed record LoginRecord(BandcampLoginOutcome Outcome, string? Message);

        private sealed record ChallengeRecord(BandcampSessionProblem Problem, DateTime AtUtc);

        private sealed record CachedSession(BandcampSession Session, DateTime ValidatedAtUtc);

        private sealed record InFlightLogin(string CredentialKey, Task<BandcampSession?> Task);
    }
}
