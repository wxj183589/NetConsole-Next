using System.Net;
using System.Text;

namespace NetConsole.Next.Service.Tests;

internal static class TerminalCoreTests
{
    public static async Task RunAsync()
    {
        VerifiesRestrictedReadonlyPolicy();
        VerifiesTelnetNegotiation();
        VerifiesOneTimeTicket();
        await VerifiesSessionSchemaAndRecordingAsync();
    }

    private static void VerifiesRestrictedReadonlyPolicy()
    {
        Assert(RestrictedReadonlyInputPolicy.TryNormalize("display version", out var command, out _)
               && command == "display version", "RESTRICTED_READONLY should accept the existing read-only command");
        Assert(!RestrictedReadonlyInputPolicy.TryNormalize("display version; reboot", out _, out _),
            "command composition must be rejected server-side");
        Assert(!RestrictedReadonlyInputPolicy.TryNormalize("display version\n display interface brief", out _, out _),
            "multi-line paste must be rejected server-side");
        Assert(!RestrictedReadonlyInputPolicy.TryNormalize("display version\t", out _, out _),
            "control and completion input must not bypass the command policy");
    }

    private static void VerifiesTelnetNegotiation()
    {
        var negotiator = new TelnetNegotiator();
        var payload = negotiator.Process(new byte[] { 255, 251, 1, (byte)'R', 255, 253, 24, (byte)'E' });
        Assert(Encoding.ASCII.GetString(payload) == "RE", "Telnet IAC bytes must not leak into terminal output");
        Assert(negotiator.Replies.Span.SequenceEqual(new byte[] { 255, 253, 1, 255, 252, 24 }),
            "Telnet negotiation must accept echo and refuse unsupported terminal options deterministically");
    }

    private static void VerifiesOneTimeTicket()
    {
        var store = new TerminalTicketStore();
        var issued = store.Issue("session-1");
        Assert(store.TryConsume(issued.Ticket, "session-1"), "a valid terminal ticket should be consumable once");
        Assert(!store.TryConsume(issued.Ticket, "session-1"), "a terminal ticket must not be reusable");
        var wrong = store.Issue("session-1");
        Assert(!store.TryConsume(wrong.Ticket, "session-2"), "a terminal ticket must be bound to its session");
    }

    private static async Task VerifiesSessionSchemaAndRecordingAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new RuntimeSettings
            {
                DataRoot = root,
                WebPort = 18771,
                SyslogPort = 0,
                SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024
            };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var recording = new TerminalRecordingWriter(root, "session-test");
            var session = new TerminalSessionSnapshot("session-test", "device-1", "device-1", "SSH",
                "RESTRICTED_READONLY", "127.0.0.1", 22, null, null, "CONNECTING", now, null, now,
                null, null, null, null, recording.RelativePath, 0, null, false);
            catalog.CreateTerminalSession(session);
            catalog.RecordTerminalEvent(session.Id, new("INPUT_REJECTED", new { reason = "test" }));
            catalog.UpdateTerminalState(session.Id, "FAILED", "AUTHENTICATION_FAILED", "test");
            await recording.AppendAsync(now, "output", Encoding.UTF8.GetBytes("提示符> "), CancellationToken.None);
            var result = await recording.CompleteAsync(CancellationToken.None);
            catalog.CompleteTerminalRecording(session.Id, result);
            var persisted = catalog.GetTerminalSession(session.Id);
            Assert(persisted?.State == "FAILED" && persisted.RecordingComplete && persisted.RecordingBytes > 0,
                "terminal session state and recording integrity must persist separately from event rows");
            Assert(catalog.TerminalEvents(session.Id).Any(item => item.EventType == "INPUT_REJECTED"),
                "terminal event rows must be queryable by session");
            Assert(File.Exists(Path.Combine(root, result.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                "terminal recording must remain an independent file");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
