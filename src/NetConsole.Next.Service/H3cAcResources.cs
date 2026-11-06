using System.Text.Json;

namespace NetConsole.Next.Service;

// Fixed read-only commands inherited from the original AC collector. Optional
// output stays command evidence until a specific table parser has fixtures.
internal static class H3cAcResources
{
    public const string DisplayWlanApAllRadioBbssid = "display wlan ap all radio verbose filter bbssid";
    public const string DisplayWlanApAllConnectionRecord = "display wlan ap all connection-record";
    public const string DisplayWlanApAllRadioType = "display wlan ap all radio type";
    public const string DisplayWlanApUnauthenticated = "display wlan ap unauthenticated";
    public const string DisplayWlanApAllLldp = "display wlan ap all lldp";
    public const string DisplayWlanApAllVerbose = "display wlan ap all verbose";
    public const string DisplayCpuUsage = "display cpu-usage";
    public const string DisplayMemory = "display memory";
    public const string DisplayDeviceManuinfo = "display device manuinfo";

    public static IReadOnlyList<string> RequiredCommands { get; } = Array.AsReadOnly(new[]
    {
        H3cCommandPolicy.DisplayWlanApAll, H3cCommandPolicy.DisplayWlanApAllAddress,
        H3cCommandPolicy.DisplayWlanApAllRadio
    });

    public static IReadOnlyList<string> OptionalCommands { get; } = Array.AsReadOnly(new[]
    {
        DisplayWlanApAllRadioBbssid, DisplayWlanApAllConnectionRecord, DisplayWlanApAllRadioType,
        DisplayWlanApUnauthenticated, DisplayWlanApAllLldp, DisplayWlanApAllVerbose
    });

    public static IReadOnlyList<string> OverviewCommands { get; } = Array.AsReadOnly(new[]
    {
        DisplayCpuUsage, DisplayMemory, H3cCommandPolicy.DisplayVersion,
        H3cCommandPolicy.DisplayDevice, DisplayDeviceManuinfo
    });

    public static bool IsAllowed(string command) => command is
        DisplayWlanApAllRadioBbssid or DisplayWlanApAllConnectionRecord or DisplayWlanApAllRadioType or
        DisplayWlanApUnauthenticated or DisplayWlanApAllLldp or DisplayWlanApAllVerbose or
        DisplayCpuUsage or DisplayMemory or DisplayDeviceManuinfo;

    public static H3cParseResult? TryParse(string command, string output, bool completedExec = false)
    {
        if (!IsAllowed(command)) return null;
        var transcript = ParseTranscript(command, output, completedExec);
        var status = transcript.Status switch
        {
            CliTranscriptStatus.Unsupported or CliTranscriptStatus.PermissionDenied => "UNRECOGNIZED",
            CliTranscriptStatus.Paged or CliTranscriptStatus.Incomplete => "PARTIAL",
            CliTranscriptStatus.Empty => "EMPTY",
            _ => string.IsNullOrWhiteSpace(transcript.Output) ? "EMPTY" : "PARSED"
        };
        return new(status, JsonSerializer.Serialize(new
        {
            command, status = transcript.Status.ToString().ToUpperInvariant(),
            output = transcript.Output, notice = transcript.Notice,
            structured = false
        }));
    }

    public static CliCommandTranscript ParseTranscript(string command, string output, bool completedExec = false)
    {
        var transcript = CliTranscriptParser.Parse(output, command, completedExec);
        if (CliTranscriptParser.HasCommandBoundaryMismatch(output, command))
            return transcript with { Status = CliTranscriptStatus.Incomplete, Notice = "command boundaries do not match" };
        // Netmiko strips echo and prompt. Only sealed transport completion can
        // prove this body complete; offline fragments retain their partial state.
        return completedExec && transcript.Status == CliTranscriptStatus.Incomplete && transcript.Prompt.Length == 0
            ? transcript with { Status = CliTranscriptStatus.Complete, Notice = null }
            : transcript;
    }
}
