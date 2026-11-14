using System;
using System.Globalization;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>Missing/malformed Retry-After waits five minutes; zero/past dates wait one second to avoid tight loops.</summary>
    internal static class BandcampRetryAfter
    {
        public static DateTime UntilUtc(string? value, DateTime now)
        {
            value = value?.Trim();
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                var maximum = (DateTime.MaxValue.Ticks - now.Ticks) / TimeSpan.TicksPerSecond;
                return now.AddSeconds(Math.Min(Math.Max(1, seconds), maximum));
            }

            if (DateTimeOffset.TryParseExact(value,
                new[] { "r", "dddd, dd-MMM-yy HH:mm:ss 'GMT'", "ddd MMM d HH:mm:ss yyyy" },
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var date))
            {
                return date.UtcDateTime > now ? date.UtcDateTime : now.AddSeconds(1);
            }

            return now.AddMinutes(5);
        }
    }
}
