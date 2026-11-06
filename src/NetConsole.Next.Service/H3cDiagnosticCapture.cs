using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

/// <summary>One bounded, display-only diagnostic operation on the shared Netmiko session.</summary>
internal static class H3cDiagnosticCapture
{
    public const string Command = "display diagnostic-information";
    public const int ReadTimeoutSeconds = 540;
    public const int MaximumOutputBytes = 4 * 1024 * 1024;
    public const string ConfirmationResponse = "n";
    // Narrow synthetic protocol coverage. Additional firmware wording requires evidence.
    public const string ConfirmationPattern = @"(?im)^[ \t]*Save[ \t]+or[ \t]+display[ \t]+diagnostic[ \t]+information[ \t]*\(Y[ \t]*=[ \t]*save,[ \t]*N[ \t]*=[ \t]*display\)[ \t]*\?[ \t]*\[Y/N\][ \t]*:[ \t\r\n]*$";

    private static readonly Regex SecretLine = new(
        @"^.*\b(?:password|passwd|passphrase|pass-phrase|pre-shared-key|community|private-key|secret|credential|authorization|token|psk)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex PrivateKey = new(
        @"-----BEGIN [^\r\n]*PRIVATE KEY-----[\s\S]*?(?:-----END [^\r\n]*PRIVATE KEY-----|\z)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string Sanitize(string output) => SecretLine.Replace(PrivateKey.Replace(output, "<REDACTED>"), "<REDACTED>");

    public static string? GetFailure(string output, bool completedExec = true)
    {
        if (Encoding.UTF8.GetByteCount(output) > MaximumOutputBytes) return "DIAGNOSTIC_OUTPUT_LIMIT";
        // The established redaction marker resembles a Comware <prompt>.
        // Treat that marker as body text when checking the sanitized transcript.
        var transcriptOutput = output.Replace("<REDACTED>", "[REDACTED]", StringComparison.Ordinal);
        if (CliTranscriptParser.HasCommandBoundaryMismatch(transcriptOutput, Command)) return "DIAGNOSTIC_COMMAND_MISMATCH";
        // A diagnostic is complete only after an observed closing prompt. The
        // transport's successful read alone must not promote a partial fixture.
        var transcript = CliTranscriptParser.Parse(transcriptOutput, Command, completedExec: false);
        return transcript.Status switch
        {
            CliTranscriptStatus.Unsupported => "DIAGNOSTIC_UNSUPPORTED",
            CliTranscriptStatus.PermissionDenied => "DIAGNOSTIC_PERMISSION_DENIED",
            CliTranscriptStatus.Paged => "DIAGNOSTIC_PAGED",
            CliTranscriptStatus.Empty => "DIAGNOSTIC_EMPTY",
            CliTranscriptStatus.Incomplete => "DIAGNOSTIC_INCOMPLETE",
            _ when !completedExec => "DIAGNOSTIC_INCOMPLETE",
            _ => null
        };
    }

    public static H3cParseResult Parse(string output, bool completedExec = false)
    {
        var failure = GetFailure(output, completedExec);
        var status = failure switch
        {
            null => "PARSED",
            "DIAGNOSTIC_EMPTY" => "EMPTY",
            "DIAGNOSTIC_UNSUPPORTED" or "DIAGNOSTIC_PERMISSION_DENIED" => "UNRECOGNIZED",
            _ => "PARTIAL"
        };
        // Raw is the evidence authority; do not duplicate a multi-MiB transcript
        // into parsed_json or expose configuration through facts publication.
        return new(status, JsonSerializer.Serialize(new
        {
            diagnostic = true,
            complete = failure is null,
            byteCount = Encoding.UTF8.GetByteCount(output),
            failure
        }));
    }
}
