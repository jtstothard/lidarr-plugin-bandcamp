using System;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Cookie-mode session provider: the session is the <see cref="BandcampDownloadSettings.Cookies"/>
    /// value of the owning client definition, validated by the live fan_id lookup.
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
        private readonly Logger _logger;
        private volatile BandcampDownloadSettings _settings;
        private volatile BandcampSessionState _state = BandcampSessionState.Initial;
        private volatile CachedSession? _cached;

        public BandcampSessionProvider(
            int definitionId,
            BandcampDownloadSettings settings,
            BandcampApiClient apiClient,
            Logger logger,
            Func<DateTime>? clock = null)
        {
            DefinitionId = definitionId;
            _settings = settings;
            _apiClient = apiClient;
            _logger = logger;
            Clock = clock ?? (() => DateTime.UtcNow);
        }

        public int DefinitionId { get; }

        public BandcampSessionState State => _state;

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

            return ValidateAsync(cancellationToken);
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
