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

    public sealed record BandcampValidationResult(BandcampValidationOutcome Outcome, long? FanId = null, string? RetryAfter = null);
}
