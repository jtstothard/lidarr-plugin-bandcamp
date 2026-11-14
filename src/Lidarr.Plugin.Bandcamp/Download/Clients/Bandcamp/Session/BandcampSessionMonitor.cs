using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>Published when a background check changes a Bandcamp session's health problem.</summary>
    public class BandcampSessionStateChangedEvent : IEvent
    {
        public BandcampSessionStateChangedEvent(int definitionId)
        {
            DefinitionId = definitionId;
        }

        public int DefinitionId { get; }
    }

    /// <summary>
    /// Hourly background check: revalidates every enabled Bandcamp client's session and asks
    /// for renewal when it is dead, so the health check stays current between grabs.
    /// A timer ticks every few minutes; <see cref="RunDueChecksAsync"/> decides what is due
    /// using the injectable clock.
    /// </summary>
    public class BandcampSessionMonitor : IHandle<ApplicationStartedEvent>, IHandle<ApplicationShutdownRequested>
    {
        public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

        private readonly IDownloadClientFactory _downloadClientFactory;
        private readonly IBandcampSessionProviderRegistry _sessionProviders;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly Func<DateTime> _clock;
        private readonly SemaphoreSlim _running = new(1, 1);
        private Timer? _timer;

        public BandcampSessionMonitor(
            IDownloadClientFactory downloadClientFactory,
            IBandcampSessionProviderRegistry sessionProviders,
            IEventAggregator eventAggregator,
            Logger logger)
            : this(downloadClientFactory, sessionProviders, eventAggregator, logger, null)
        {
        }

        public BandcampSessionMonitor(
            IDownloadClientFactory downloadClientFactory,
            IBandcampSessionProviderRegistry sessionProviders,
            IEventAggregator eventAggregator,
            Logger logger,
            Func<DateTime>? clock)
        {
            _downloadClientFactory = downloadClientFactory;
            _sessionProviders = sessionProviders;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _timer ??= new Timer(_ => _ = RunDueChecksSafeAsync(), null, TimeSpan.Zero, TickInterval);
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            _timer?.Dispose();
            _timer = null;
        }

        /// <summary>Revalidates each enabled client's session whose last check is an hour or more old.</summary>
        public async Task RunDueChecksAsync(CancellationToken cancellationToken = default)
        {
            if (!await _running.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                var definitions = _downloadClientFactory.All()
                    .Where(d => d.Enable && d.Implementation == nameof(BandcampDownloadClient) && d.Settings is BandcampDownloadSettings)
                    .ToList();

                foreach (var definition in definitions)
                {
                    var provider = _sessionProviders.Get(definition.Id, (BandcampDownloadSettings)definition.Settings);
                    var before = provider.State;
                    if (before.CheckedAtUtc.HasValue && _clock() - before.CheckedAtUtc.Value < CheckInterval)
                    {
                        continue;
                    }

                    try
                    {
                        var session = await provider.ValidateAsync(cancellationToken).ConfigureAwait(false);
                        if (session == null)
                        {
                            await provider.RenewAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warn(ex, "Bandcamp session [{0}]: background check failed", definition.Id);
                    }

                    if (!ReferenceEquals(before.Problem, provider.State.Problem))
                    {
                        _eventAggregator.PublishEvent(new BandcampSessionStateChangedEvent(definition.Id));
                    }
                }
            }
            finally
            {
                _running.Release();
            }
        }

        private async Task RunDueChecksSafeAsync()
        {
            try
            {
                await RunDueChecksAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Bandcamp session background check crashed");
            }
        }
    }
}
