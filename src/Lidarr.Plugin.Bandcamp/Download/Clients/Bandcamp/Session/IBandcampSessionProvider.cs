using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Owns the fan session for one download client definition. Callers ask it for the
    /// session at the moment they need one; nothing else holds a cookie copy.
    ///
    /// Liveness is decided only by the live fan_id lookup; the cookie's own expiry field
    /// is ignored.
    /// </summary>
    public interface IBandcampSessionProvider
    {
        /// <summary>Gets the download client definition this provider belongs to.</summary>
        int DefinitionId { get; }

        /// <summary>Gets the result of the most recent validation.</summary>
        BandcampSessionState State { get; }

        /// <summary>Gets the outcome of the most recent login attempt, or null if none ran.</summary>
        BandcampLoginOutcome? LastLoginOutcome { get; }

        /// <summary>Gets a non-secret diagnostic from the most recent failed login.</summary>
        string? LastLoginMessage { get; }

        /// <summary>
        /// Returns a session usable right now, or null when none can be obtained.
        /// Used when a queued item starts running and before a grab.
        /// </summary>
        Task<BandcampSession?> GetSessionAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Always runs a live fan_id lookup against the current settings, bypassing any
        /// cache. Used by Test().
        /// </summary>
        Task<BandcampSession?> ValidateAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Called when a session that was handed out has stopped working. Returns a session
        /// usable right now (the same one if it still validates), or null when none can be
        /// obtained. Cookie mode re-validates the current settings; login mode obtains a
        /// fresh session here.
        /// </summary>
        Task<BandcampSession?> RenewAsync(CancellationToken cancellationToken = default);
    }
}
