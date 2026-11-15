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

        /// <summary>The session is positively signed out, or credential login needs user action.</summary>
        Invalid,

        /// <summary>The lookup or login failed; the typed problem identifies the failure without implying an outage.</summary>
        Error,

        /// <summary>The page does not prove a usable session or positive sign-out.</summary>
        ChallengeOrUnrecognized,

        /// <summary>Bandcamp requests must wait until the reported retry condition.</summary>
        RateLimited
    }

    /// <summary>
    /// Snapshot of a session provider's state. Exposed so a health check can report the
    /// cause and a validation cache can decide whether a fresh lookup is needed.
    /// </summary>
    public sealed class BandcampSessionState
    {
        public static readonly BandcampSessionState Initial = new(BandcampSessionStatus.Unknown, null);

        public BandcampSessionState(BandcampSessionStatus status, DateTime? checkedAtUtc, BandcampSessionProblem? problem = null,
            DateTime? lastSuccessfulValidationUtc = null, string effectiveAuthMode = "Not configured")
        {
            Problem = problem;
            Status = status;
            CheckedAtUtc = checkedAtUtc;
            LastSuccessfulValidationUtc = lastSuccessfulValidationUtc;
            EffectiveAuthMode = effectiveAuthMode;
        }

        public BandcampSessionStatus Status { get; }

        public DateTime? LastSuccessfulValidationUtc { get; }

        public string EffectiveAuthMode { get; }

        public string Description => (Problem?.Description ?? Status.ToString()) +
            $". Auth mode: {EffectiveAuthMode}. Last successful validation: " +
            (LastSuccessfulValidationUtc.HasValue ? $"{LastSuccessfulValidationUtc.Value:yyyy-MM-dd HH:mm:ss} UTC" : "never");

        /// <summary>Gets when the last live validation finished (UTC), or null if never.</summary>
        public DateTime? CheckedAtUtc { get; }

        /// <summary>Gets why the session is unusable, for health reporting; null when there is nothing for the user to fix.</summary>
        public BandcampSessionProblem? Problem { get; }
    }
}
