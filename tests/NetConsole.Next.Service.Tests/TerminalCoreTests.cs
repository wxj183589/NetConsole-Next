using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

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
        await VerifiesRecordingPublicationSafetyAsync();
        await VerifiesRecordingCatalogBusyAndDiskFaultsAsync();
        await VerifiesSshHostKeyLifecycleAsync();
        VerifiesTerminalTextEncoding();
        VerifiesStreamingTerminalTextDecoder();
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
            var segmentCursorOffset = resultSegments[0].RecordCount + 1;
            var cursorPage = recordingService.ReadReplay(session.Id, segmentCursorOffset, 3);
            Assert(cursorPage.Entries.Count > 0 && cursorPage.Entries[0].Position == segmentCursorOffset &&
                   cursorPage.Entries[0].SegmentNumber >= 2,
                "replay pagination should use persisted segment record counts as a forward cursor");
            try { TerminalRecordingReader.ResolvePath(root, "terminal-recordings/../catalog.db"); throw new InvalidOperationException("path traversal was accepted"); }
            catch (InvalidDataException) { }

            var first = resultSegments[0];
            var firstPath = TerminalRecordingReader.ResolvePath(root, first.RelativePath);
            await File.AppendAllTextAsync(firstPath, "tampered\n");
            var corrupted = recordingService.Verify(session.Id);
            Assert(corrupted.Status == "CORRUPTED" && corrupted.Segments.Any(item => item.State == "CORRUPTED"),
                "tampered segment must remain visible as corrupted evidence");
            var corruptedSession = catalog.GetTerminalSession(session.Id);
            Assert(corruptedSession?.RecordingStatus == "CORRUPTED" && !corruptedSession.RecordingComplete,
                "a failed integrity check must never leave the historical session marked COMPLETE");

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

    private static async Task VerifiesRecordingPublicationSafetyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-terminal-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var collisionWriter = new TerminalRecordingWriter(root, "collision-session");
            await collisionWriter.AppendAsync(1700000000000, "output", Encoding.UTF8.GetBytes("original\n"), CancellationToken.None);
            var collisionDirectory = Path.Combine(root, "terminal-recordings", "collision-session");
            var collisionPath = Path.Combine(collisionDirectory, "segment-000001.jsonl");
            await File.WriteAllTextAsync(collisionPath, "existing-evidence\n", Encoding.UTF8);
            try
            {
                await collisionWriter.CompleteAsync(CancellationToken.None);
                throw new InvalidOperationException("a same-name segment collision should fail closed");
            }
            catch (IOException) { }
            Assert(await File.ReadAllTextAsync(collisionPath, Encoding.UTF8) == "existing-evidence\n",
                "sealing must not delete or overwrite an existing segment target");
            Assert(File.Exists(Path.Combine(collisionDirectory, "segment-000001.jsonl.open")),
                "the open segment must remain as collision evidence");
            File.Delete(collisionPath);
            var collisionResult = await collisionWriter.CompleteAsync(CancellationToken.None);
            Assert(collisionResult.Complete && File.Exists(collisionPath),
                "after the collision is resolved, completion must be retryable");
            await collisionWriter.DisposeAsync();

            var indexFailure = true;
            var indexFaults = new TerminalRecordingFaults
            {
                BeforeIndexPublish = _ =>
                {
                    if (indexFailure)
                    {
                        indexFailure = false;
                        throw new IOException("injected index publication interruption");
                    }
                }
            };
            var retryWriter = new TerminalRecordingWriter(root, "index-retry-session", indexFaults);
            await retryWriter.AppendAsync(1700000000001, "output", Encoding.UTF8.GetBytes("retry\n"), CancellationToken.None);
            try
            {
                await retryWriter.CompleteAsync(CancellationToken.None);
                throw new InvalidOperationException("the injected index publication failure should be observable");
            }
            catch (IOException) { }
            var retryDirectory = Path.Combine(root, "terminal-recordings", "index-retry-session");
            Assert(!File.Exists(Path.Combine(retryDirectory, "index.json")) &&
                   File.Exists(Path.Combine(retryDirectory, "segment-000001.jsonl")) &&
                   !Directory.EnumerateFiles(retryDirectory, "*.open").Any(),
                "an index failure must retain sealed segments without publishing a partial index");
            var retryResult = await retryWriter.CompleteAsync(CancellationToken.None);
            Assert(retryResult.Complete && File.Exists(Path.Combine(retryDirectory, "index.json")),
                "CompleteAsync must retry index publication without duplicating segments");
            var repeatResult = await retryWriter.CompleteAsync(CancellationToken.None);
            Assert(repeatResult.Sha256 == retryResult.Sha256 &&
                   !Directory.EnumerateFiles(retryDirectory, "index.json.previous-*").Any(),
                "repeating a successful completion must be idempotent");
            await retryWriter.DisposeAsync();

            var oldIndexWriter = new TerminalRecordingWriter(root, "old-index-session");
            await oldIndexWriter.AppendAsync(1700000000002, "output", Encoding.UTF8.GetBytes("new\n"), CancellationToken.None);
            var oldIndexDirectory = Path.Combine(root, "terminal-recordings", "old-index-session");
            var indexPath = Path.Combine(oldIndexDirectory, "index.json");
            await File.WriteAllTextAsync(indexPath, "old-index-evidence\n", Encoding.UTF8);
            var oldIndexResult = await oldIndexWriter.CompleteAsync(CancellationToken.None);
            var backups = Directory.EnumerateFiles(oldIndexDirectory, "index.json.previous-*").ToArray();
            Assert(oldIndexResult.Complete && backups.Length == 1 &&
                   await File.ReadAllTextAsync(backups[0], Encoding.UTF8) == "old-index-evidence\n",
                "atomic index replacement must retain the previous index as evidence");
            await oldIndexWriter.DisposeAsync();
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task VerifiesRecordingCatalogBusyAndDiskFaultsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-terminal-faults-" + Guid.NewGuid().ToString("N"));
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
            var writer = new TerminalRecordingWriter(root, "busy-session");
            var session = new TerminalSessionSnapshot("busy-session", "device-1", "device-1", "SSH",
                "RESTRICTED_READONLY", "127.0.0.1", 22, null, null, "CLOSED", now, now, now,
                "CONNECTED", "USER_REQUEST", null, null, writer.RelativePath, 0, null, false);
            catalog.CreateTerminalSession(session);
            await writer.AppendAsync(now, "output", Encoding.UTF8.GetBytes("busy\n"), CancellationToken.None);
            var result = await writer.CompleteAsync(CancellationToken.None);
            using var lockConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "catalog.db"), Pooling = false
            }.ToString());
            lockConnection.Open();
            using var lockTransaction = lockConnection.BeginTransaction();
            using (var lockCommand = lockConnection.CreateCommand())
            {
                lockCommand.Transaction = lockTransaction;
                lockCommand.CommandText = "UPDATE terminal_sessions SET last_activity_ms=last_activity_ms WHERE id='busy-session'";
                lockCommand.ExecuteNonQuery();
            }
            try
            {
                catalog.CompleteTerminalRecording(session.Id, result);
                throw new InvalidOperationException("SQLite busy should be observable during terminal recording commit");
            }
            catch (SqliteException ex)
            {
                Assert(ex.SqliteErrorCode == 5 || ex.SqliteExtendedErrorCode == 5,
                    $"expected SQLITE_BUSY, got {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}");
            }
            var segmentPath = Path.Combine(root, result.Segments![0].RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert(File.Exists(segmentPath), "SQLite busy must not remove an already published recording segment");
            lockTransaction.Rollback();
            catalog.CompleteTerminalRecording(session.Id, result);
            Assert(catalog.GetTerminalSession(session.Id)?.RecordingComplete == true,
                "terminal recording metadata must be retryable after SQLite busy is released");
            await writer.DisposeAsync();

            var diskFaults = new TerminalRecordingFaults
            {
                CreateStream = path => new FailAfterBytesStream(new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 4096, FileOptions.WriteThrough), 1)
            };
            var diskWriter = new TerminalRecordingWriter(root, "disk-fault-session", diskFaults);
            try
            {
                await diskWriter.AppendAsync(now + 1, "output", Encoding.UTF8.GetBytes(new string('x', 2048)), CancellationToken.None);
                throw new InvalidOperationException("the injected recording disk fault should be observable");
            }
            catch (IOException) { }
            var partial = await diskWriter.CompleteAsync(CancellationToken.None);
            var diskDirectory = Path.Combine(root, "terminal-recordings", "disk-fault-session");
            Assert(!partial.Complete && File.Exists(Path.Combine(diskDirectory, "segment-000001.jsonl")) &&
                   File.Exists(Path.Combine(diskDirectory, "index.json")),
                "a recording write interruption must preserve partial evidence and publish an INCOMPLETE index");
            await diskWriter.DisposeAsync();
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task VerifiesSshHostKeyLifecycleAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-terminal-host-key-" + Guid.NewGuid().ToString("N"));
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
            var deviceId = Guid.NewGuid().ToString("N");
            var first = Fingerprint(0x11);
            var second = Fingerprint(0x22);
            var third = Fingerprint(0x33);
            var fourth = Fingerprint(0x44);
            var fifth = Fingerprint(0x55);
            catalog.CreateH3cDeviceProfile(new(deviceId, "192.0.2.10", 22, "reader", "TEST_CREDENTIAL", "",
                true, 0, 0, "AUTO_UPDATE", false));
            var profile = catalog.GetH3cDeviceProfile(deviceId)!;
            var firstDecision = catalog.CheckSshHostKey(profile, "ssh-ed25519", first);
            Assert(firstDecision.Accepted && firstDecision.Decision == "FIRST_TRUST" && firstDecision.CorrelationId is not null,
                "an authorized device without a saved key should enter first-trust pending state");
            Assert(catalog.CommitSshHostKey(firstDecision.CorrelationId!), "first-trust key should become effective after connection success");
            Assert(catalog.CommitSshHostKey(firstDecision.CorrelationId!), "committing the same first-trust result should be idempotent");
            profile = catalog.GetH3cDeviceProfile(deviceId)!;
            Assert(profile.HostKeySha256 == H3cHostKeyFingerprint.Normalize(first), "first trusted fingerprint should be persisted on the profile");
            var match = catalog.CheckSshHostKey(profile, "ssh-ed25519", first);
            Assert(match.Accepted && match.Decision == "MATCH", "a later matching host key should connect directly");

            var changed = catalog.CheckSshHostKey(profile, "ssh-ed25519", second);
            Assert(changed.Accepted && changed.Decision == "AUTO_UPDATE" && changed.CorrelationId is not null,
                "AUTO_UPDATE should accept a changed key only through a pending correlation");
            Assert(catalog.CommitSshHostKey(changed.CorrelationId!), "AUTO_UPDATE key should become effective after connection success");
            profile = catalog.GetH3cDeviceProfile(deviceId)!;
            Assert(profile.HostKeySha256 == H3cHostKeyFingerprint.Normalize(second), "AUTO_UPDATE should replace current key only after effective commit");
            var history = catalog.SshHostKeyHistory(deviceId);
            Assert(history.Any(x => x.State == "PENDING") && history.Any(x => x.State == "EFFECTIVE") && history.Count >= 4,
                "host-key history should retain first trust, change pending and effective events");

            catalog.SetH3cCompatibility(deviceId, "STRICT", false);
            var strictChanged = catalog.CheckSshHostKey(profile, "ssh-ed25519", first);
            Assert(!strictChanged.Accepted && strictChanged.Decision == "REJECTED" && strictChanged.Reason == "HOST_KEY_CHANGED_STRICT",
                "STRICT should reject a changed host key and retain the current fingerprint");
            Assert(catalog.GetH3cDeviceProfile(deviceId)!.HostKeySha256 == H3cHostKeyFingerprint.Normalize(second),
                "STRICT rejection must not overwrite the current fingerprint");

            catalog.SetH3cCompatibility(deviceId, "AUTO_UPDATE", false, "GB18030", "AUTO", true);
            profile = catalog.GetH3cDeviceProfile(deviceId)!;
            Assert(profile.TerminalEncoding == "GB18030" && profile.TerminalProtocol == "AUTO" &&
                   profile.AllowTelnetFallback,
                "device compatibility settings should persist protocol, encoding and explicit Telnet fallback policy");
            var execFactory = new TerminalCoreFakeH3cSshTransportFactory(first);
            var execSession = new H3cSshDeviceSession(profile, new TerminalCoreFixedH3cCredentialProvider("synthetic"), execFactory, catalog);
            await execSession.ConnectAsync(CancellationToken.None);
            await execSession.DisposeAsync();
            Assert(catalog.SshHostKeyHistory(deviceId).Any(x => x.State == "EFFECTIVE" && x.KeyType == "ssh-ed25519"),
                "SSH Exec host-key verification should retain the concrete SSH key type");

            var unauthorized = new H3cDeviceProfileSnapshot(Guid.NewGuid().ToString("N"), "192.0.2.99", 22,
                "reader", "TEST_CREDENTIAL", "", true, 0, 0, "AUTO_UPDATE", false);
            var unauthorizedDecision = catalog.CheckSshHostKey(unauthorized, "ssh-ed25519", first);
            Assert(!unauthorizedDecision.Accepted && unauthorizedDecision.Reason == "UNAUTHORIZED_DEVICE",
                "unknown or unregistered devices must not enter automatic trust");

            catalog.SetH3cCompatibility(deviceId, "AUTO_UPDATE", false);
            profile = catalog.GetH3cDeviceProfile(deviceId)!;
            var concurrentChecks = await Task.WhenAll(
                Task.Run(() => catalog.CheckSshHostKey(profile, "ssh-ed25519", third)),
                Task.Run(() => catalog.CheckSshHostKey(profile, "ssh-ed25519", third)));
            var concurrentA = concurrentChecks[0];
            var concurrentB = concurrentChecks[1];
            Assert(concurrentA.CorrelationId is not null && concurrentB.CorrelationId == concurrentA.CorrelationId,
                "repeated concurrent observations of one pending key should share one correlation");
            catalog.FailSshHostKey(concurrentA.CorrelationId!, "SIMULATED_CONNECTION_CANCELLED");
            Assert(catalog.GetH3cDeviceProfile(deviceId)!.HostKeySha256 == H3cHostKeyFingerprint.Normalize(first),
                "a cancelled or failed connection must retain the old current key");
            Assert(catalog.SshHostKeyHistory(deviceId).Any(x => x.State == "FAILED"),
                "failed host-key activation must remain visible in the audit history");

            var competingChecks = await Task.WhenAll(
                Task.Run(() => catalog.CheckSshHostKey(profile, "ssh-ed25519", fourth)),
                Task.Run(() => catalog.CheckSshHostKey(profile, "ssh-ed25519", fifth)));
            Assert(competingChecks.All(item => item.Accepted) &&
                   competingChecks.Select(item => item.CorrelationId).Distinct().Count() == 2,
                "concurrent observations of different keys should create independently auditable pending candidates");
            Assert(catalog.CommitSshHostKey(competingChecks[0].CorrelationId!),
                "the first concurrent candidate should become effective");
            Assert(!catalog.CommitSshHostKey(competingChecks[1].CorrelationId!),
                "a stale concurrent candidate must be rejected by revision protection");
            Assert(catalog.SshHostKeyHistory(deviceId).Any(item => item.State == "FAILED" &&
                       item.Reason == "CONCURRENT_HOST_KEY_UPDATE"),
                "a stale concurrent candidate must remain visible as a failed audit event");
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        static string Fingerprint(byte seed) => Convert.ToBase64String(Enumerable.Range(0, 32)
            .Select(index => (byte)(seed + index)).ToArray()).TrimEnd('=');
    }

    private static void VerifiesTerminalTextEncoding()
    {
        Assert(TerminalTextEncoding.IsSupported("AUTO") && TerminalTextEncoding.IsSupported("GB18030") &&
               !TerminalTextEncoding.IsSupported("BIG5"), "terminal encoding policy should be bounded to supported modes");
        var source = TerminalTextEncoding.Resolve("GB18030").GetBytes("用户名: 密码:");
        Assert(TerminalTextEncoding.Decode(source, "GB18030") == "用户名: 密码:",
            "explicit GB18030 Telnet prompts should decode without replacement characters");
        Assert(TerminalTextEncoding.Decode(Encoding.UTF8.GetBytes("提示符> "), "AUTO") == "提示符> ",
            "AUTO terminal encoding should preserve valid UTF-8 device prompts");
    }

    private static void VerifiesStreamingTerminalTextDecoder()
    {
        var utf8 = Encoding.UTF8.GetBytes("中文提示符> ");
        var utf8Decoder = new TerminalTextDecoder("UTF-8");
        var first = utf8Decoder.Decode(utf8.AsSpan(0, 2));
        var second = utf8Decoder.Decode(utf8.AsSpan(2));
        Assert(Encoding.UTF8.GetString(first) + Encoding.UTF8.GetString(second) == "中文提示符> ",
            "UTF-8 terminal decoding must preserve characters split across transport reads");

        var gb = TerminalTextEncoding.Resolve("GB18030").GetBytes("中文提示符> ");
        var gbDecoder = new TerminalTextDecoder("GB18030");
        var gbFirst = gbDecoder.Decode(gb.AsSpan(0, 1));
        var gbSecond = gbDecoder.Decode(gb.AsSpan(1));
        Assert(Encoding.UTF8.GetString(gbFirst) + Encoding.UTF8.GetString(gbSecond) == "中文提示符> ",
            "GB18030 terminal decoding must preserve characters split across transport reads");
    }

    private sealed class TerminalCoreFixedH3cCredentialProvider(string? value) : IH3cCredentialProvider
    {
        public string? GetPassword(string reference) => value;
    }

    private sealed class TerminalCoreFakeH3cSshTransportFactory(string? fingerprint) : IH3cSshTransportFactory
    {
        public IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password)
        {
            Assert(password == "synthetic", "terminal host-key test should pass the resolved test credential");
            return new TerminalCoreFakeH3cSshTransport(fingerprint);
        }
    }

    private sealed class TerminalCoreFakeH3cSshTransport(string? fingerprint) : IH3cSshTransport
    {
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fingerprint is null) throw new H3cHostKeyMissingException();
            if (!verifyHostKey("ssh-ed25519", fingerprint)) throw new H3cHostKeyMismatchException();
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new IOException("terminal host-key test transport is disconnected");
            return Task.FromResult("synthetic Comware output");
        }

        public void Abort() => IsConnected = false;
        public void Dispose() => IsConnected = false;
    }

    private sealed class FailAfterBytesStream(Stream inner, long byteLimit) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = byteLimit - _written;
            if (buffer.Length > remaining)
            {
                if (remaining > 0)
                {
                    inner.Write(buffer[..(int)remaining].Span);
                    _written += remaining;
                }
                throw new IOException("No space left on device (deterministic terminal test injection).");
            }
            _written += buffer.Length;
            return inner.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
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
