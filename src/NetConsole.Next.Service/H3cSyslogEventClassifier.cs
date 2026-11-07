using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

internal sealed record SyslogEventClassification(string Family, string Type);

// Classify the explicit Comware message identifier only. Payload, sender identity and site are not inferred.
internal static class H3cSyslogEventClassifier
{
    private static readonly SyslogEventClassification Unknown = new("UNKNOWN", "UNKNOWN");
    private static readonly Regex Identifier = new(
        @"(?<![A-Za-z0-9_])(?:%%[0-9]+)?(?<family>[A-Z][A-Z0-9_]{0,63})/[0-9]+/(?<type>[A-Z][A-Z0-9_]{0,63})\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25));

    public static bool IsSupportedFilter(string? type) => type is null or "UNKNOWN" or "IFNET_PHY_UPDOWN" or
        "CFGMAN_CFGCHANGED" or "MESH_LINKUP" or "MESH_LINKDOWN" or "MESH_ACTIVELINK_SWITCH";

    public static SyslogEventClassification Classify(string message)
    {
        try
        {
            var match = Identifier.Match(message);
            // Multiple message identifiers are ambiguous, even when one is a supported event.
            if (!match.Success || match.NextMatch().Success) return Unknown;
            var family = match.Groups["family"].Value.ToUpperInvariant();
            var type = match.Groups["type"].Value.ToUpperInvariant();
            return (family, type) switch
            {
                ("IFNET", "PHY_UPDOWN") => new("IFNET", "IFNET_PHY_UPDOWN"),
                ("CFGMAN", "CFGMAN_CFGCHANGED") => new("CFGMAN", "CFGMAN_CFGCHANGED"),
                ("WMESH", "MESH_LINKUP" or "MESH_LINKDOWN" or "MESH_ACTIVELINK_SWITCH") => new("WMESH", type),
                _ => Unknown,
            };
        }
        catch (RegexMatchTimeoutException) { return Unknown; }
    }
}
