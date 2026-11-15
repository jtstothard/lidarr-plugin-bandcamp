using System.Linq;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Warns when an enabled Bandcamp client's last session validation found a problem,
    /// naming the client and the cause. Clears itself once a validation succeeds.
    /// </summary>
    [CheckOn(typeof(ProviderUpdatedEvent<IDownloadClient>))]
    [CheckOn(typeof(ProviderDeletedEvent<IDownloadClient>))]
    [CheckOn(typeof(ProviderStatusChangedEvent<IDownloadClient>))]
    [CheckOn(typeof(BandcampSessionStateChangedEvent))]
    public class BandcampSessionHealthCheck : HealthCheckBase
    {
        private readonly IDownloadClientFactory _downloadClientFactory;
        private readonly IBandcampSessionProviderRegistry _sessionProviders;

        public BandcampSessionHealthCheck(
            IDownloadClientFactory downloadClientFactory,
            IBandcampSessionProviderRegistry sessionProviders,
            ILocalizationService localizationService)
            : base(localizationService)
        {
            _downloadClientFactory = downloadClientFactory;
            _sessionProviders = sessionProviders;
        }

        public override HealthCheck.HealthCheck Check()
        {
            var problems = _downloadClientFactory.All()
                .Where(d => d.Enable && d.Implementation == nameof(BandcampDownloadClient))
                .Select(d => new { d.Name, State = _sessionProviders.Find(d.Id)?.State })
                .Where(p => p.State?.Problem != null)
                .Select(p => $"{p.Name}: {p.State!.Description}")
                .ToList();

            if (problems.Count == 0)
            {
                return new HealthCheck.HealthCheck(GetType());
            }

            return BandcampHealthResult.Warning(
                GetType(),
                "Bandcamp session needs attention. " + string.Join("; ", problems));
        }
    }
}
