using System.Text;
using System.Text.Json;
using NetConsole.Next.Service;

internal static class H3cDiagnosticCaptureTests
{
    public static Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks++;
        }
        const string command = H3cDiagnosticCapture.Command;
        string Wrap(string body) => "<fixture>" + command + "\n" + body + "\n<fixture>";
        Check(H3cCommandPolicy.IsAllowed(command), "full diagnostic enters the common readonly command policy");
        Check(!H3cCommandPolicy.IsAllowed("n") && !H3cCommandPolicy.IsAllowed("save") &&
            !H3cCommandPolicy.IsAllowed(command + "\nsave"), "the interaction response never becomes a standalone task command");
        var profile = new H3cDeviceProfileSnapshot("fixture", "192.0.2.5", 22, "reader", "FIXTURE", "", true, 0, 0);
        Check(!NetmikoWorkerH3cSshTransportFactory.CanReplay(profile, "h3c", command),
            "a sent diagnostic whose result is unknown cannot be replayed automatically");
        var scenarios = new[]
        {
            (Output: Wrap("Diagnostics complete"), Failure: (string?)null, Status: "PARSED"),
            (Output: Wrap("% Unrecognized command found at '^' position."), Failure: "DIAGNOSTIC_UNSUPPORTED", Status: "UNRECOGNIZED"),
            (Output: Wrap("Permission denied"), Failure: "DIAGNOSTIC_PERMISSION_DENIED", Status: "UNRECOGNIZED"),
            (Output: Wrap("Partial\n---- More ----"), Failure: "DIAGNOSTIC_PAGED", Status: "PARTIAL"),
            (Output: "<fixture>" + command + "\nPartial", Failure: "DIAGNOSTIC_INCOMPLETE", Status: "PARTIAL"),
            (Output: "", Failure: "DIAGNOSTIC_EMPTY", Status: "EMPTY"),
            (Output: "<fixture>display version\nWrong command\n<fixture>", Failure: "DIAGNOSTIC_COMMAND_MISMATCH", Status: "PARTIAL")
        };
        foreach (var item in scenarios)
        {
            Check(H3cDiagnosticCapture.GetFailure(item.Output) == item.Failure, "diagnostic completion evidence: " + item.Failure);
            var parsed = H3cComwareParser.Parse(command, item.Output, completedExec: true);
            Check(parsed.Status == item.Status, "diagnostic parser keeps the existing status vocabulary");
            using var json = JsonDocument.Parse(parsed.Json);
            Check(json.RootElement.GetProperty("complete").GetBoolean() == (item.Failure is null) &&
                !json.RootElement.TryGetProperty("output", out _), "parsed metadata does not duplicate raw diagnostic/configuration text");
        }
        var sensitive = Wrap(" sysname fixture\n password simple synthetic-password\n token synthetic-token\n community synthetic-community\n" +
            "-----BEGIN PRIVATE KEY-----\nsynthetic-key-material\n-----END PRIVATE KEY-----\nComplete");
        var sanitized = H3cDiagnosticCapture.Sanitize(sensitive);
        Check(!sanitized.Contains("synthetic-password") && !sanitized.Contains("synthetic-token") &&
            !sanitized.Contains("synthetic-community") && !sanitized.Contains("synthetic-key-material") &&
            sanitized.Contains("sysname fixture") && sanitized.Contains("<REDACTED>"), "diagnostic Raw drops secret lines and private-key blocks");
        Check(H3cDiagnosticCapture.Sanitize(sanitized) == sanitized, "redaction is idempotent across scheduler, sink and download");
        Check(H3cDiagnosticCapture.GetFailure(sanitized) is null,
            "redaction markers remain body text and cannot impersonate closing Comware prompts");
        var large = Wrap(new string('x', 300 * 1024));
        Check(Encoding.UTF8.GetByteCount(large) > H3cRawOutput.MaximumBytes && H3cDiagnosticCapture.GetFailure(large) is null,
            "diagnostic capture accepts evidence larger than the ordinary 256 KiB cap");
        Check(H3cDiagnosticCapture.GetFailure(new string('x', H3cDiagnosticCapture.MaximumOutputBytes + 1)) == "DIAGNOSTIC_OUTPUT_LIMIT",
            "source diagnostics above 4 MiB are rejected before parsing or redaction");
        Check(H3cRawOutput.MaximumBytesFor(command) == 4 * 1024 * 1024 &&
            H3cRawOutput.MaximumBytesFor(H3cCommandPolicy.DisplayVersion) == 256 * 1024 &&
            H3cRawOutput.MaximumRecordBytesFor(command) == 6 * 1024 * 1024 &&
            H3cRawOutput.MaximumRecordBytesFor(H3cCommandPolicy.DisplayVersion) == 512 * 1024,
            "output and NDJSON limits are command-specific");
        Console.WriteLine($"H3C_DIAGNOSTIC_CAPTURE_TESTS={checks}/{checks} PASS");
        return Task.CompletedTask;
    }
}
