using System;
using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>A reference to a named owner, never a second session or login engine.</summary>
    internal sealed class BandcampSharedSessionProvider : IBandcampSessionProvider
    {
        private readonly Func<IBandcampSessionProvider?> _resolve;
        private BandcampSessionProblem? _problem;

        public BandcampSharedSessionProvider(Func<IBandcampSessionProvider?> resolve)
        {
            _resolve = resolve;
        }

        private IBandcampSessionProvider? Current
        {
            get
            {
                try
                {
                    var owner = _resolve();
                    _problem = owner == null ? BandcampSessionProblem.ConfigurationMissing : null;
                    return owner;
                }
                catch (Exception)
                {
                    _problem = BandcampSessionProblem.StorageFailure;
                    return null;
                }
            }
        }

        public int DefinitionId => Current?.DefinitionId ?? 0;

        public BandcampSessionState State
        {
            get
            {
                var state = Current?.State;
                return state == null
                    ? new BandcampSessionState(BandcampSessionStatus.Error, null, _problem)
                    : new BandcampSessionState(state.Status, state.CheckedAtUtc, state.Problem,
                        state.LastSuccessfulValidationUtc, "Shared named configuration; " + state.EffectiveAuthMode);
            }
        }

        public BandcampLoginOutcome? LastLoginOutcome => Current?.LastLoginOutcome;
        public string? LastLoginMessage => Current?.LastLoginMessage;

        public Task<BandcampSession?> GetSessionAsync(CancellationToken cancellationToken = default) =>
            Current?.GetSessionAsync(cancellationToken) ?? Task.FromResult<BandcampSession?>(null);

        public Task<BandcampSession?> RenewAsync(CancellationToken cancellationToken = default) =>
            Current?.RenewAsync(cancellationToken) ?? Task.FromResult<BandcampSession?>(null);

        public Task<BandcampSession?> ValidateAsync(CancellationToken cancellationToken = default) =>
            Current?.ValidateAsync(cancellationToken) ?? Task.FromResult<BandcampSession?>(null);

        public Task<BandcampSession?> ReplaceIdentityAsync(string cookies, CancellationToken cancellationToken = default) =>
            Current?.ReplaceIdentityAsync(cookies, cancellationToken) ?? Task.FromResult<BandcampSession?>(null);
    }
}
