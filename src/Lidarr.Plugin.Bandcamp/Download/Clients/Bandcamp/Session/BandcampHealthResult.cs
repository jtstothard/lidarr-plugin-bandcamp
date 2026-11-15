using System;
using NzbDrone.Core.HealthCheck;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Adapts only the warning constructor changed in Lidarr 3.1.5.5066.
    /// Do not reference either removed members or the newer enum in emitted IL.
    /// </summary>
    internal static class BandcampHealthResult
    {
        public static HealthCheck.HealthCheck Warning(Type source, string message)
        {
            var healthType = typeof(HealthCheck.HealthCheck);
            var reasonType = healthType.Assembly.GetType("NzbDrone.Core.HealthCheck.HealthCheckReason");
            if (reasonType != null)
            {
                // Like Lidarr's warning for named unavailable clients (not all clients).
                var reason = Enum.Parse(reasonType, "DownloadClientStatusSingleClient");
                var constructor = healthType.GetConstructor(new[]
                {
                    typeof(Type), typeof(HealthCheckResult), reasonType, typeof(string), typeof(string)
                }) ?? throw new MissingMethodException(healthType.FullName, "reason-bearing health constructor");
                return (HealthCheck.HealthCheck)constructor.Invoke(new[]
                {
                    source, (object)HealthCheckResult.Warning, reason, message,
                    "#download-clients-are-unavailable-due-to-failures"
                });
            }

            var legacy = healthType.GetConstructor(new[]
            {
                typeof(Type), typeof(HealthCheckResult), typeof(string), typeof(string)
            }) ?? throw new MissingMethodException(healthType.FullName, "legacy health constructor");
            return (HealthCheck.HealthCheck)legacy.Invoke(new object[]
            {
                source, HealthCheckResult.Warning, message,
                "#download-clients-are-unavailable-due-to-failures"
            });
        }
    }
}
