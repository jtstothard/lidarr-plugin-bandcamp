using System;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Outcome of the most recent live session validation.
    /// </summary>
    public enum BandcampSessionStatus
    {
        /// <summary>No validation has run yet.</summary>
        Unknown,

        /// <summary>The fan_id lookup succeeded.</summary>
        Valid,

        /// <summary>No session is configured.</summary>
        Missing,

        /// <summary>The fan_id lookup returned no fan (cookie expired or invalid).</summary>
        Invalid,

        /// <summary>The fan_id lookup failed with an error (network, parse, etc.).</summary>
        Error
    }

    /// <summary>
    /// Snapshot of a session provider's state. Exposed so a health check can report the
    /// cause and a validation cache can decide whether a fresh lookup is needed.
    /// </summary>
    public sealed class BandcampSessionState
    {
        public static readonly BandcampSessionState Initial = new(BandcampSessionStatus.Unknown, null);

        public BandcampSessionState(BandcampSessionStatus status, DateTime? checkedAtUtc)
        {
            Status = status;
            CheckedAtUtc = checkedAtUtc;
        }

        public BandcampSessionStatus Status { get; }

        /// <summary>Gets when the last live validation finished (UTC), or null if never.</summary>
        public DateTime? CheckedAtUtc { get; }
    }
}
