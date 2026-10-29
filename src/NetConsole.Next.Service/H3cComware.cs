using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

internal static class H3cCommandPolicy
{
    public const string DisplayVersion = "display version";
    public const string DisplayInterfaceBrief = "display interface brief";
    public const string DisplayDevice = "display device";
    public const string DisplayWlanApAll = "display wlan ap all";
    public const string DisplayWlanApAllAddress = "display wlan ap all address";
    public const string DisplayWlanApAllRadio = "display wlan ap all radio";
    public const string DisplayLldpNeighborInformationList = "display lldp neighbor-information list";
    public const string DisplayLldpNeighborInformationVerbose = "display lldp neighbor-information verbose";
    public const string DisplayTransceiverDiagnosisInterface = "display transceiver diagnosis interface";

    public static bool IsAllowed(string command) => command is
        DisplayVersion or DisplayInterfaceBrief or DisplayDevice or DisplayWlanApAll or
        DisplayWlanApAllAddress or DisplayWlanApAllRadio or DisplayLldpNeighborInformationList or
        DisplayLldpNeighborInformationVerbose or DisplayTransceiverDiagnosisInterface;
}

internal static class TerminalTextEncoding
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Gb18030 = CreateGb18030();

    public static bool IsSupported(string? value) => value?.Trim().ToUpperInvariant() is "AUTO" or "UTF-8" or "GB18030";

    public static Encoding Resolve(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "GB18030" => Gb18030,
        "UTF-8" => Utf8,
        _ => Utf8
    };

    public static string Decode(ReadOnlySpan<byte> bytes, string? preference)
    {
        var normalized = preference?.Trim().ToUpperInvariant();
        if (normalized == "GB18030") return Gb18030.GetString(bytes);
        if (normalized == "UTF-8") return Utf8.GetString(bytes);
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Gb18030.GetString(bytes); }
    }

    public static string DecodePrefix(byte[] bytes, int length, string? preference, bool allowIncompleteTail)
    {
        if (length < 0 || length > bytes.Length) throw new ArgumentOutOfRangeException(nameof(length));
        var normalized = preference?.Trim().ToUpperInvariant();
        var encoding = normalized switch
        {
            "GB18030" => Gb18030,
            "UTF-8" => Utf8,
            _ => Utf8
        };
        try { return DecodeWith(encoding, bytes, length, allowIncompleteTail); }
        catch (DecoderFallbackException) when (normalized is null or "" or "AUTO")
        {
            return DecodeWith(Gb18030, bytes, length, allowIncompleteTail);
        }
    }

    private static string DecodeWith(Encoding encoding, byte[] bytes, int length, bool allowIncompleteTail)
    {
        if (!allowIncompleteTail) return encoding.GetString(bytes, 0, length);
        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(length)];
        var count = decoder.GetChars(bytes, 0, length, chars, 0, flush: false);
        return new string(chars, 0, count);
    }

    private static Encoding CreateGb18030()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}

internal sealed class TerminalTextDecoder
{
    private readonly string _preference;
    private Encoding _encoding;
    private Decoder _decoder;
    private bool _autoUtf8;
    private readonly List<byte> _autoPending = [];

    public TerminalTextDecoder(string? preference)
    {
        _preference = preference?.Trim().ToUpperInvariant() ?? "AUTO";
        _autoUtf8 = _preference is "AUTO" or "";
        _encoding = _autoUtf8 ? TerminalTextEncoding.Resolve("UTF-8") : TerminalTextEncoding.Resolve(_preference);
        _decoder = _encoding.GetDecoder();
    }

    public byte[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return [];
        if (_autoUtf8)
        {
            _autoPending.AddRange(bytes.ToArray());
            try
            {
                var complete = _encoding.GetString(_autoPending.ToArray());
                _autoPending.Clear();
                return Encoding.UTF8.GetBytes(complete);
            }
            catch (DecoderFallbackException)
            {
                var probe = _encoding.GetDecoder();
                var chars = new char[_encoding.GetMaxCharCount(_autoPending.Count)];
                try
                {
                    probe.Convert(_autoPending.ToArray(), 0, _autoPending.Count, chars, 0, chars.Length,
                        flush: false, out _, out _, out _);
                    return [];
                }
                catch (DecoderFallbackException)
                {
                    _autoUtf8 = false;
                    _encoding = TerminalTextEncoding.Resolve("GB18030");
                    _decoder = _encoding.GetDecoder();
                    var fallback = DecodeWith(_encoding, _decoder, _autoPending.ToArray());
                    _autoPending.Clear();
                    return fallback;
                }
            }
        }
        try { return DecodeWith(_encoding, _decoder, bytes); }
        catch (DecoderFallbackException) { throw; }
    }

    private static byte[] DecodeWith(Encoding encoding, Decoder decoder, ReadOnlySpan<byte> bytes)
    {
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes, chars, flush: false);
        return Encoding.UTF8.GetBytes(chars, 0, count);
    }
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
    long UpdatedMs,
    string HostKeyUpdatePolicy = "STRICT",
    bool AllowLegacyAlgorithms = false,
    string TerminalEncoding = "AUTO",
    string TerminalProtocol = "AUTO",
    bool AllowTelnetFallback = false)
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

internal sealed record H3cParseResult(string Status, string Json);

internal static partial class H3cComwareParser
{
    [GeneratedRegex(@"H3C Comware Software,\s*Version\s+([^,\s]+)(?:,\s*Release\s+([^\r\n]+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9/._:-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceNamePattern();

    [GeneratedRegex(@"(?im)^\s*-{2,}\s*more\b.*$|^\s*press\s+any\s+key\b.*$|^\s*\[?more\]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PaginationPattern();

    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiControlPattern();

    public static H3cVersionInfo ParseVersion(string output)
    {
        var match = VersionPattern().Match(NormalizeTerminalText(output));
        return match.Success
            ? new("H3C Comware", match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value.Trim() : null, true)
            : new("unknown", string.Empty, null, false);
    }

    public static H3cInterfaceBriefResult ParseInterfaceBrief(string output)
    {
        var interfaces = new List<H3cInterfaceStatus>();
        var section = BriefSection.None;
        foreach (var rawLine in NormalizeTerminalText(output).Split('\n'))
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
                line.StartsWith("Speed or duplex:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Protocol:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Speed:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Duplex:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
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
        return Parse(command, output).Json;
    }

    public static H3cParseResult Parse(string command, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new("EMPTY", "{}");
        if (GetTranscriptFailure(command, output) is { } transcriptFailure)
            return transcriptFailure;
        if (command == H3cCommandPolicy.DisplayVersion)
        {
            var result = ParseVersion(output);
            return new(result.Recognized ? "PARSED" : "UNRECOGNIZED",
                System.Text.Json.JsonSerializer.Serialize(result));
        }
        if (command == H3cCommandPolicy.DisplayInterfaceBrief)
        {
            var result = ParseInterfaceBrief(output);
            return new(result.Recognized ? "PARSED" : "UNRECOGNIZED",
                System.Text.Json.JsonSerializer.Serialize(result));
        }
        if (command == H3cCommandPolicy.DisplayWlanApAll)
        {
            var result = ParseWlanApAll(output);
            return new(result.Count > 0 ? "PARSED" : "UNRECOGNIZED",
                System.Text.Json.JsonSerializer.Serialize(result));
        }
        if (command == H3cCommandPolicy.DisplayWlanApAllAddress)
        {
            var result = ParseWlanApAllAddress(output);
            return new(result.Count > 0 ? "PARSED" : "UNRECOGNIZED",
                System.Text.Json.JsonSerializer.Serialize(result));
        }
        if (command == H3cCommandPolicy.DisplayWlanApAllRadio)
        {
            var result = ParseWlanApAllRadio(output);
            return new(result.Count > 0 ? "PARSED" : "UNRECOGNIZED",
                System.Text.Json.JsonSerializer.Serialize(result));
        }
        if (command == H3cCommandPolicy.DisplayLldpNeighborInformationList)
        {
            var result = ParseLldpList(output);
            return new(result.Count > 0 ? "PARSED" : "UNRECOGNIZED",
                System.Text.Json.JsonSerializer.Serialize(result));
        }
        if (command == H3cCommandPolicy.DisplayLldpNeighborInformationVerbose)
        {
            var result = ParseLldpVerbose(output);
            return new(result.Count > 0 ? "PARSED" : "UNRECOGNIZED",
                System.Text.Json.JsonSerializer.Serialize(result));
        }
        if (command == H3cCommandPolicy.DisplayTransceiverDiagnosisInterface)
        {
            var result = ParseTransceiverDiagnosis(output);
            return new(result.Count > 0 ? "PARSED" : "UNRECOGNIZED",
                System.Text.Json.JsonSerializer.Serialize(result));
        }
        if (command == H3cCommandPolicy.DisplayDevice)
        {
            var transcript = CliTranscriptParser.Parse(output, command);
            var parseStatus = transcript.Status switch
            {
                CliTranscriptStatus.Complete => string.IsNullOrWhiteSpace(transcript.Output) ? "EMPTY" : "PARSED",
                CliTranscriptStatus.Empty => "EMPTY",
                CliTranscriptStatus.Incomplete when transcript.Prompt.Length == 0 =>
                    string.IsNullOrWhiteSpace(transcript.Output) ? "EMPTY" : "PARSED",
                CliTranscriptStatus.Paged or CliTranscriptStatus.Incomplete => "PARTIAL",
                CliTranscriptStatus.Unsupported or CliTranscriptStatus.PermissionDenied => "UNRECOGNIZED",
                _ => "UNCLASSIFIED"
            };
            return new(parseStatus,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = transcript.Status.ToString().ToUpperInvariant(),
                    output = transcript.Output,
                    notice = transcript.Notice
                }));
        }
        throw new ArgumentException("Only allowlisted H3C read-only commands have a parser.", nameof(command));
    }

    private static H3cParseResult? GetTranscriptFailure(string command, string output)
    {
        if (command is not (H3cCommandPolicy.DisplayDevice or H3cCommandPolicy.DisplayWlanApAll or
            H3cCommandPolicy.DisplayWlanApAllAddress or H3cCommandPolicy.DisplayWlanApAllRadio or
            H3cCommandPolicy.DisplayLldpNeighborInformationList or H3cCommandPolicy.DisplayLldpNeighborInformationVerbose or
            H3cCommandPolicy.DisplayTransceiverDiagnosisInterface))
            return null;

        var transcript = CliTranscriptParser.Parse(output, command);
        var parseStatus = transcript.Status switch
        {
            CliTranscriptStatus.Unsupported or CliTranscriptStatus.PermissionDenied => "UNRECOGNIZED",
            CliTranscriptStatus.Paged => "PARTIAL",
            CliTranscriptStatus.Incomplete when transcript.Prompt.Length > 0 => "PARTIAL",
            _ => null
        };
        return parseStatus is null
            ? null
            : new(parseStatus, System.Text.Json.JsonSerializer.Serialize(new
            {
                status = transcript.Status.ToString().ToUpperInvariant(),
                output = transcript.Output,
                notice = transcript.Notice
            }));
    }

    public static bool ContainsPaginationPrompt(string output)
    {
        return PaginationPattern().IsMatch(NormalizeTerminalText(output));
    }

    private static string NormalizeTerminalText(string? output)
    {
        var ansiStripped = AnsiControlPattern().Replace(output ?? string.Empty, string.Empty);
        var builder = new System.Text.StringBuilder(ansiStripped.Length);
        foreach (var character in ansiStripped)
        {
            if (character == '\b')
            {
                if (builder.Length > 0) builder.Length--;
            }
            else if (character != '\0' && character != '\x1b') builder.Append(character);
        }
        return builder.ToString();
    }

    private static string? At(string[] parts, int index) =>
        index >= parts.Length || parts[index] == "--" ? null : parts[index];

    private enum BriefSection { None, Route, Bridge }
}
