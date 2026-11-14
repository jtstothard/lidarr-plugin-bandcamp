using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Http.Bandcamp
{
    /// <summary>Conservative identity extraction; never merges response cookies into a header.</summary>
    internal static class BandcampIdentityCookie
    {
        private static readonly Regex Value = new(@"\A[\x21\x23-\x2B\x2D-\x3A\x3C-\x5B\x5D-\x7E]+\z", RegexOptions.CultureInvariant);

        internal static bool IsAccountOrigin(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.Host == "bandcamp.com" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

        internal static string? Identity(string header)
        {
            var identities = header.Split(';').Select(p => p.Trim()).Where(p => p.StartsWith("identity=", StringComparison.Ordinal)).ToArray();
            return identities.Length == 1 && Value.IsMatch(identities[0].Substring(9)) ? identities[0] : null;
        }

        internal static string? Candidate(string[]? headers)
        {
            string? candidate = null;
            foreach (var header in headers ?? Array.Empty<string>())
            {
                if (header.Length > 4096 || header.Any(c => c < 0x20 || c == 0x7f)) return null;
                var parts = header.Split(';').Select(p => p.Trim()).ToArray();
                if (!parts[0].StartsWith("identity=", StringComparison.Ordinal)) continue;
                if (candidate != null || !Value.IsMatch(parts[0].Substring(9))) return null;
                var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in parts.Skip(1))
                {
                    var pair = part.Split('=', 2);
                    var flag = pair[0].Equals("Secure", StringComparison.OrdinalIgnoreCase) || pair[0].Equals("HttpOnly", StringComparison.OrdinalIgnoreCase);
                    if ((flag && pair.Length != 1) || (!flag && pair.Length != 2) ||
                        !attributes.TryAdd(pair[0], pair.Length == 2 ? pair[1] : string.Empty)) return null;
                }

                if (!attributes.TryGetValue("Secure", out var secure) || secure != string.Empty ||
                    !attributes.TryGetValue("Path", out var path) || path != "/" ||
                    (attributes.TryGetValue("Domain", out var domain) && domain.ToLowerInvariant() != "bandcamp.com" && domain.ToLowerInvariant() != ".bandcamp.com")) return null;
                foreach (var attribute in attributes)
                {
                    switch (attribute.Key.ToLowerInvariant())
                    {
                        case "secure":
                        case "path":
                        case "domain":
                            break;
                        case "httponly":
                            if (attribute.Value != string.Empty) return null;
                            break;
                        case "samesite":
                            if (!new[] { "lax", "strict", "none" }.Contains(attribute.Value.ToLowerInvariant())) return null;
                            break;
                        case "max-age":
                            if (!long.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0) return null;
                            break;
                        case "expires":
                            if (!DateTimeOffset.TryParseExact(attribute.Value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry) || expiry <= DateTimeOffset.UtcNow) return null;
                            break;
                        default:
                            return null;
                    }
                }

                candidate = parts[0];
            }

            return candidate;
        }
    }
}
