namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// A validated Bandcamp fan session: the identity cookie that authenticates requests,
    /// and the fan_id that the live fan_id lookup resolved for it.
    /// </summary>
    public sealed class BandcampSession
    {
        public BandcampSession(string cookies, long fanId)
        {
            Cookies = cookies;
            FanId = fanId;
        }

        /// <summary>Gets the cookie string sent with Bandcamp requests. Never log this.</summary>
        public string Cookies { get; }

        public long FanId { get; }
    }
}
