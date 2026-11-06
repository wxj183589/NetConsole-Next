using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

internal sealed record H3cVlanFact(int VlanId, string? Name, string? Description, string? Type,
    bool? RouteInterface, string? RouteInterfaceDetail, IReadOnlyList<string> TaggedPorts,
    IReadOnlyList<string> UntaggedPorts, string? Ipv4Address, string? Ipv4SubnetMask,
    string? PrivateVlanType, IReadOnlyList<string> Ipv6Addresses);

internal sealed record H3cVlanTable(string Command, string Status, IReadOnlyList<H3cVlanFact> Vlans, string? Notice);

internal static partial class H3cComwareParser
{
    [GeneratedRegex(@"^VLAN\s+ID\s*:\s*(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VlanIdLine();

    [GeneratedRegex(@"^(?<key>VLAN type|Route interface|Description|Name|Tagged ports|Untagged ports|IPv4 address|IPv4 subnet mask|Private VLAN type|IPv6 global unicast addresses)\s*:\s*(?<value>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VlanFieldLine();

    // Preserve range expressions rather than guessing or expanding interfaces.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9.-]*\d+(?:/\d+)*(?:\.\d+)?(?:\s+to\s+[A-Za-z][A-Za-z0-9.-]*\d+(?:/\d+)*(?:\.\d+)?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VlanPortName();

    [GeneratedRegex(@"^(?<address>[0-9a-f:]+),\s+subnet\s+is\s+(?<network>[0-9a-f:]+)/(?<prefix>\d{1,3})(?:\s+\[[A-Za-z][A-Za-z -]*\])?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VlanIpv6Line();

    public static H3cVlanTable ParseVlans(string output, bool completedExec = false)
    {
        const string command = H3cCommandPolicy.DisplayVlanAll;
        var transcript = CliTranscriptParser.Parse(output, command, completedExec);
        H3cVlanTable Failure(string status, string? notice) => new(command, status, [], notice);
        if (CliTranscriptParser.HasCommandBoundaryMismatch(output, command))
            return Failure("PARTIAL", "Command output contains a mismatched command boundary.");
        if (transcript.Status is CliTranscriptStatus.PermissionDenied or CliTranscriptStatus.Unsupported)
            return Failure("UNRECOGNIZED", transcript.Notice);
        if (transcript.Status == CliTranscriptStatus.Paged ||
            transcript.Status == CliTranscriptStatus.Incomplete && !(completedExec && transcript.Prompt.Length == 0))
            return Failure("PARTIAL", transcript.Notice);
        if (transcript.Status == CliTranscriptStatus.Empty)
            return Failure("EMPTY", transcript.Notice);

        var vlans = new List<H3cVlanFact>();
        var ids = new HashSet<int>();
        Dictionary<string, string>? fields = null;
        int vlanId = 0;
        string? continuationSection = null;
        var invalid = false;

        void FinishBlock()
        {
            if (fields is null) return;
            string[] required = ["Name", "Description", "VLAN type", "Route interface", "Tagged ports", "Untagged ports"];
            if (required.Any(key => !fields.ContainsKey(key)) || !ids.Add(vlanId)) { invalid = true; return; }
            var tagged = ParsePorts(fields["Tagged ports"]);
            var untagged = ParsePorts(fields["Untagged ports"]);
            var ipv6 = fields.TryGetValue("IPv6 global unicast addresses", out var ipv6Text)
                ? ParseVlanIpv6(ipv6Text) : [];
            if (tagged is null || untagged is null || ipv6 is null || fields["VLAN type"].Length == 0 ||
                fields["Route interface"].Length == 0) { invalid = true; return; }
            var route = fields["Route interface"];
            bool? configured = route.Equals("Not configured", StringComparison.OrdinalIgnoreCase) ? false
                : route.Equals("Configured", StringComparison.OrdinalIgnoreCase) ? true : null;
            vlans.Add(new(vlanId, fields["Name"], fields["Description"], fields["VLAN type"],
                configured, route, tagged, untagged, fields.GetValueOrDefault("IPv4 address"),
                fields.GetValueOrDefault("IPv4 subnet mask"), fields.GetValueOrDefault("Private VLAN type"),
                ipv6));
        }

        foreach (var rawLine in transcript.Output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var idMatch = VlanIdLine().Match(line);
            if (idMatch.Success)
            {
                FinishBlock();
                if (!int.TryParse(idMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out vlanId) ||
                    vlanId is < 1 or > 4094)
                    return Failure("PARTIAL", "Invalid VLAN identifier in command output.");
                fields = new(StringComparer.OrdinalIgnoreCase);
                continuationSection = null;
                continue;
            }
            if (fields is null) return Failure("UNRECOGNIZED", "No recognized Comware VLAN detail blocks.");
            var field = VlanFieldLine().Match(line);
            if (field.Success)
            {
                var key = field.Groups["key"].Value;
                if (!fields.TryAdd(key, field.Groups["value"].Value.Trim())) invalid = true;
                continuationSection = key.Equals("Tagged ports", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Untagged ports", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("IPv6 global unicast addresses", StringComparison.OrdinalIgnoreCase) ? key : null;
            }
            else if (continuationSection is not null && char.IsWhiteSpace(rawLine[0]))
                fields[continuationSection] += continuationSection.StartsWith("IPv6", StringComparison.OrdinalIgnoreCase)
                    ? "\n" + line : " " + line;
            else invalid = true;
        }
        FinishBlock();
        if (invalid) return Failure("PARTIAL", "Incomplete, duplicate or unrecognized VLAN detail fields.");
        if (vlans.Count == 0) return Failure("UNRECOGNIZED", "No recognized Comware VLAN detail blocks.");
        return new(command, "PARSED", vlans.OrderBy(row => row.VlanId).ToArray(), null);
    }

    private static IReadOnlyList<string>? ParseVlanIpv6(string value)
    {
        if (value.Equals("None", StringComparison.OrdinalIgnoreCase)) return [];
        var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return null;
        foreach (var line in lines)
        {
            var match = VlanIpv6Line().Match(line);
            if (!match.Success ||
                !IPAddress.TryParse(match.Groups["address"].Value, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6 ||
                !IPAddress.TryParse(match.Groups["network"].Value, out var network) || network.AddressFamily != AddressFamily.InterNetworkV6 ||
                !int.TryParse(match.Groups["prefix"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix > 128)
                return null;
        }
        return lines;
    }

    private static IReadOnlyList<string>? ParsePorts(string value)
    {
        value = value.Trim();
        if (value.Equals("None", StringComparison.OrdinalIgnoreCase)) return [];
        if (value.Length == 0) return null; // An empty section is not proof of no members.
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var ports = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < tokens.Length; index++)
        {
            var port = tokens[index];
            if (index + 2 < tokens.Length && tokens[index + 1].Equals("to", StringComparison.OrdinalIgnoreCase))
            {
                port += " to " + tokens[index + 2];
                index += 2;
            }
            if (!VlanPortName().IsMatch(port) || !seen.Add(port)) return null;
            ports.Add(port);
        }
        return ports;
    }
}
