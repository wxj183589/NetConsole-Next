using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

internal static class H3cCommandPolicy
{
    public const string DisplayVersion = "display version";
    public const string DisplayInterfaceBrief = "display interface brief";

    public static bool IsAllowed(string command) => command is DisplayVersion or DisplayInterfaceBrief;
}

internal static class H3cHostKeyFingerprint
{
    public static string Normalize(string value)
    {
        var fingerprint = value?.Trim() ?? string.Empty;
        if (fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            fingerprint = fingerprint[7..];
        if (fingerprint.Length == 0 || fingerprint.Contains('=') || fingerprint.Any(char.IsWhiteSpace))
            throw new ArgumentException("Host key pin must be an SSH SHA256 fingerprint (unpadded base64).", nameof(value));

        var padded = fingerprint + new string('=', (4 - fingerprint.Length % 4) % 4);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(padded); }
        catch (FormatException ex) { throw new ArgumentException("Host key pin is not valid base64.", nameof(value), ex); }
        if (bytes.Length != 32) throw new ArgumentException("Host key pin must decode to exactly 32 bytes.", nameof(value));
        return "SHA256:" + Convert.ToBase64String(bytes).TrimEnd('=');
    }

    public static bool Matches(string configuredPin, string? presentedFingerprint)
    {
        if (string.IsNullOrWhiteSpace(presentedFingerprint)) return false;
        try
        {
            var configured = Convert.FromBase64String(Normalize(configuredPin)[7..] + "=");
            var presented = Convert.FromBase64String(Normalize(presentedFingerprint)[7..] + "=");
            return CryptographicOperations.FixedTimeEquals(configured, presented);
        }
        catch (ArgumentException) { return false; }
    }
}

internal static partial class H3cCredentialReference
{
    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string value) => Pattern().IsMatch(value ?? string.Empty);
}

internal sealed record H3cDeviceProfileSnapshot(
    string Id,
    string Host,
    int Port,
    string Username,
    string CredentialReference,
    string HostKeySha256,
    bool Enabled,
    long CreatedMs,
    long UpdatedMs)
{
    public string SourceId => "h3c-" + Id;
}

internal sealed record H3cVersionInfo(string Vendor, string Version, string? Release, bool Recognized);

internal sealed record H3cInterfaceStatus(
    string InterfaceName,
    string LinkStatus,
    string? ProtocolStatus,
    string? IpAddress,
    string? Speed,
    string? Duplex,
    string? PortMode,
    string? Vlan,
    string? Description);

internal sealed record H3cInterfaceBriefResult(
    IReadOnlyList<H3cInterfaceStatus> Interfaces,
    bool Recognized,
    string? Notice);

internal static partial class H3cComwareParser
{
    [GeneratedRegex(@"H3C Comware Software,\s*Version\s+([^,\s]+)(?:,\s*Release\s+([^\r\n]+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9/._:-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceNamePattern();

    public static H3cVersionInfo ParseVersion(string output)
    {
        var match = VersionPattern().Match(output ?? string.Empty);
        return match.Success
            ? new("H3C Comware", match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value.Trim() : null, true)
            : new("unknown", string.Empty, null, false);
    }

    public static H3cInterfaceBriefResult ParseInterfaceBrief(string output)
    {
        var interfaces = new List<H3cInterfaceStatus>();
        var section = BriefSection.None;
        foreach (var rawLine in (output ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Contains("brief information on interfaces in route mode", StringComparison.OrdinalIgnoreCase))
            {
                section = BriefSection.Route;
                continue;
            }
            if (line.Contains("brief information on interfaces in bridge mode", StringComparison.OrdinalIgnoreCase))
            {
                section = BriefSection.Bridge;
                continue;
            }
            if (section == BriefSection.None || line.StartsWith("-", StringComparison.Ordinal) ||
                line.StartsWith("Interface ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Link:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Speed or duplex:", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split((char[]?)null, section == BriefSection.Route ? 5 : 7,
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !InterfaceNamePattern().IsMatch(parts[0])) continue;
            if (section == BriefSection.Route)
            {
                interfaces.Add(new(parts[0], parts[1], At(parts, 2), At(parts, 3), null, null,
                    "route", null, At(parts, 4)));
            }
            else if (parts.Length >= 5)
            {
                interfaces.Add(new(parts[0], parts[1], null, null, At(parts, 2), At(parts, 3),
                    At(parts, 4), At(parts, 5), At(parts, 6)));
            }
        }

        return new(interfaces, interfaces.Count > 0,
            interfaces.Count == 0 ? "No recognized Comware interface brief rows were found." : null);
    }

    public static string ToParsedJson(string command, string output)
    {
        if (command == H3cCommandPolicy.DisplayVersion)
            return System.Text.Json.JsonSerializer.Serialize(ParseVersion(output));
        if (command == H3cCommandPolicy.DisplayInterfaceBrief)
            return System.Text.Json.JsonSerializer.Serialize(ParseInterfaceBrief(output));
        throw new ArgumentException("Only allowlisted H3C read-only commands have a parser.", nameof(command));
    }

    private static string? At(string[] parts, int index) =>
        index >= parts.Length || parts[index] == "--" ? null : parts[index];

    private enum BriefSection { None, Route, Bridge }
}
