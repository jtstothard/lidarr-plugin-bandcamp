using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Http.Bandcamp;

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
        private volatile RateLimitRecord? _rateLimit;
        private readonly object _rateLimitLock = new();

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
            lock (_loginLock)
            {
                _settings = settings;
            }
        }

        /// <summary>
        /// Returns the last validated session while it is younger than
        /// <see cref="ValidationCacheDuration"/> and the configured cookie is unchanged;
        /// otherwise runs a live validation.
        /// </summary>
        public virtual Task<BandcampSession?> GetSessionAsync(CancellationToken cancellationToken = default)
        {
            var cached = _cached;
            if (_rateLimit is { } limit)
            {
                return Clock() < limit.UntilUtc
                    ? Task.FromResult<BandcampSession?>(null)
                    : ValidateOrLoginAsync(cancellationToken);
            }

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
            var validation = await ValidateAttemptAsync(settings, cancellationToken).ConfigureAwait(false);
            if (validation.Session != null || !validation.CanLogin || _loginClient == null || !settings.HasCredentials())
            {
                return validation.Session;
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
            var expectedCookies = settings.Cookies;
            if (_rateLimit is { } limit && Clock() < limit.UntilUtc)
            {
                SetState(BandcampSessionStatus.RateLimited, limit.Problem);
                return null;
            }

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
            if (settings.ChallengeUntilUtc is DateTime until && until > Clock())
            {
                var persistedProblem = settings.ChallengeProblem == BandcampSessionProblem.VerificationNeeded.Code
                    ? BandcampSessionProblem.VerificationNeeded
                    : settings.ChallengeProblem == BandcampSessionProblem.LoginResponseUnrecognized.Code
                        ? BandcampSessionProblem.LoginResponseUnrecognized : BandcampSessionProblem.CaptchaRequired;
                // A stale settings snapshot must never downgrade a pending CAPTCHA to
                // verification (and thereby let a code bypass CAPTCHA suppression).
                if (challenge == null || Clock() - challenge.AtUtc >= ChallengeCooldown ||
                    (persistedProblem == BandcampSessionProblem.CaptchaRequired && challenge.Problem != persistedProblem) ||
                    (persistedProblem == challenge.Problem && until > challenge.AtUtc + ChallengeCooldown))
                {
                    challenge = new ChallengeRecord(persistedProblem, until - ChallengeCooldown);
                }
            }
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

            // Consume the submitted code even if durable storage is unavailable.
            if (hasCode)
            {
                settings.VerificationCode = string.Empty;
            }

            if (result.Outcome == BandcampLoginOutcome.RateLimited)
            {
                if (hasCode && !ClearVerificationCode(settings))
                {
                    return null;
                }

                SetRateLimit(result.RetryAfter);
                return null;
            }

            var problem = result.Outcome switch
            {
                BandcampLoginOutcome.BadCredentials => BandcampSessionProblem.BadCredentials,
                BandcampLoginOutcome.VerificationRequired => BandcampSessionProblem.VerificationNeeded,
                BandcampLoginOutcome.CaptchaRequired => BandcampSessionProblem.CaptchaRequired,
                BandcampLoginOutcome.Error => BandcampSessionProblem.LoginFailed,
                BandcampLoginOutcome.LoginPageRejected => BandcampSessionProblem.LoginPageRejected,
                BandcampLoginOutcome.LoginCallbackRejected => BandcampSessionProblem.LoginCallbackRejected,
                BandcampLoginOutcome.SessionNotIssued => BandcampSessionProblem.LoginSessionMissing,
                BandcampLoginOutcome.RequestFailed => BandcampSessionProblem.LoginTransportFailure,
                BandcampLoginOutcome.UnrecognizedResponse => BandcampSessionProblem.LoginResponseUnrecognized,
                _ => null
            };
            if (result.Outcome == BandcampLoginOutcome.BadCredentials)
            {
                _rejectedCredentials = credentials;
            }
            else if (problem == BandcampSessionProblem.VerificationNeeded || problem == BandcampSessionProblem.CaptchaRequired ||
                problem == BandcampSessionProblem.LoginResponseUnrecognized)
            {
                _challenge = new ChallengeRecord(problem, Clock());
                settings.ChallengeUntilUtc = Clock() + ChallengeCooldown;
                settings.ChallengeProblem = problem.Code;
                try
                {
                    if (_sessionStore == null)
                    {
                        throw new InvalidOperationException("Session storage is unavailable");
                    }

                    _sessionStore.SaveChallenge(DefinitionId, settings.ChallengeUntilUtc.Value, problem.Code, hasCode);
                }
                catch (Exception)
                {
                    SetState(BandcampSessionStatus.Error, BandcampSessionProblem.StorageFailure);
                    return null;
                }
            }
            else if (result.Outcome == BandcampLoginOutcome.Success)
            {
                _challenge = null;
            }

            if (hasCode && problem != BandcampSessionProblem.VerificationNeeded &&
                problem != BandcampSessionProblem.CaptchaRequired &&
                problem != BandcampSessionProblem.LoginResponseUnrecognized && !ClearVerificationCode(settings))
            {
                return null;
            }

            if (problem != null)
            {
                SetState(result.Outcome is BandcampLoginOutcome.Error or BandcampLoginOutcome.LoginPageRejected or
                    BandcampLoginOutcome.LoginCallbackRejected or BandcampLoginOutcome.SessionNotIssued or BandcampLoginOutcome.RequestFailed
                    ? BandcampSessionStatus.Error : BandcampSessionStatus.Invalid, problem);
            }

            if (result.Outcome != BandcampLoginOutcome.Success || result.Cookies == null)
            {
                _logger.Debug("Bandcamp session [{0}]: login failed ({1})", DefinitionId, result.Outcome);
                return null;
            }

            return await ReplaceIdentityAsync(result.Cookies, settings, expectedCookies, credentials, cancellationToken).ConfigureAwait(false);
        }

        private Task<BandcampSession?> ObserveIdentityAsync(string expectedHeader, string candidate, CancellationToken cancellationToken)
        {
            lock (_loginLock)
            {
                var settings = _settings;
                var cached = _cached;
                // Only a still-current, successfully validated session authorizes passive rotation.
                // Bind the transaction to the identity that actually produced the response.
                if (cached == null || cached.Session.Cookies != settings.Cookies ||
                    Clock() - cached.ValidatedAtUtc >= ValidationCacheDuration ||
                    BandcampHttpClient.NormalizeCookieHeader(settings.Cookies) != expectedHeader)
                {
                    return Task.FromResult<BandcampSession?>(null);
                }

                return ReplaceIdentityAsync(candidate, settings, settings.Cookies, settings.CredentialKey(), cancellationToken, cached.Session.FanId);
            }
        }

        public virtual Task<BandcampSession?> ReplaceIdentityAsync(string cookies, CancellationToken cancellationToken = default)
        {
            lock (_loginLock)
            {
                var settings = _settings;
                return ReplaceIdentityAsync(cookies, settings, settings.Cookies, settings.CredentialKey(), cancellationToken);
            }
        }

        public Task<BandcampSession?> ReplaceIdentityForFanAsync(string cookies, long expectedFanId, int expectedDefinitionId, CancellationToken cancellationToken = default, Func<bool>? tryBeginCommit = null)
        {
            if (expectedDefinitionId != DefinitionId || expectedFanId <= 0 || !cookies.StartsWith("identity=", StringComparison.Ordinal) ||
                cookies.Length <= 9 || cookies.Length > 16393 ||
                cookies.Substring(9).Any(c => c <= 32 || c >= 127 || c == ';' || c == ','))
            {
                return Task.FromResult<BandcampSession?>(null);
            }

            lock (_loginLock)
            {
                var settings = _settings;
                return ReplaceIdentityAsync(cookies, settings, settings.Cookies, settings.CredentialKey(), cancellationToken, expectedFanId, tryBeginCommit);
            }
        }

        private async Task<BandcampSession?> ReplaceIdentityAsync(
            string cookies, BandcampDownloadSettings settings, string expectedCookies, string credentials, CancellationToken cancellationToken, long? expectedFanId = null, Func<bool>? tryBeginCommit = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_rateLimit is { } limit && Clock() < limit.UntilUtc)
            {
                SetState(BandcampSessionStatus.RateLimited, limit.Problem);
                return null;
            }

            _rateLimit = null;
            if (string.IsNullOrWhiteSpace(cookies))
            {
                SetState(BandcampSessionStatus.Missing, BandcampSessionProblem.Missing);
                return null;
            }

            var validation = await _apiClient.ValidateSessionAsync(cookies).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (ApplyValidationFailure(validation)) return null;

            var fanId = validation.FanId;
            if (fanId == null)
            {
                SetState(BandcampSessionStatus.Invalid, BandcampSessionProblem.CookieExpired);
                return null;
            }

            if (expectedFanId.HasValue && fanId != expectedFanId.Value)
            {
                SetState(BandcampSessionStatus.Invalid, BandcampSessionProblem.WrongAccount);
                return null;
            }

            lock (_loginLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_settings.Cookies != expectedCookies || _settings.CredentialKey() != credentials)
                {
                    SetState(BandcampSessionStatus.Error, BandcampSessionProblem.ReplacementConflict);
                    return null;
                }

                try
                {
                    if (_sessionStore == null)
                    {
                        throw new InvalidOperationException("Session storage is unavailable");
                    }

                    // Cancellation and the irreversible commit boundary arbitrate under one operation gate.
                    if (tryBeginCommit != null && !tryBeginCommit()) return null;

                    if (!_sessionStore.TryReplaceCookies(DefinitionId, expectedCookies, credentials, cookies))
                    {
                        SetState(BandcampSessionStatus.Error, BandcampSessionProblem.ReplacementConflict);
                        return null;
                    }
                }
                catch (Exception)
                {
                    SetState(BandcampSessionStatus.Error, BandcampSessionProblem.StorageFailure);
                    return null;
                }

                // Persistence is the commit point. Only now publish the validated identity.
                _settings.Cookies = cookies;
                settings.Cookies = cookies;
                var session = CreateSession(cookies, fanId.Value);
                _cached = new CachedSession(session, Clock());
                SetState(BandcampSessionStatus.Valid);
                return session;
            }
        }

        /// <summary>The code is single-use: clear it in memory and in the saved definition.</summary>
        private bool ClearVerificationCode(BandcampDownloadSettings settings)
        {
            lock (_loginLock)
            {
                settings.VerificationCode = string.Empty;
            }

            try
            {
                if (_sessionStore == null)
                {
                    throw new InvalidOperationException("Session storage is unavailable");
                }

                _sessionStore.ClearVerificationCode(DefinitionId);
                return true;
            }
            catch (Exception)
            {
                SetState(BandcampSessionStatus.Error, BandcampSessionProblem.StorageFailure);
                return false;
            }
        }

        public virtual Task<BandcampSession?> ValidateAsync(CancellationToken cancellationToken = default)
        {
            return ValidateAsync(_settings, cancellationToken);
        }

        /// <summary>Validates the cookie of the given settings snapshot (the one a login wrote to).</summary>
        private async Task<BandcampSession?> ValidateAsync(BandcampDownloadSettings settings, CancellationToken cancellationToken)
        {
            return (await ValidateAttemptAsync(settings, cancellationToken).ConfigureAwait(false)).Session;
        }

        private async Task<(BandcampSession? Session, bool CanLogin)> ValidateAttemptAsync(BandcampDownloadSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cookies = settings.Cookies;
            if (_rateLimit is { } limit && Clock() < limit.UntilUtc)
            {
                SetState(BandcampSessionStatus.RateLimited, limit.Problem);
                return (null, false);
            }

            _rateLimit = null;

            if (string.IsNullOrWhiteSpace(cookies))
            {
                _cached = null;
                SetState(BandcampSessionStatus.Missing, BandcampSessionProblem.Missing);
                return (null, true);
            }

            var validation = await _apiClient.ValidateSessionAsync(cookies).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (ApplyValidationFailure(validation)) return (null, false);

            var fanId = validation.FanId;
            cancellationToken.ThrowIfCancellationRequested();
            if (fanId == null)
            {
                _cached = null;
                _logger.Debug("Bandcamp session [{0}]: fan_id lookup returned no fan; session invalid", DefinitionId);
                SetState(BandcampSessionStatus.Invalid, BandcampSessionProblem.CookieExpired);
                return (null, true);
            }

            var session = CreateSession(cookies, fanId.Value);
            _cached = new CachedSession(session, Clock());
            SetState(BandcampSessionStatus.Valid);
            if (validation.IdentityCandidate is { } candidate)
            {
                var replacement = await ObserveIdentityAsync(BandcampHttpClient.NormalizeCookieHeader(cookies), candidate, cancellationToken).ConfigureAwait(false);
                return (replacement ?? session, false);
            }

            return (session, false);
        }

        // Failure classification is shared; callers retain their distinct cache/login/commit policy.
        private bool ApplyValidationFailure(BandcampValidationResult validation)
        {
            switch (validation.Outcome)
            {
                case BandcampValidationOutcome.RateLimited:
                    SetRateLimit(validation.RetryAfter);
                    return true;
                case BandcampValidationOutcome.ChallengeOrUnrecognized:
                    SetState(BandcampSessionStatus.ChallengeOrUnrecognized, BandcampSessionProblem.ChallengeOrUnrecognized);
                    return true;
                case BandcampValidationOutcome.Unavailable:
                    SetState(BandcampSessionStatus.Error, BandcampSessionProblem.Unavailable);
                    return true;
                default:
                    return false;
            }
        }

        private BandcampSession CreateSession(string cookies, long fanId) => new(cookies, fanId)
        {
            IdentityObserver = ObserveIdentityAsync,
            RateLimitObserver = retryAfter => SetRateLimit(retryAfter),
            RequestAllowed = () => _rateLimit is not { } limit || Clock() >= limit.UntilUtc
        };

        private void SetRateLimit(string? retryAfter)
        {
            var until = BandcampRetryAfter.UntilUtc(retryAfter, Clock());
            lock (_rateLimitLock)
            {
                if (_rateLimit is { } current && current.UntilUtc > until)
                    until = current.UntilUtc;
                var problem = BandcampSessionProblem.RateLimited(until);
                _rateLimit = new RateLimitRecord(until, problem);
                SetState(BandcampSessionStatus.RateLimited, problem);
            }
        }

        protected void SetState(BandcampSessionStatus status, BandcampSessionProblem? problem = null)
        {
            var now = Clock();
            var mode = !string.IsNullOrWhiteSpace(_settings.Cookies)
                ? "Web identity" + (_settings.HasCredentials() ? " (password fallback configured)" : " (manual cookie)")
                : _settings.HasCredentials() ? "Web password fallback (no identity)" : "Not configured";
            _state = new BandcampSessionState(status, now, problem,
                status == BandcampSessionStatus.Valid ? now : _state.LastSuccessfulValidationUtc, mode);
        }

        private sealed record RateLimitRecord(DateTime UntilUtc, BandcampSessionProblem Problem);

        private sealed record LoginRecord(BandcampLoginOutcome Outcome, string? Message);

        private sealed record ChallengeRecord(BandcampSessionProblem Problem, DateTime AtUtc);

        private sealed record CachedSession(BandcampSession Session, DateTime ValidatedAtUtc);

        private sealed record InFlightLogin(string CredentialKey, Task<BandcampSession?> Task);
    }
}
