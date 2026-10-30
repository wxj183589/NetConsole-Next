using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

internal enum CliTranscriptStatus
{
    Complete,
    Empty,
    Paged,
    Unsupported,
    PermissionDenied,
    Incomplete
}

internal sealed record CliCommandTranscript(
    string Command,
    string Prompt,
    string Output,
    CliTranscriptStatus Status,
    int PageMarkerCount,
    string? Notice)
{
    public bool IsComplete => Status == CliTranscriptStatus.Complete;
}

internal static partial class CliTranscriptParser
{
    [GeneratedRegex(@"^\s*(?<prompt>(?:<[^>\r\n]{1,128}>|[A-Za-z0-9_.:/()\-]{1,128}(?:\(config[^)]*\))?[>#]))\s*(?<command>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PromptLinePattern();

    [GeneratedRegex(@"(?i)(?:-{2,}\s*more\s*-{2,}|--+more--+|press\s+any\s+key\s+to\s+continue|press\s+space\s+to\s+continue)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PagerPattern();

    [GeneratedRegex(@"(?i)(?:invalid\s+command|unrecognized\s+command|incomplete\s+command|ambiguous\s+command|%\s*invalid\s+input)",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnsupportedPattern();

    [GeneratedRegex(@"(?i)(?:permission\s+denied|insufficient\s+privilege|not\s+authorized|authorization\s+failed)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PermissionPattern();

    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiPattern();

    public static CliCommandTranscript Parse(string? raw, string command, bool completedExec = false)
    {
        var normalized = Normalize(raw);
        if (normalized.Length == 0)
            return new(command, string.Empty, string.Empty, CliTranscriptStatus.Empty, 0, "empty output");

        var output = new List<string>();
        var prompt = string.Empty;
        var pages = 0;
        var sawCommandEcho = false;
        var bodyStarted = false;
        var sawClosingPrompt = false;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var promptMatch = PromptLinePattern().Match(line);
            if (promptMatch.Success)
            {
                prompt = promptMatch.Groups["prompt"].Value;
                var echoed = promptMatch.Groups["command"].Value.Trim();
                if (echoed.Length == 0)
                {
                    if (sawCommandEcho || output.Count > 0) sawClosingPrompt = true;
                    continue;
                }
                if (!bodyStarted && string.Equals(echoed, command, StringComparison.OrdinalIgnoreCase))
                {
                    sawCommandEcho = true;
                    continue;
                }
            }

            if (PagerPattern().IsMatch(trimmed))
            {
                pages++;
                continue;
            }

            // Some SSH/Telnet servers echo the command on a separate line. Only
            // remove that preamble echo; an identical line in the body is data.
            if (!bodyStarted && string.Equals(trimmed, command, StringComparison.OrdinalIgnoreCase))
            {
                sawCommandEcho = true;
                continue;
            }
            bodyStarted = true;
            output.Add(line);
        }

        var body = string.Join('\n', output).Trim('\n', '\r');
        if (PermissionPattern().IsMatch(body))
            return new(command, prompt, body, CliTranscriptStatus.PermissionDenied, pages, "permission denied");
        if (UnsupportedPattern().IsMatch(body))
            return new(command, prompt, body, CliTranscriptStatus.Unsupported, pages, "unsupported command");
        if (pages > 0)
            return new(command, prompt, body, CliTranscriptStatus.Paged, pages, "paging marker remains in transcript");
        if (!sawClosingPrompt && completedExec && sawCommandEcho && prompt.Length > 0)
            return new(command, prompt, body, body.Length == 0 ? CliTranscriptStatus.Empty : CliTranscriptStatus.Complete, pages, null);
        if (!sawClosingPrompt)
            return new(command, prompt, body, CliTranscriptStatus.Incomplete, pages, "closing terminal prompt was not observed");
        return new(command, prompt, body, body.Length == 0 ? CliTranscriptStatus.Empty : CliTranscriptStatus.Complete, pages, null);
    }

    public static bool HasCommandBoundaryMismatch(string? raw, string command)
    {
        var normalized = Normalize(raw);
        if (normalized.Length == 0) return false;
        var sawPrompt = false;
        var sawPromptCommand = false;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = PromptLinePattern().Match(line);
            if (!match.Success) continue;
            var echoed = match.Groups["command"].Value.Trim();
            if (echoed.Length == 0)
            {
                sawPrompt = true;
                continue;
            }
            if (!sawPrompt)
            {
                sawPrompt = true;
                sawPromptCommand = true;
                if (!string.Equals(echoed, command, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (sawPromptCommand || echoed.Length > 0) return true;
        }
        return false;
    }

    public static string Normalize(string? raw)
    {
        var value = AnsiPattern().Replace((raw ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n'), string.Empty);
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character == '\b')
            {
                if (builder.Length > 0) builder.Length--;
            }
            else if (character != '\0' && character != '\x1b') builder.Append(character);
        }
        return builder.ToString();
    }
}

internal sealed record H3cApFact(string Name, int? Id, string? State, string? Model, string? Serial,
    string? Group, string? OnlineTime);

internal sealed record H3cApAddressFact(string Name, string? IpAddress, string? MacAddress);

internal sealed record H3cRadioFact(string ApName, int? RadioId, int? Channel, string? Bandwidth, decimal? TxPower);

internal sealed record H3cMeshLinkFact(string? PeerName, string? PeerMac, int? Rssi, string? Bssid,
    string? InterfaceName, string? LinkState, string? OnlineTime);

internal sealed record H3cLldpFact(string? LocalInterface, string? NeighborSysname, string? NeighborInterface,
    string? ManagementAddress, string? ChassisId = null);

internal sealed record H3cTransceiverFact(string? InterfaceName, decimal? Temperature, decimal? Voltage,
    decimal? BiasCurrent, decimal? RxPower, decimal? TxPower, string? Status);

internal static partial class H3cComwareParser
{
    public static string ToParsedJsonWithTranscript(string command, string output, bool completedExec = false)
    {
        var transcript = CliTranscriptParser.Parse(output, command, completedExec);
        if (transcript.Status is CliTranscriptStatus.Unsupported or CliTranscriptStatus.PermissionDenied or CliTranscriptStatus.Paged)
            return JsonSerializer.Serialize(new { status = transcript.Status.ToString().ToUpperInvariant(), notice = transcript.Notice });
        return ToParsedJson(command, transcript.Output);
    }

    public static IReadOnlyList<H3cApFact> ParseWlanApAll(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, H3cCommandPolicy.DisplayWlanApAll);
        var rows = new List<H3cApFact>();
        foreach (var line in transcript.Output.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            rows.Add(new(parts[0], id, parts[2], parts[3], parts[4], parts[5].Split(' ', 2)[0], parts[5].Contains(' ') ? parts[5][(parts[5].IndexOf(' ') + 1)..] : null));
        }
        return rows;
    }

    public static IReadOnlyList<H3cApAddressFact> ParseWlanApAllAddress(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, H3cCommandPolicy.DisplayWlanApAllAddress);
        return transcript.Output.Split('\n').Select(line => line.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 3 && parts[1].Contains('.') && parts[2].Length > 0)
            .Select(parts => new H3cApAddressFact(parts[0], parts[1], parts[2])).ToArray();
    }

    public static IReadOnlyList<H3cRadioFact> ParseWlanApAllRadio(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, H3cCommandPolicy.DisplayWlanApAllRadio);
        var rows = new List<H3cRadioFact>();
        foreach (var line in transcript.Output.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var radioId) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)) continue;
            decimal? tx = decimal.TryParse(parts[4], NumberStyles.Number, CultureInfo.InvariantCulture, out var txValue) ? txValue : null;
            rows.Add(new(parts[0], radioId, channel, parts[3], tx));
        }
        return rows;
    }

    public static IReadOnlyList<H3cMeshLinkFact> ParseMeshLink(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, H3cCommandPolicy.DisplayWlanMeshLink);
        var lines = transcript.Output.Split('\n');
        var header = Array.FindIndex(lines, line => line.Contains("Peer Name", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("Link state", StringComparison.OrdinalIgnoreCase));
        if (header >= 0) lines = lines[(header + 1)..];
        var rows = new List<H3cMeshLinkFact>();
        foreach (var line in lines)
        {
            var parts = line.Trim().Split((char[]?)null, 7, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 7 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rssi)) continue;
            rows.Add(new(parts[0], parts[1], rssi, parts[3], parts[4], parts[5], parts[6]));
        }
        return rows;
    }

    public static IReadOnlyList<H3cInterfaceDetailFact> ParseInterfaceDetails(string output)
    {
        var lines = LooseCommandBody(output, H3cCommandPolicy.DisplayInterface);
        var rows = new List<H3cInterfaceDetailFact>();
        string? name = null, state = null, description = null, mode = null;
        int? channel = null;
        long? inputBytes = null, outputBytes = null;

        void Flush()
        {
            if (name is not null)
                rows.Add(new(name, state, description, mode, channel, inputBytes, outputBytes));
            name = state = description = mode = null;
            channel = null;
            inputBytes = outputBytes = null;
        }

        foreach (var rawLine in lines)
        {
            var text = rawLine.Trim();
            if (text.Length == 0) continue;
            if (IsInterfaceToken(text) && !text.Contains(' '))
            {
                Flush();
                name = text;
                continue;
            }
            if (name is null) continue;
            if (text.StartsWith("Current state:", StringComparison.OrdinalIgnoreCase))
                state = text["Current state:".Length..].Trim();
            else if (text.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                description = text["Description:".Length..].Trim();
            else if (text.StartsWith("current mode:", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(text, @"current mode:\s*(?<mode>[^,]+),\s*current channel:\s*(?<channel>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    mode = match.Groups["mode"].Value.Trim();
                    if (int.TryParse(match.Groups["channel"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        channel = parsed;
                }
            }
            else if (text.StartsWith("Input (total):", StringComparison.OrdinalIgnoreCase))
                inputBytes = ParseBytes(text);
            else if (text.StartsWith("Output (total):", StringComparison.OrdinalIgnoreCase))
                outputBytes = ParseBytes(text);
        }
        Flush();
        return rows;

        static long? ParseBytes(string text)
        {
            var match = Regex.Match(text, @"(?<packets>[\d,]+)\s+packets,\s*(?<bytes>[\d,]+)\s+bytes", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success && long.TryParse(match.Groups["bytes"].Value.Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
    }

    public static H3cMeshProfileFact ParseMeshProfile(string output)
    {
        var text = string.Join('\n', LooseCommandBody(output, H3cCommandPolicy.DisplayWlanMeshProfile));
        return new(
            ParseIntField(text, "Mesh profile number"),
            ParseTextField(text, "Mesh ID"),
            ParseTextField(text, "Mesh profile status"),
            ParseTextField(text, "Mesh forwarding-location"),
            ParseTextField(text, "AKM mode"));

        static int? ParseIntField(string value, string key)
        {
            var parsed = ParseTextField(value, key);
            return int.TryParse(parsed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
        }

        static string? ParseTextField(string value, string key)
        {
            var match = Regex.Match(value, $@"(?im)^\s*{Regex.Escape(key)}\s*:\s*(?<value>[^\r\n]+)");
            return match.Success ? match.Groups["value"].Value.Trim() : null;
        }
    }

    public static IReadOnlyList<H3cLldpFact> ParseLldpList(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, H3cCommandPolicy.DisplayLldpNeighborInformationList);
        var lines = transcript.Output.Split('\n');
        var systemNameHeader = Array.FindIndex(lines, line => line.Trim().StartsWith("System Name", StringComparison.OrdinalIgnoreCase));
        var systemNameFirst = systemNameHeader >= 0;
        if (systemNameHeader >= 0) lines = lines[(systemNameHeader + 1)..];
        return lines.Where(line => !line.Trim().StartsWith("Local Interface", StringComparison.OrdinalIgnoreCase) &&
                !line.Trim().StartsWith("System Name", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim().Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 4 && IsInterfaceToken(systemNameFirst ? parts[1] : parts[0]))
            .Select(parts => systemNameFirst
                ? new H3cLldpFact(parts[1], parts[0], parts[3], null, parts[2])
                : new H3cLldpFact(parts[0], parts[1], parts[2], parts[3])).ToArray();
    }

    public static IReadOnlyList<H3cLldpFact> ParseLldpVerbose(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, H3cCommandPolicy.DisplayLldpNeighborInformationVerbose);
        var rows = new List<H3cLldpFact>();
        string? local = null, neighbor = null, port = null, address = null, chassis = null;
        void Flush() { if (local is not null) rows.Add(new(local, neighbor, port, address, chassis)); local = neighbor = port = address = chassis = null; }
        foreach (var line in transcript.Output.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("LLDP neighbor-information of port ", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                var bracketStart = text.IndexOf('[', StringComparison.Ordinal);
                var bracketEnd = text.IndexOf(']', bracketStart + 1);
                local = bracketStart >= 0 && bracketEnd > bracketStart
                    ? text[(bracketStart + 1)..bracketEnd].Trim()
                    : text[34..].Trim();
                continue;
            }
            var colon = text.IndexOf(':');
            if (colon < 0) continue;
            var key = text[..colon].Trim(); var value = text[(colon + 1)..].Trim();
            if (key.Equals("Chassis ID", StringComparison.OrdinalIgnoreCase)) chassis = value;
            else if (key.Equals("Port ID", StringComparison.OrdinalIgnoreCase)) port = value;
            else if (key.Equals("System name", StringComparison.OrdinalIgnoreCase)) neighbor = value;
            else if (key.Equals("Management address", StringComparison.OrdinalIgnoreCase)) address = value;
        }
        Flush();
        return rows;
    }

    public static IReadOnlyList<H3cTransceiverFact> ParseTransceiverDiagnosis(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, H3cCommandPolicy.DisplayTransceiverDiagnosisInterface);
        var rows = new List<H3cTransceiverFact>();
        string? iface = null, status = null; decimal? temperature = null, voltage = null, bias = null, rx = null, tx = null;
        void Flush() { if (iface is not null) rows.Add(new(iface, temperature, voltage, bias, rx, tx, status)); iface = status = null; temperature = voltage = bias = rx = tx = null; }
        foreach (var line in transcript.Output.Split('\n'))
        {
            var text = line.Trim();
            if (text.EndsWith("transceiver diagnostic information:", StringComparison.OrdinalIgnoreCase)) { Flush(); iface = text[..^" transceiver diagnostic information:".Length].Trim(); continue; }
            var colon = text.IndexOf(':'); if (colon < 0) continue;
            var key = text[..colon].Trim().ToLowerInvariant(); var value = text[(colon + 1)..].Trim();
            decimal? Number() => decimal.TryParse(value.Split(' ', 2)[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;
            if (key == "temperature") temperature = Number(); else if (key == "voltage") voltage = Number(); else if (key == "bias current") bias = Number();
            else if (key == "rx power") rx = Number(); else if (key == "tx power") tx = Number(); else if (key == "status") status = value;
        }
        Flush();
        return rows;
    }

    private static bool IsInterfaceToken(string value) => Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9/._:-]{1,63}$", RegexOptions.CultureInvariant);

    private static IReadOnlyList<string> LooseCommandBody(string output, string command)
    {
        var lines = CliTranscriptParser.Normalize(output).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var commandLine = Array.FindIndex(lines, line => line.TrimEnd().EndsWith(command, StringComparison.OrdinalIgnoreCase));
        if (commandLine >= 0) lines = lines[(commandLine + 1)..];
        return lines.Where(line => !line.Contains("Copyright", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("Without the owner's", StringComparison.OrdinalIgnoreCase) &&
                !line.TrimStart().StartsWith('*') && line.Trim().Length > 0).ToArray();
    }
}

internal static class ZteCommandPolicy
{
    public const string ShowVersion = "show version";
    public const string ShowInterfaceBrief = "show interface brief";
    public const string ShowOpticalInfoBrief = "show opticalinfo brief";
    public const string ShowLldpNeighborBrief = "show lldp neighbor brief";
    public const string ShowLldpEntry = "show lldp entry";

    public static bool IsAllowed(string command) => command is ShowVersion or ShowInterfaceBrief or ShowOpticalInfoBrief or ShowLldpNeighborBrief or ShowLldpEntry;
}

internal sealed record ZteIdentityFact(string Model, string? SoftwareVersion, int? UptimeSeconds, string? SystemName);
internal sealed record ZteInterfaceFact(string Name, string Media, string Duplex, string Bandwidth, string AdminStatus, string PhysicalStatus, string ProtocolStatus, string? Description);
internal sealed record ZteOpticalFact(string InterfaceName, bool ModulePresent, decimal? RxPowerDbm, decimal? TxPowerDbm, string Status, string? Mode);
internal sealed record ZteLldpFact(string? LocalInterface, string? ChassisId, string? NeighborInterface, int? Ttl, string? NeighborSystemName, string? NeighborIp);
internal sealed record ZteParseResult<T>(string Status, T Value, IReadOnlyList<string> Warnings);

internal static class ZteZxr10Parser
{
    public static ZteParseResult<ZteIdentityFact?> ParseIdentity(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, ZteCommandPolicy.ShowVersion);
        if (TranscriptFailureStatus(transcript) is { } failure)
            return new(failure, null, new[] { transcript.Notice ?? failure });
        if (!Regex.IsMatch(transcript.Output, @"\bZTE\b", RegexOptions.IgnoreCase) || !Regex.IsMatch(transcript.Output, @"\bZXR10\b", RegexOptions.IgnoreCase))
            return new("NOT_RECOGNIZED", null, new[] { "output is not recognized as ZTE ZXR10" });
        var model = Regex.Match(transcript.Output, @"(?im)^\s*(?:ZTE\s+)?ZXR10\s+(?<model>[A-Za-z0-9-]+)\s*$").Groups["model"].Value;
        if (string.IsNullOrWhiteSpace(model)) model = "UNKNOWN";
        var version = Regex.Match(transcript.Output, @"Version\s*:\s*(?:[A-Za-z0-9-]+\s+)*?(?<version>V\d+(?:\.\d+){1,3}(?:B\d+)?)", RegexOptions.IgnoreCase).Groups["version"].Value;
        var uptime = Regex.Match(transcript.Output, @"System uptime is\s+(?:(\d+) day\(s\),?\s*)?(?:(\d+) hour\(s\),?\s*)?(?:(\d+) minute\(s\),?\s*)?(?:(\d+) second\(s\))?", RegexOptions.IgnoreCase);
        int? seconds = uptime.Success ? int.Parse(uptime.Groups[1].Value == "" ? "0" : uptime.Groups[1].Value, CultureInfo.InvariantCulture) * 86400 + int.Parse(uptime.Groups[2].Value == "" ? "0" : uptime.Groups[2].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(uptime.Groups[3].Value == "" ? "0" : uptime.Groups[3].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(uptime.Groups[4].Value == "" ? "0" : uptime.Groups[4].Value, CultureInfo.InvariantCulture) : null;
        var prompt = transcript.Prompt.Length > 0 ? transcript.Prompt[..^1] : null;
        return new("PARSED", new(model, string.IsNullOrWhiteSpace(version) ? null : version.ToUpperInvariant(), seconds, prompt), Array.Empty<string>());
    }

    public static ZteParseResult<IReadOnlyList<ZteInterfaceFact>> ParseInterfaces(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, ZteCommandPolicy.ShowInterfaceBrief);
        var rows = new List<ZteInterfaceFact>();
        var header = false;
        foreach (var line in transcript.Output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"^Interface\s+Attribute\s+Mode\s+BW\s+Admin\s+Phy\s+Prot", RegexOptions.IgnoreCase)) { header = true; continue; }
            var parts = trimmed.Split((char[]?)null, 8, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 7 || !Regex.IsMatch(parts[0], @"^(?:gei|xgei|xgeis|xxvgei|xlgei|cgei|sci)-[A-Za-z0-9/.:_-]+$", RegexOptions.IgnoreCase)) continue;
            rows.Add(new(parts[0], parts[1], parts[2], parts[3], parts[4].ToLowerInvariant(), parts[5].ToLowerInvariant(), parts[6].ToLowerInvariant(), parts.Length == 8 ? parts[7] : null));
        }
        if (TranscriptFailureStatus(transcript) is { } failure)
            return new(failure, rows, new[] { transcript.Notice ?? failure });
        return new(header ? rows.Count > 0 ? "PARSED" : "EMPTY" : "PARSE_FAILED", rows, header ? Array.Empty<string>() : new[] { "show interface brief header was not found" });
    }

    public static ZteParseResult<IReadOnlyList<ZteOpticalFact>> ParseOpticalBrief(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, ZteCommandPolicy.ShowOpticalInfoBrief);
        var rows = new List<ZteOpticalFact>();
        foreach (var line in transcript.Output.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !Regex.IsMatch(parts[0], @"^(?:gei|xgei|xgeis|xxvgei|xlgei|cgei|sci)-[A-Za-z0-9/.:_-]+$", RegexOptions.IgnoreCase)) continue;
            if (parts[1].Equals("offline", StringComparison.OrdinalIgnoreCase)) { rows.Add(new(parts[0], false, null, null, "NO_MODULE", null)); continue; }
            if (parts.Length < 6) continue;
            var rx = ParsePower(parts[^4]); var tx = ParsePower(parts[^3]); var status = parts[^2].ToUpperInvariant();
            rows.Add(new(parts[0], true, rx, tx, status switch { "NORMAL" => "NORMAL", "UNKNOWN" => "NO_LIGHT", "ABNORMAL" => "ABNORMAL", _ => "UNVERIFIED" }, parts[^1]));
        }
        if (TranscriptFailureStatus(transcript) is { } failure)
            return new(failure, rows, new[] { transcript.Notice ?? failure });
        return new(rows.Count > 0 ? "PARSED" : "EMPTY", rows, Array.Empty<string>());
    }

    public static ZteParseResult<IReadOnlyList<ZteLldpFact>> ParseLldpBrief(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, ZteCommandPolicy.ShowLldpNeighborBrief);
        var rows = new List<ZteLldpFact>();
        string? pending = null;
        void Flush()
        {
            if (pending is null) return;
            var parts = pending.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6)
            {
                var ttl = int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (int?)null;
                rows.Add(new(parts[0], parts[2], string.Join(' ', parts[3..^2]), ttl, parts[^1], NormalizeIp(parts[2])));
            }
            pending = null;
        }
        foreach (var line in transcript.Output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"^(?:gei|xgei|xgeis|xxvgei|xlgei|cgei|sci)-[A-Za-z0-9/.:_-]+$", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(trimmed, @"^(?:gei|xgei|xgeis|xxvgei|xlgei|cgei|sci)-[A-Za-z0-9/.:_-]+\s+", RegexOptions.IgnoreCase))
            {
                Flush();
                pending = trimmed;
            }
            else if (pending is not null && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                var joiner = pending.EndsWith("Ethern", StringComparison.OrdinalIgnoreCase) &&
                             trimmed.StartsWith("et", StringComparison.OrdinalIgnoreCase) ? string.Empty : " ";
                pending += joiner + trimmed;
            }
        }
        Flush();
        if (TranscriptFailureStatus(transcript) is { } failure)
            return new(failure, rows, new[] { transcript.Notice ?? failure });
        return new(rows.Count > 0 ? "PARSED" : "EMPTY", rows, Array.Empty<string>());
    }

    public static ZteParseResult<IReadOnlyList<ZteLldpFact>> ParseLldpEntries(string output)
    {
        var transcript = CliTranscriptParser.Parse(output, ZteCommandPolicy.ShowLldpEntry);
        var rows = new List<ZteLldpFact>();
        Dictionary<string, string>? current = null;
        void Flush() { if (current is null || !current.TryGetValue("local_port", out var local)) return; current.TryGetValue("chassis_id", out var chassis); current.TryGetValue("port_id", out var port); current.TryGetValue("time_to_live", out var ttlText); current.TryGetValue("system_name", out var name); current.TryGetValue("management_address", out var management); rows.Add(new(local, chassis, port, int.TryParse(ttlText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttl) ? ttl : null, name, NormalizeIp(management))); }
        foreach (var line in transcript.Output.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*([^:]+?)\s*:\s*(.*)$");
            if (!match.Success) continue;
            var key = Regex.Replace(match.Groups[1].Value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
            if (key == "local_port") { Flush(); current = new(StringComparer.OrdinalIgnoreCase); }
            if (current is not null) current[key] = match.Groups[2].Value.Trim();
        }
        Flush();
        if (TranscriptFailureStatus(transcript) is { } failure)
            return new(failure, rows, new[] { transcript.Notice ?? failure });
        return new(rows.Count > 0 ? "PARSED" : "EMPTY", rows, Array.Empty<string>());
    }

    private static string? TranscriptFailureStatus(CliCommandTranscript transcript) => transcript.Status switch
    {
        CliTranscriptStatus.Unsupported or CliTranscriptStatus.PermissionDenied or CliTranscriptStatus.Paged =>
            transcript.Status.ToString().ToUpperInvariant(),
        CliTranscriptStatus.Incomplete when transcript.Prompt.Length > 0 => "INCOMPLETE",
        _ => null
    };

    private static decimal? ParsePower(string value)
    {
        var token = value.Split('/', 2)[0];
        return decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static string? NormalizeIp(string? value) => Regex.Match(value ?? string.Empty, @"\b(?:\d{1,3}\.){3}\d{1,3}\b").Value switch { { Length: > 0 } ip => ip, _ => null };
}
