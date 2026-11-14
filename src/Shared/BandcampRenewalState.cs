using System;

namespace Lidarr.Plugin.Bandcamp.Protocol
{
    // Explicit wire mapping keeps receipts compatible with previously shipped helpers/servers.
    internal enum BandcampRenewalState
    {
        Unrecognized,
        Pending,
        Validating,
        Committing,
        Committed,
        Cancelled,
        Refused,
        Unknown
    }

    internal static class BandcampRenewalStateWire
    {
        internal static string Serialize(BandcampRenewalState state) => state switch
        {
            BandcampRenewalState.Pending => "Pending",
            BandcampRenewalState.Validating => "Validating",
            BandcampRenewalState.Committing => "Committing",
            BandcampRenewalState.Committed => "Committed",
            BandcampRenewalState.Cancelled => "Cancelled",
            BandcampRenewalState.Refused => "Refused",
            BandcampRenewalState.Unknown => "Unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };

        internal static BandcampRenewalState Parse(string? value) => value switch
        {
            "Pending" => BandcampRenewalState.Pending,
            "Validating" => BandcampRenewalState.Validating,
            "Committing" => BandcampRenewalState.Committing,
            "Committed" => BandcampRenewalState.Committed,
            "Cancelled" => BandcampRenewalState.Cancelled,
            "Refused" => BandcampRenewalState.Refused,
            "Unknown" => BandcampRenewalState.Unknown,
            _ => BandcampRenewalState.Unrecognized
        };
    }
}
