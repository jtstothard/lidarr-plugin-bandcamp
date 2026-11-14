namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Why a Bandcamp session is unusable, as reported by the health check.
    /// A closed set of well-known instances compared by reference.
    /// </summary>
    public sealed class BandcampSessionProblem
    {
        private static readonly string CooldownText = $"{BandcampSessionProvider.ChallengeCooldown.TotalHours:0} hours";

        public static readonly BandcampSessionProblem CookieExpired = new(
            "CookieExpired",
            "Bandcamp positively signed the session out; renew in your browser and paste a fresh identity cookie, or configure complete email and password credentials");

        public static readonly BandcampSessionProblem BadCredentials = new(
            "BadCredentials",
            "Bandcamp rejected the email or password; login won't be retried until you change them in the client settings");

        public static readonly BandcampSessionProblem VerificationNeeded = new(
            "VerificationNeeded",
            "Bandcamp sent a verification code to your email; enter it in the client's Verification Code setting (login is otherwise retried at most every " + CooldownText + ")");

        public static readonly BandcampSessionProblem CaptchaRequired = new(
            "CaptchaRequired",
            "Bandcamp asked for a captcha, which can't be solved here; paste a session cookie from your browser into the client settings (login is retried at most every " + CooldownText + ")");

        public static readonly BandcampSessionProblem ReplacementConflict = new(
            "ReplacementConflict",
            "Bandcamp settings changed during recovery; the saved identity was retained; retry using the current settings");

        public static readonly BandcampSessionProblem StorageFailure = new(
            "StorageFailure",
            "could not save Bandcamp session settings; check Lidarr database storage and permissions, then retry recovery");

        public static readonly BandcampSessionProblem Missing = new(
            "Missing", "no session configured; enter complete email and password credentials or paste a browser identity cookie");

        public static readonly BandcampSessionProblem ChallengeOrUnrecognized = new(
            "ChallengeOrUnrecognized", "Bandcamp returned a challenge or unrecognized page; check Bandcamp in your browser and renew manually if required; the saved identity has been retained");

        public static readonly BandcampSessionProblem Unavailable = new(
            "Unavailable", "Bandcamp is temporarily unavailable; check network and service availability and try later; the saved identity has been retained");

        public static BandcampSessionProblem RateLimited(System.DateTime untilUtc) => new(
            "RateLimited", $"Bandcamp rate limited requests; wait until {untilUtc:yyyy-MM-dd HH:mm:ss} UTC before trying again; do not change credentials");

        private BandcampSessionProblem(string code, string description)
        {
            Code = code;
            Description = description;
        }

        /// <summary>Gets a stable identifier for the cause.</summary>
        public string Code { get; }

        /// <summary>Gets the user-facing explanation, including what to do about it.</summary>
        public string Description { get; }

        public override string ToString() => Code;
    }
}
