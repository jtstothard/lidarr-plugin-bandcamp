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
