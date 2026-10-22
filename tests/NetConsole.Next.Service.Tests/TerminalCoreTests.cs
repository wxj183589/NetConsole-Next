using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace NetConsole.Next.Service.Tests;

internal static class TerminalCoreTests
{
    public static async Task RunAsync()
    {
        VerifiesRestrictedReadonlyPolicy();
        VerifiesTelnetNegotiation();
        VerifiesOneTimeTicket();
        await VerifiesSessionSchemaAndRecordingAsync();
        await VerifiesSegmentedRecordingIntegrityAndRecoveryAsync();
        await VerifiesLegacyV6SingleFileReadAsync();
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
        var payload = negotiator.Process(new byte[] { 255, 251, 1, (byte)'R', 255, 253, 42, (byte)'E' });
        Assert(Encoding.ASCII.GetString(payload) == "RE", "Telnet IAC bytes must not leak into terminal output");
        Assert(negotiator.Replies.Span.SequenceEqual(new byte[] { 255, 253, 1, 255, 252, 42 }),
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

    private static async Task VerifiesSegmentedRecordingIntegrityAndRecoveryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-terminal-v7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new RuntimeSettings
            {
                DataRoot = root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024
            };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var writer = new TerminalRecordingWriter(root, "segmented-session");
            var session = new TerminalSessionSnapshot("segmented-session", "device-1", "device-1", "SSH",
                "RESTRICTED_READONLY", "127.0.0.1", 22, null, null, "CONNECTED", now, null, now,
                "CONNECTED", null, null, null, writer.RelativePath, 0, null, false);
            catalog.CreateTerminalSession(session);
            var payload = Encoding.UTF8.GetBytes(new string('x', 120_000) + "<script>alert(1)</script>");
            for (var i = 0; i < 80; i++)
                await writer.AppendAsync(now + i, i % 2 == 0 ? "output" : "input", payload, CancellationToken.None);
            var result = await writer.CompleteAsync(CancellationToken.None);
            catalog.CompleteTerminalRecording(session.Id, result);
            Assert(result.Segments is { Count: > 1 }, "large recordings should roll into multiple ordered segments");
            var resultSegments = result.Segments!;
            Assert(resultSegments.SequenceEqual(resultSegments.OrderBy(item => item.Number)), "segments should be ordered");

            var recordingService = new TerminalRecordingService(catalog, settings);
            var verified = recordingService.Verify(session.Id);
            Assert(verified.Status == "COMPLETE" && verified.HashMatches && verified.Segments.Count > 1,
                $"segmented recording should verify: {verified}");
            var htmlContext = new DefaultHttpContext();
            htmlContext.Response.Body = new MemoryStream();
            var exports = new TerminalExportService(catalog, settings);
            await exports.WriteAsync(htmlContext.Response, catalog.GetTerminalSession(session.Id)!, "HTML", verified, CancellationToken.None);
            var html = Encoding.UTF8.GetString(((MemoryStream)htmlContext.Response.Body).ToArray());
            Assert(html.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", StringComparison.Ordinal),
                "HTML export must escape device output");
            Assert(catalog.TerminalExports(session.Id).Count == 1, "export audit should be persisted");
            var page = recordingService.ReadReplay(session.Id, 0, 60);
            Assert(page.Entries.Count == 60 && page.HasMore && page.Entries[0].SegmentNumber == 1 && page.Entries.Any(item => item.SegmentNumber > 1),
                "replay should page across the ordered segments without loading the entire recording");
            try { TerminalRecordingReader.ResolvePath(root, "terminal-recordings/../catalog.db"); throw new InvalidOperationException("path traversal was accepted"); }
            catch (InvalidDataException) { }

            var first = resultSegments[0];
            var firstPath = TerminalRecordingReader.ResolvePath(root, first.RelativePath);
            await File.AppendAllTextAsync(firstPath, "tampered\n");
            var corrupted = recordingService.Verify(session.Id);
            Assert(corrupted.Status == "CORRUPTED" && corrupted.Segments.Any(item => item.State == "CORRUPTED"),
                "tampered segment must remain visible as corrupted evidence");

            var openSessionId = "interrupted-open-session";
            var openWriter = new TerminalRecordingWriter(root, openSessionId);
            var openSession = new TerminalSessionSnapshot(openSessionId, "device-1", "device-1", "SSH",
                "RESTRICTED_READONLY", "127.0.0.1", 22, null, null, "CONNECTED", now, null, now,
                "CONNECTED", null, null, null, openWriter.RelativePath, 0, null, false);
            catalog.CreateTerminalSession(openSession);
            await openWriter.AppendAsync(now + 1, "output", Encoding.UTF8.GetBytes("prefix> "), CancellationToken.None);
            await openWriter.DisposeAsync();
            Assert(Directory.EnumerateFiles(Path.Combine(root, "terminal-recordings", openSessionId), "*.open").Any(),
                "an interrupted writer must leave its open segment as recovery evidence");

            var interrupted = new TerminalSessionSnapshot("interrupted-session", "device-1", "device-1", "SSH",
                "RESTRICTED_READONLY", "127.0.0.1", 22, null, null, "CONNECTED", now, null, now,
                "CONNECTED", null, null, null, null, 0, null, false);
            catalog.CreateTerminalSession(interrupted);
            var restarted = new Catalog(settings);
            restarted.Initialize();
            var recovered = restarted.GetTerminalSession(interrupted.Id);
            Assert(recovered?.RecordingStatus == "INTERRUPTED" && recovered.State == "DISCONNECTED",
                "startup recovery must preserve interrupted terminal sessions");
            var recoveredOpen = restarted.GetTerminalSession(openSessionId);
            var recoveredSegments = restarted.TerminalRecordingSegments(openSessionId);
            Assert(recoveredOpen?.RecordingStatus == "INTERRUPTED" && recoveredSegments.Count == 1 &&
                recoveredSegments[0].State == "INCOMPLETE" && recoveredSegments[0].RecordCount == 1,
                "startup recovery must index an open recording segment and preserve its verified prefix");
            var recoveredReplay = new TerminalRecordingService(restarted, settings).ReadReplay(openSessionId, 0, 10);
            Assert(recoveredReplay.Entries.Count == 1 && Encoding.UTF8.GetString(recoveredReplay.Entries[0].Data) == "prefix> ",
                "an interrupted open segment must remain replayable after restart");
            var eventCount = restarted.TerminalEvents(interrupted.Id).Count(item => item.EventType == "DISCONNECTED");
            restarted.Initialize();
            Assert(restarted.TerminalEvents(interrupted.Id).Count(item => item.EventType == "DISCONNECTED") == eventCount,
                "terminal recovery must be idempotent");
            Assert(restarted.TerminalRecordingSegments(openSessionId).Count == 1,
                "repeating startup recovery must not duplicate recovered recording segments");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task VerifiesLegacyV6SingleFileReadAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-terminal-v6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new RuntimeSettings
            {
                DataRoot = root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024
            };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            var relative = "terminal-recordings/legacy-v6.jsonl";
            var bytes = Encoding.UTF8.GetBytes("{\"at_ms\":1700000000000,\"direction\":\"output\",\"data_b64\":\"5Lit5paHOg==\"}\n");
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var session = new TerminalSessionSnapshot("legacy-v6", "device-legacy", "device-legacy", "SSH",
                "RESTRICTED_READONLY", "127.0.0.1", 22, null, null, "CLOSED", now, now, now,
                "CONNECTED", "USER_REQUEST", null, null, relative, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), true);
            catalog.CreateTerminalSession(session);
            catalog.CompleteTerminalRecording(session.Id, new(relative, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), true));
            var service = new TerminalRecordingService(catalog, settings);
            var integrity = service.Verify(session.Id);
            Assert(integrity.Status == "COMPLETE" && integrity.HashMatches,
                "schema v6 single-file recordings must remain readable after the v7 migration");
            var replay = service.ReadReplay(session.Id, 0, 10);
            Assert(replay.Entries.Count == 1 && Encoding.UTF8.GetString(replay.Entries[0].Data) == "中文:",
                "legacy v6 recording data must remain replayable");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
