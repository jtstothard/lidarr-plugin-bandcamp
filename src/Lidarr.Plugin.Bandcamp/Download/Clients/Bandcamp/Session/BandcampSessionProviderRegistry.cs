using System;
using System.Collections.Concurrent;
using NLog;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Process-wide map of download client definition id to its session provider.
    /// Resolved as a singleton so every client instance, the queue and (later) a health check
    /// share one provider per definition.
    /// </summary>
    public interface IBandcampSessionProviderRegistry
    {
        /// <summary>
        /// Returns the provider for the definition, creating it on first use, and points it at
        /// the given (latest) settings.
        /// </summary>
        IBandcampSessionProvider Get(int definitionId, BandcampDownloadSettings settings);

        /// <summary>Returns the provider for the definition if one exists.</summary>
        IBandcampSessionProvider? Find(int definitionId);
    }

    public class BandcampSessionProviderRegistry : IBandcampSessionProviderRegistry
    {
        private readonly ConcurrentDictionary<int, BandcampSessionProvider> _providers = new();
        private readonly BandcampApiClient _apiClient;
        private readonly Logger _logger;
        private readonly Func<DateTime>? _clock;
        private readonly BandcampLoginClient? _loginClient;
        private readonly IBandcampSessionStore? _sessionStore;

        public BandcampSessionProviderRegistry(BandcampApiClient apiClient, Logger logger)
            : this(apiClient, null, null, logger, null)
        {
        }

        public BandcampSessionProviderRegistry(BandcampApiClient apiClient, Logger logger, Func<DateTime>? clock)
            : this(apiClient, null, null, logger, clock)
        {
        }

        /// <summary>
        /// Production constructor (the container picks the one with the most resolvable
        /// arguments): credential login and session write-back enabled.
        /// </summary>
        public BandcampSessionProviderRegistry(
            BandcampApiClient apiClient,
            BandcampLoginClient loginClient,
            IBandcampSessionStore sessionStore,
            Logger logger)
            : this(apiClient, loginClient, sessionStore, logger, null)
        {
        }

        public BandcampSessionProviderRegistry(
            BandcampApiClient apiClient,
            BandcampLoginClient? loginClient,
            IBandcampSessionStore? sessionStore,
            Logger logger,
            Func<DateTime>? clock)
        {
            _apiClient = apiClient;
            _loginClient = loginClient;
            _sessionStore = sessionStore;
            _logger = logger;
            _clock = clock;
        }

        public IBandcampSessionProvider Get(int definitionId, BandcampDownloadSettings settings)
        {
            var provider = _providers.GetOrAdd(
                definitionId,
                id => new BandcampSessionProvider(id, settings, _apiClient, _logger, _clock, _loginClient, _sessionStore));
            provider.UpdateSettings(settings);
            return provider;
        }

        public IBandcampSessionProvider? Find(int definitionId)
        {
            return _providers.TryGetValue(definitionId, out var provider) ? provider : null;
        }
    }
}
