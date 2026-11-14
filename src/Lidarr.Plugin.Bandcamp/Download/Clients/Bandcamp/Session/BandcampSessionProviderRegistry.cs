using System;
using System.Collections.Concurrent;
using System.Linq;
using NLog;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Process-wide map of download client definition id to its session provider.
    /// Resolved as a singleton so every client instance, the queue and the health check
    /// share one provider per definition.
    /// </summary>
    public interface IBandcampSessionProviderRegistry
    {
        /// <summary>
        /// Returns the provider for the definition, creating it on first use, and points it at
        /// the given (latest) settings.
        /// </summary>
        IBandcampSessionProvider Get(int definitionId, BandcampDownloadSettings settings);

        IBandcampSessionProvider GetNamed(string name);

        bool ClearLocalCredentials(int definitionId) => false;

        // Host Test validates the proposed unsaved settings; execution uses saved settings.
        IBandcampSessionProvider GetForTest(int definitionId, BandcampDownloadSettings settings) => Get(definitionId, settings);

        /// <summary>Returns the provider for the definition if one exists.</summary>
        IBandcampSessionProvider? Find(int definitionId);
    }

    public class BandcampSessionProviderRegistry : IBandcampSessionProviderRegistry
    {
        private readonly ConcurrentDictionary<int, BandcampSessionProvider> _providers = new();
        private readonly ConcurrentDictionary<int, IBandcampSessionProvider> _consumers = new();
        private readonly BandcampApiClient _apiClient;
        private readonly IDownloadClientRepository? _repository;
        private readonly Logger _logger;
        private readonly Func<DateTime>? _clock;
        private readonly BandcampLoginClient? _loginClient;
        private readonly IBandcampSessionStore? _sessionStore;

        /// <summary>The only public constructor, so Lidarr's container picks it unambiguously.</summary>
        public BandcampSessionProviderRegistry(
            BandcampApiClient apiClient,
            BandcampLoginClient? loginClient,
            IBandcampSessionStore? sessionStore,
            Logger logger,
            IDownloadClientRepository? repository = null)
            : this(apiClient, loginClient, sessionStore, logger, (Func<DateTime>?)null)
        {
            _repository = repository;
        }

        /// <summary>Test seam: a controllable clock.</summary>
        internal BandcampSessionProviderRegistry(
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
            if (!string.IsNullOrWhiteSpace(settings.AuthConfiguration))
            {
                var selected = GetNamed(settings.AuthConfiguration);
                _consumers[definitionId] = selected;
                return selected;
            }

            IBandcampSessionProvider provider = _repository != null && definitionId > 0
                ? new BandcampSharedSessionProvider(() =>
                {
                    var saved = _repository.Find(definitionId);
                    if (saved?.Settings is not BandcampDownloadSettings current)
                    {
                        return null;
                    }

                    return string.IsNullOrWhiteSpace(current.AuthConfiguration)
                        ? GetOwner(definitionId, current) : GetNamed(current.AuthConfiguration);
                })
                : GetOwner(definitionId, settings);
            _consumers[definitionId] = provider;
            return provider;
        }

        public IBandcampSessionProvider GetForTest(int definitionId, BandcampDownloadSettings settings)
        {
            var provider = string.IsNullOrWhiteSpace(settings.AuthConfiguration)
                ? GetOwner(definitionId, settings) : GetNamed(settings.AuthConfiguration);
            _consumers[definitionId] = provider;
            return provider;
        }

        private BandcampSessionProvider GetOwner(int definitionId, BandcampDownloadSettings settings)
        {
            var provider = _providers.GetOrAdd(
                definitionId,
                id => new BandcampSessionProvider(id, settings, _apiClient, _logger, _clock, _loginClient, _sessionStore));
            provider.UpdateSettings(settings);
            return provider;
        }

        public IBandcampSessionProvider GetNamed(string name)
        {
            // Resolve from saved definitions on every operation, including queued execution.
            // A selector is an explicit account choice; never infer ownership from cookies.
            return new BandcampSharedSessionProvider(() =>
            {
                if (_repository == null)
                {
                    return null;
                }

                var owners = _repository.All().Where(d => d.Name == name &&
                    d.Implementation == nameof(BandcampDownloadClient) && d.Settings is BandcampDownloadSettings).ToList();
                if (owners.Count != 1)
                {
                    return null;
                }

                var definition = _repository.Find(owners[0].Id);
                if (definition?.Settings is not BandcampDownloadSettings settings ||
                    !string.IsNullOrWhiteSpace(settings.AuthConfiguration))
                {
                    return null;
                }

                return GetOwner(definition.Id, settings);
            });
        }

        public bool ClearLocalCredentials(int definitionId)
        {
            if (_sessionStore == null || _repository == null || definitionId <= 0) return false;
            _sessionStore.ClearLocalCredentials(definitionId);
            var saved = _repository.Find(definitionId);
            if (saved?.Settings is not BandcampDownloadSettings current) return false;
            if (_providers.TryGetValue(definitionId, out var provider)) provider.UpdateSettings(current);
            return true;
        }

        public IBandcampSessionProvider? Find(int definitionId)
        {
            return _consumers.TryGetValue(definitionId, out var provider) ? provider : null;
        }
    }
}
