namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    public enum BandcampValidationOutcome
    {
        Valid,
        SignedOut,
        ChallengeOrUnrecognized,
        RateLimited,
        Unavailable
    }

    public sealed record BandcampValidationResult(BandcampValidationOutcome Outcome, long? FanId = null, string? RetryAfter = null)
    {
        // Response data stays internal and is never included in generated record diagnostics.
        internal string? IdentityCandidate { get; init; }

        public override string ToString() => $"Bandcamp validation: {Outcome}";
    }
}
