namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Why a Bandcamp session is unusable, as reported by the health check.
    /// A closed set of well-known instances compared by reference; add new causes
    /// (bad credentials, verification needed, captcha, ...) as further static members.
    /// </summary>
    public sealed class BandcampSessionProblem
    {
        public static readonly BandcampSessionProblem CookieExpired = new(
            "CookieExpired",
            "session cookie expired or invalid; paste a fresh identity cookie into the client settings");

        public static readonly BandcampSessionProblem BadCredentials = new(
            "BadCredentials",
            "Bandcamp rejected the email or password; login won't be retried until you change them in the client settings");

        public static readonly BandcampSessionProblem VerificationNeeded = new(
            "VerificationNeeded",
            "Bandcamp sent a verification code to your email; enter it in the client's Verification Code setting (login is otherwise retried at most every 6 hours)");

        public static readonly BandcampSessionProblem CaptchaRequired = new(
            "CaptchaRequired",
            "Bandcamp asked for a captcha, which can't be solved here; paste a session cookie from your browser into the client settings (login is retried at most every 6 hours)");

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
