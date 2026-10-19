using NetConsole.Next.Service;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Security.Cryptography;
using System.Text;

await SchedulerTests.RunAsync();

internal static class SchedulerTests
{
    public static async Task RunAsync()
    {
        await EnforcesQueueAndConcurrencyLimits();
        await CancelsQueuedAndRunningRequests();
        await TimesOutAndDisposesSession();
        await ReportsConnectionFailuresAndDisposesSession();
        await StopsActiveSessionsDuringSchedulerShutdown();
        await PersistsDefinitionsRunsAndRestartState();
        VerifiesExistingV1CatalogUpgradeReadsEachRawOnce();
        VerifiesIncrementalRecoverySkipsLargeTrustedHistory();
        VerifiesQuarantineDispositionPreservesRawAndReopensHealthOnlyForAuditedIssues();
        VerifiesIngressQueueHasAPayloadByteLimit();
        await VerifiesSparseSyslogFlushesWithinConfiguredWindow();
        VerifiesSqliteWriteFailureRetainsRawFile();
        VerifiesSqliteBusyIsVisibleAndPreservesRawFile();
        VerifiesRawPermissionRevocationIsVisible();
        await VerifiesCaptureDiskFullRetainsPartialRawAndFaults();
        await MeasuresConcurrentSimulatedDeviceAndSyslogLoad();
        VerifiesRollingLogSizeAndRetention();
        RejectsInvalidRequests();
        Console.WriteLine("PASS: bounded/persisted scheduling, migration, incremental Raw recovery, SQLite/permission fault injection, queue rejection, timeout, cancellation and session lifecycle");
    }

    private static async Task PersistsDefinitionsRunsAndRestartState()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-tasks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = TestSettings(root);
            var catalog = new Catalog(settings);
            catalog.Initialize();
            catalog.Initialize(); // migration must be safe to repeat
            catalog.CreateTaskDefinition("sim-echo-1", "SIMULATED_DEVICE_ECHO", "sim://device-1",
                "show version", TimeSpan.FromSeconds(3));
            var factory = new FakeSessionFactory(_ => new FakeBehavior((command, _) => Task.FromResult("echo:" + command)));
            string runId;
            await using (var scheduler = new BoundedCollectionScheduler(factory, 1, 1, catalog))
            {
                Assert(scheduler.TryScheduleDefinition("sim-echo-1", out runId), "persistent definition should be admitted");
                var completed = await WaitTerminal(scheduler, runId);
                Assert(completed.State == "SUCCEEDED", completed.ToString()!);
            }
            var completedRun = catalog.RecentTaskRuns().Single(x => x.Id == runId);
            Assert(completedRun.State == "SUCCEEDED" && completedRun.QueuedMs is not null &&
                   completedRun.StartedMs is not null && completedRun.EndedMs is not null &&
                   completedRun.DefinitionId == "sim-echo-1", completedRun.ToString()!);
            Assert(completedRun.DeadlineMs >= completedRun.QueuedMs, completedRun.ToString()!);

            var interruptedId = catalog.CreateTaskRun("sim-echo-1");
            catalog.TransitionTaskRun(interruptedId, "CONNECTING");
            catalog.TransitionTaskRun(interruptedId, "RUNNING");
            var restartedCatalog = new Catalog(settings);
            restartedCatalog.Initialize();
            var interrupted = restartedCatalog.RecentTaskRuns().Single(x => x.Id == interruptedId);
            Assert(interrupted.State == "INTERRUPTED" && !string.IsNullOrWhiteSpace(interrupted.FailureReason), interrupted.ToString()!);
            restartedCatalog.CreateTaskDefinition("sim-timeout", "SIMULATED_DEVICE_ECHO", "sim://slow",
                "show status", TimeSpan.FromMilliseconds(100));
            var slowFactory = new FakeSessionFactory(_ => new FakeBehavior(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unreachable";
            }));
            await using (var scheduler = new BoundedCollectionScheduler(slowFactory, 1, 1, restartedCatalog))
            {
                Assert(scheduler.TryScheduleDefinition("sim-timeout", out var timeoutId), "persisted timeout task should be admitted");
                var timeoutRun = await WaitTerminal(scheduler, timeoutId);
                Assert(timeoutRun.State == "TIMED_OUT", timeoutRun.ToString()!);
            }
            var persistedTimeout = restartedCatalog.RecentTaskRuns().Single(x => x.TaskType == "SIMULATED_DEVICE_ECHO" && x.DefinitionId == "sim-timeout");
            Assert(persistedTimeout.State == "TIMED_OUT" && persistedTimeout.DeadlineMs is not null &&
                   persistedTimeout.FailureReason?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true,
                persistedTimeout.ToString()!);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var version = db.CreateCommand();
                version.CommandText = "SELECT MAX(version) FROM schema_versions";
                Assert(Convert.ToInt32(version.ExecuteScalar()) == 3, "v3 migration should be present after repeated initialization");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifiesExistingV1CatalogUpgradeReadsEachRawOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-v1-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".netconsole-next-root"), "NETCONSOLE_NEXT_V1\n");
            foreach (var directory in new[] { "raw", "published", "inbox", "archive", "backup" })
                Directory.CreateDirectory(Path.Combine(root, directory));
            var relative = "raw/syslog/v1-existing.ndjson";
            var fullPath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
            {
                received_ms = 1_800_000_000_000L,
                sender_ip = "192.0.2.7",
                sender_port = 514,
                payload_base64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })
            }) + "\n");
            File.WriteAllBytes(fullPath, bytes);

            var schemaStream = typeof(SchedulerTests).Assembly.GetManifestResourceStream("NetConsole.Next.Service.Tests.schema_v1.sql")
                ?? throw new InvalidOperationException("Embedded v1 schema test fixture was not found.");
            using (schemaStream)
            using (var reader = new StreamReader(schemaStream))
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var apply = db.CreateCommand();
                apply.CommandText = reader.ReadToEnd();
                apply.ExecuteNonQuery();
                using var insert = db.CreateCommand();
                insert.CommandText = @"INSERT INTO raw_segments(id,source_id,relative_path,state,first_received_ms,last_received_ms,
                    record_count,byte_count,sha256,sealed_ms) VALUES('v1-row','syslog-local',$path,'FINALIZED',
                    1800000000000,1800000000000,1,$bytes,$sha,1800000000000)";
                insert.Parameters.AddWithValue("$path", relative);
                insert.Parameters.AddWithValue("$bytes", bytes.Length);
                insert.Parameters.AddWithValue("$sha", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                insert.ExecuteNonQuery();
            }

            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            using (var wal = OpenCatalog(root))
            using (var journal = wal.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode";
                Assert(string.Equals((string?)journal.ExecuteScalar(), "wal", StringComparison.OrdinalIgnoreCase),
                    "catalog must keep WAL enabled after initialization without resetting journal mode per connection");
            }
            var first = catalog.RecoverRawSegments();
            Assert(first.BytesRead == bytes.Length && first.BytesHashed == bytes.Length && first.TrustedFilesSkipped == 0,
                "v1 files without checkpoints must receive one complete content verification");
            var second = catalog.RecoverRawSegments();
            Assert(second.BytesRead == 0 && second.BytesHashed == 0 && second.TrustedFilesSkipped == 1,
                "a fully verified v1 file should be skipped on the next startup");
            using (var upgraded = OpenCatalog(root))
            using (var columns = upgraded.CreateCommand())
            {
                columns.CommandText = "SELECT checkpoint_size FROM raw_segments WHERE relative_path=$path";
                columns.Parameters.AddWithValue("$path", relative);
                Assert(Convert.ToInt64(columns.ExecuteScalar()) == bytes.Length,
                    "v2 checkpoint should be persisted while preserving the v1 segment row");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifiesIncrementalRecoverySkipsLargeTrustedHistory()
    {
        const int fileCount = 1000;
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-incremental-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            var rawDirectory = Path.Combine(root, "raw", "syslog", "2026", "10", "19");
            Directory.CreateDirectory(rawDirectory);
            var payload = Convert.ToBase64String(new byte[48 * 1024]);
            var bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
            {
                received_ms = 1_800_000_000_000L,
                sender_ip = "192.0.2.1",
                sender_port = 514,
                payload_base64 = payload
            }) + "\n");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var transaction = db.BeginTransaction();
                using var insert = db.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = @"INSERT INTO raw_segments(id,source_id,relative_path,state,first_received_ms,last_received_ms,
                record_count,byte_count,sha256,sealed_ms,checkpoint_size,checkpoint_write_ticks,last_verified_ms)
                VALUES($id,'syslog-local',$path,'FINALIZED',1800000000000,1800000000000,1,$bytes,$sha,1800000000000,$size,$ticks,1800000000000)";
                var id = insert.Parameters.Add("$id", SqliteType.Text);
                var pathParam = insert.Parameters.Add("$path", SqliteType.Text);
                var size = insert.Parameters.Add("$size", SqliteType.Integer);
                var ticks = insert.Parameters.Add("$ticks", SqliteType.Integer);
                insert.Parameters.AddWithValue("$bytes", bytes.Length);
                insert.Parameters.AddWithValue("$sha", hash);
                for (var i = 0; i < fileCount; i++)
                {
                    var name = $"trusted-{i:D5}.ndjson";
                    var fullPath = Path.Combine(rawDirectory, name);
                    File.WriteAllBytes(fullPath, bytes);
                    var info = new FileInfo(fullPath);
                    id.Value = Guid.NewGuid().ToString("N");
                    pathParam.Value = $"raw/syslog/2026/10/19/{name}";
                    size.Value = info.Length;
                    ticks.Value = info.LastWriteTimeUtc.Ticks;
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();
            var summary = catalog.RecoverRawSegments();
            timer.Stop();
            Assert(summary.TrustedFilesSkipped == fileCount && summary.ScannedFiles == fileCount, summary.ToString()!);
            Assert(summary.BytesRead == 0 && summary.BytesHashed == 0, summary.ToString()!);
            Assert(timer.Elapsed < TimeSpan.FromSeconds(10), $"stat-only startup took {timer.Elapsed}; expected under 10 seconds");

            var lateOrphan = Path.Combine(rawDirectory, "late-unindexed.ndjson");
            File.WriteAllBytes(lateOrphan, bytes);
            var changed = catalog.RecoverRawSegments();
            Assert(changed.IndexedOrphanFiles == 1 && changed.TrustedFilesSkipped == fileCount, changed.ToString()!);
            Assert(changed.BytesRead == bytes.Length && changed.BytesHashed == bytes.Length, changed.ToString()!);
            Assert(catalog.GetRawCounts().Finalized == fileCount + 1, "orphan was not indexed after content validation");

            var tampered = Path.Combine(rawDirectory, "trusted-00000.ndjson");
            var originalTime = File.GetLastWriteTimeUtc(tampered);
            var alteredBytes = File.ReadAllBytes(tampered);
            const string payloadMarker = "\"payload_base64\":\"";
            var payloadStart = Encoding.UTF8.GetString(alteredBytes).IndexOf(payloadMarker, StringComparison.Ordinal) + payloadMarker.Length;
            Assert(payloadStart > 0 && alteredBytes[payloadStart] == (byte)'A', "test fixture should contain a replaceable base64 byte");
            alteredBytes[payloadStart] = (byte)'B';
            File.WriteAllBytes(tampered, alteredBytes);
            File.SetLastWriteTimeUtc(tampered, originalTime);
            var metadataOnly = catalog.RecoverRawSegments();
            Assert(metadataOnly.BytesRead == 0 && metadataOnly.TrustedFilesSkipped == fileCount + 1,
                "unchanged metadata should avoid opening historical Raw contents at startup");
            using (var db = OpenCatalog(root))
            using (var prioritize = db.CreateCommand())
            {
                prioritize.CommandText = "UPDATE raw_segments SET last_full_audit_ms=1800000000000 WHERE relative_path<>$path; UPDATE raw_segments SET last_full_audit_ms=NULL WHERE relative_path=$path";
                prioritize.Parameters.AddWithValue("$path", "raw/syslog/2026/10/19/trusted-00000.ndjson");
                prioritize.ExecuteNonQuery();
            }
            var audit = catalog.AuditOneFinalizedSegment();
            Assert(audit?.Contains("mismatch", StringComparison.OrdinalIgnoreCase) == true, audit ?? "expected low-priority audit to detect same-size, same-time tampering");
            Assert(catalog.GetRawCounts().Incomplete == 1 && File.Exists(tampered), "maintenance audit must retain and mark changed Raw evidence");
            Assert(catalog.GetIntegrityAuditCount() == 1, "checksum mismatch should have a durable audit record");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifiesQuarantineDispositionPreservesRawAndReopensHealthOnlyForAuditedIssues()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-raw-disposition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = TestSettings(root);
            var catalog = new Catalog(settings);
            catalog.Initialize();
            var open = Path.Combine(catalog.RawRoot, "syslog", "crashed.open");
            Directory.CreateDirectory(Path.GetDirectoryName(open)!);
            File.WriteAllText(open, System.Text.Json.JsonSerializer.Serialize(new
            {
                received_ms = 1_800_000_000_001L,
                sender_ip = "192.0.2.44",
                sender_port = 514,
                payload_base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("retained raw"))
            }) + "\n");

            catalog.BeginRawRecovery();
            var recovery = catalog.RecoverRawSegments();
            catalog.CompleteRawRecovery();
            Assert(recovery.RecoveredOpenFiles == 1 && catalog.GetUnresolvedIntegrityIssueCount() == 1,
                "an interrupted open segment must be visible as an unresolved, durable audit");
            var status = new RuntimeStatus();
            status.AssessRecovery(recovery, catalog.GetUnresolvedIntegrityIssueCount());
            Assert(status.HealthState == "DEGRADED", "unresolved historical Raw evidence must degrade health");

            var audit = catalog.RecentIntegrityAudits().Single();
            var retained = Path.Combine(root, audit.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(retained))).ToLowerInvariant();
            catalog.QuarantineIntegrityAudit(audit.Id, "test-operator", "Crash artifact reviewed and retained.");
            Assert(File.Exists(retained), "quarantine must preserve Raw bytes on disk");
            Assert(catalog.GetIntegrityAuditCount() == 1 && catalog.GetUnresolvedIntegrityIssueCount() == 0,
                "a disposition must remain auditable and stop counting as unresolved");
            var disposed = catalog.GetHealthSnapshot().RecentIntegrityAudits.Single();
            Assert(disposed.Disposition == "QUARANTINED" && disposed.DisposedBy == "test-operator" &&
                   disposed.DispositionDetail == "Crash artifact reviewed and retained." && disposed.ObservedSha256 == originalHash,
                "quarantine evidence must identify the actor, reason and preserved byte hash");
            status.ReassessIntegrityIssues(catalog.GetUnresolvedIntegrityIssueCount());
            Assert(status.HealthState == "HEALTHY", "confirmed, logically quarantined history must not permanently degrade current collection health");

            var changed = File.ReadAllBytes(retained);
            var text = Encoding.UTF8.GetString(changed);
            const string marker = "\"payload_base64\":\"";
            var payloadOffset = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            Assert(payloadOffset > 0, "fixture Raw should include Base64 payload");
            changed[payloadOffset] = changed[payloadOffset] == (byte)'A' ? (byte)'B' : (byte)'A';
            File.WriteAllBytes(retained, changed);
            var auditAgain = catalog.RecoverRawSegments();
            Assert(catalog.GetUnresolvedIntegrityIssueCount() > 0 && File.Exists(retained),
                "a changed quarantined Raw file must produce a new visible issue while preserving the file");
            status.AssessRecovery(auditAgain, catalog.GetUnresolvedIntegrityIssueCount());
            Assert(status.HealthState == "DEGRADED", "new integrity evidence must reopen degraded health");
            using var db = OpenCatalog(root);
            using var integrity = db.CreateCommand();
            integrity.CommandText = "PRAGMA quick_check(1)";
            Assert((string?)integrity.ExecuteScalar() == "ok", "disposition and subsequent audit must preserve SQLite integrity");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifiesIngressQueueHasAPayloadByteLimit()
    {
        Assert(UdpSyslogWorker.QueueCapacity == 8192 && UdpSyslogWorker.QueueByteCapacity == 32 * 1024 * 1024,
            "Syslog admission must keep the measured packet window and independent 32 MiB payload bound");
        var stats = new IngressStats(queueCapacity: 4);
        var queue = System.Threading.Channels.Channel.CreateBounded<SyslogPacket>(4);
        var accepted = new SyslogPacket(1, "127.0.0.1", 514, new byte[10]);
        var rejected = new SyslogPacket(2, "127.0.0.1", 514, new byte[8]);
        stats.MarkReceived(1);
        Assert(stats.TryQueue(queue.Writer, accepted, maximumQueuedBytes: 15), "payload fitting within byte budget must be admitted");
        stats.MarkReceived(2);
        Assert(!stats.TryQueue(queue.Writer, rejected, maximumQueuedBytes: 15), "byte budget must reject a packet even when packet-count capacity remains");
        Assert(stats.Received == 2 && stats.Accepted == 1 && stats.Dropped == 1 && stats.QueueBytes == 10 &&
               stats.QueueBytesHighWatermark == 10 && stats.QueueDepth == 1,
            "packet and payload-byte counters must describe the same bounded queue state");
        Assert(queue.Reader.TryRead(out var dequeued) && ReferenceEquals(dequeued, accepted), "accepted payload should remain in queue");
        stats.MarkDequeued(dequeued!.Payload.Length);
        Assert(stats.QueueBytes == 0 && stats.QueueDepth == 0, "dequeue must release byte and packet capacity");
    }

    private static async Task VerifiesSparseSyslogFlushesWithinConfiguredWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-sparse-flush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var port = GetFreeUdpPort();
            var settings = new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = port,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            catalog.ProbeRawWritable();
            var stats = new IngressStats();
            var status = new RuntimeStatus();
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var worker = new UdpSyslogWorker(settings, catalog, new RawSegmentWriterFactory(), stats, status,
                loggerFactory.CreateLogger<UdpSyslogWorker>());
            using var shutdown = new CancellationTokenSource();
            await worker.StartAsync(shutdown.Token);
            await WaitUntil(() => status.CollectionState == "listening");
            using (var sender = new System.Net.Sockets.UdpClient())
                await sender.SendAsync(new byte[] { 1, 2, 3, 4 }, new IPEndPoint(IPAddress.Loopback, port));
            await WaitUntil(() => stats.Flushed == 1, TimeSpan.FromSeconds(5));
            var open = Directory.GetFiles(catalog.RawRoot, "*.open", SearchOption.AllDirectories).Single();
            Assert(File.ReadLines(open).Count() == 1 && stats.QueueDepth == 0,
                "a sparse packet must reach its synced open Raw segment without waiting for the next packet");
            await worker.StopAsync(CancellationToken.None);
            Assert(catalog.GetRawCounts().Finalized == 1 && stats.Flushed == 1,
                "cooperative stop must seal and catalog the sparse Raw segment");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifiesSqliteWriteFailureRetainsRawFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-db-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            var raw = Path.Combine(root, "raw", "syslog", "captured.open");
            Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
            var contents = Encoding.UTF8.GetBytes("retained raw bytes\n");
            File.WriteAllBytes(raw, contents);
            using (var db = OpenCatalog(root))
            using (var trigger = db.CreateCommand())
            {
                trigger.CommandText = "CREATE TRIGGER fail_raw_insert BEFORE INSERT ON raw_segments BEGIN SELECT RAISE(ABORT,'injected database write failure'); END";
                trigger.ExecuteNonQuery();
            }
            AssertThrows<SqliteException>(() => catalog.RegisterFinalizedSegment("raw/syslog/captured.open", 1, 1, 1,
                Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant(), contents.Length));
            Assert(File.ReadAllBytes(raw).SequenceEqual(contents), "database failure must not delete Raw bytes");
            using (var check = OpenCatalog(root))
            using (var integrity = check.CreateCommand())
            {
                integrity.CommandText = "PRAGMA quick_check(1)";
                Assert((string?)integrity.ExecuteScalar() == "ok", "catalog should stay consistent after an aborted write");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifiesSqliteBusyIsVisibleAndPreservesRawFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-db-busy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            var raw = Path.Combine(root, "raw", "syslog", "locked.open");
            Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
            var contents = Encoding.UTF8.GetBytes("preserve while sqlite is locked\n");
            File.WriteAllBytes(raw, contents);
            using (var lockDb = OpenCatalog(root))
            {
                using (var begin = lockDb.CreateCommand()) { begin.CommandText = "BEGIN IMMEDIATE"; begin.ExecuteNonQuery(); }
                var write = Task.Run(() =>
                {
                    try { catalog.StartTaskRun("syslog-local", "LOCK_TEST"); return false; }
                    catch (SqliteException) { return true; }
                });
                Assert(write.Wait(TimeSpan.FromSeconds(10)), "SQLite busy test did not finish within its deadline");
                Assert(write.Result, "SQLite locked/busy error should be visible to the caller");
                Assert(File.ReadAllBytes(raw).SequenceEqual(contents), "SQLite lock must not remove Raw data");
                using var rollback = lockDb.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                rollback.ExecuteNonQuery();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifiesRawPermissionRevocationIsVisible()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-permission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            var original = File.GetUnixFileMode(catalog.RawRoot);
            try
            {
                File.SetUnixFileMode(catalog.RawRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                var failed = false;
                try { catalog.ProbeRawWritable(); }
                catch (UnauthorizedAccessException) { failed = true; }
                catch (IOException) { failed = true; }
                Assert(failed && !catalog.RawWriteAvailable && catalog.StorageFailure?.Contains("RAW_WRITE_UNAVAILABLE") == true,
                    "revoked Raw directory permissions must block collection and be visible");
            }
            finally { File.SetUnixFileMode(catalog.RawRoot, original); }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifiesCaptureDiskFullRetainsPartialRawAndFaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-capture-full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var port = GetFreeUdpPort();
            var settings = new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = port, SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            catalog.ProbeRawWritable();
            var stats = new IngressStats();
            var status = new RuntimeStatus();
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var worker = new UdpSyslogWorker(settings, catalog, new FailAfterBytesWriterFactory(1024), stats, status,
                loggerFactory.CreateLogger<UdpSyslogWorker>());
            using var shutdown = new CancellationTokenSource();
            await worker.StartAsync(shutdown.Token);
            await WaitUntil(() => status.CollectionState == "listening");
            using (var sender = new System.Net.Sockets.UdpClient())
                await sender.SendAsync(new byte[60_000], new IPEndPoint(IPAddress.Loopback, port));
            await WaitUntil(() => status.HealthState == "FAULTED");
            Assert(status.CollectionState == "faulted" && stats.Accepted == 1 && stats.Flushed == 0,
                $"write error was not visible: {status.HealthState}/{status.CollectionState}, accepted={stats.Accepted}, flushed={stats.Flushed}");
            var open = Directory.GetFiles(catalog.RawRoot, "*.open", SearchOption.AllDirectories).Single();
            var retained = File.ReadAllBytes(open);
            Assert(retained.Length > 0 && retained.Length <= 1024 && retained.Any(b => b != 0),
                $"failed write should retain partial Raw bytes, got {retained.Length}");
            await worker.StopAsync(CancellationToken.None);
            var run = catalog.RecentTaskRuns().Single(x => x.TaskType == "SYSLOG_UDP");
            Assert(run.State == "FAILED", run.ToString()!);
            using (var db = OpenCatalog(root))
            using (var integrity = db.CreateCommand())
            {
                integrity.CommandText = "PRAGMA quick_check(1)";
                Assert((string?)integrity.ExecuteScalar() == "ok", "database must remain consistent after Raw write failure");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task MeasuresConcurrentSimulatedDeviceAndSyslogLoad()
    {
        const int datagrams = 500;
        const int deviceRuns = 40;
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-combined-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var port = GetFreeUdpPort();
            var settings = new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = port, SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            catalog.ProbeRawWritable();
            catalog.CreateTaskDefinition("sim-load", "SIMULATED_DEVICE_ECHO", "sim://load", "show status", TimeSpan.FromMinutes(1));
            var sessionFactory = new FakeSessionFactory(_ => new FakeBehavior(async (command, token) =>
            {
                await Task.Delay(5, token);
                return "echo:" + command;
            }));
            await using var scheduler = new BoundedCollectionScheduler(sessionFactory, 2, 128, catalog);
            var stats = new IngressStats();
            var status = new RuntimeStatus();
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var worker = new UdpSyslogWorker(settings, catalog, new RawSegmentWriterFactory(), stats, status,
                loggerFactory.CreateLogger<UdpSyslogWorker>());
            var process = System.Diagnostics.Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var startedAt = System.Diagnostics.Stopwatch.StartNew();
            using var shutdown = new CancellationTokenSource();
            await worker.StartAsync(shutdown.Token);
            await WaitUntil(() => status.CollectionState == "listening");
            var runIds = new List<string>();
            for (var i = 0; i < deviceRuns; i++)
            {
                Assert(scheduler.TryScheduleDefinition("sim-load", out var id), "combined simulated device task should be queued");
                runIds.Add(id);
            }
            using (var sender = new System.Net.Sockets.UdpClient())
            {
                var buffer = new byte[128];
                for (var i = 0; i < datagrams; i++)
                {
                    await sender.SendAsync(buffer, new IPEndPoint(IPAddress.Loopback, port));
                    await Task.Delay(2);
                }
            }
            await WaitUntil(() => scheduler.Status.Active == 0 && scheduler.Status.Queued == 0 &&
                runIds.All(id => scheduler.Get(id)?.State is "SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED"),
                TimeSpan.FromSeconds(30));
            await WaitUntil(() => stats.QueueDepth == 0);
            await worker.StopAsync(CancellationToken.None);
            startedAt.Stop();
            process.Refresh();
            var taskRuns = catalog.RecentTaskRuns(100).Where(x => x.DefinitionId == "sim-load").ToArray();
            var record = new
            {
                scenario = "simulated_device_plus_udp_syslog",
                elapsedMs = startedAt.ElapsedMilliseconds,
                processCpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                workingSetBytes = process.WorkingSet64,
                threadCount = process.Threads.Count,
                handleCount = OperatingSystem.IsWindows() ? process.HandleCount : (int?)null,
                udpAttempted = datagrams,
                udpReceived = stats.Received,
                udpAccepted = stats.Accepted,
                udpDropped = stats.Dropped,
                udpFlushed = stats.Flushed,
                queueCapacity = UdpSyslogWorker.QueueCapacity,
                queueHighWatermark = stats.QueueHighWatermark,
                rawWriteLatency = stats.RawFlushLatency,
                deviceDefinitions = 1,
                deviceRuns = runIds.Count,
                deviceSucceeded = taskRuns.Count(x => x.State == "SUCCEEDED"),
                deviceOtherStates = taskRuns.Where(x => x.State != "SUCCEEDED")
                    .Select(x => new { x.Id, x.State, x.FailureReason }).ToArray(),
                schedulerAccepted = scheduler.Status.Accepted,
                schedulerRejected = scheduler.Status.Rejected,
                dataDirectoryBytes = DirectoryBytes(root)
            };
            Console.WriteLine("PERF " + System.Text.Json.JsonSerializer.Serialize(record));
            Assert(stats.Received == datagrams && stats.Accepted == datagrams && stats.Dropped == 0 && stats.Flushed == datagrams,
                $"below-capacity combined load should receive, admit and durably flush every packet: {stats.Received}/{stats.Accepted}/{stats.Dropped}/{stats.Flushed}");
            Assert(stats.QueueDepth <= UdpSyslogWorker.QueueCapacity &&
                   stats.QueueHighWatermark <= UdpSyslogWorker.QueueCapacity,
                $"queue occupancy telemetry must remain within the bounded capacity: {stats.QueueDepth}/{stats.QueueHighWatermark}");
            Assert(taskRuns.Length == deviceRuns && record.deviceSucceeded == deviceRuns,
                $"all simulated device tasks should finish successfully: {System.Text.Json.JsonSerializer.Serialize(record.deviceOtherStates)}; persisted={taskRuns.Length}/{deviceRuns}, succeeded={record.deviceSucceeded}/{deviceRuns}");
        }
        finally { Directory.Delete(root, true); }
    }

    private static long DirectoryBytes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);

    private static int GetFreeUdpPort()
    {
        using var socket = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static RuntimeSettings TestSettings(string root) => new()
    {
        DataRoot = root,
        WebPort = 18771,
        SyslogPort = 0,
        SyslogBind = IPAddress.Loopback,
        SyslogReceiveBufferBytes = 4 * 1024 * 1024
    };

    private static SqliteConnection OpenCatalog(string root)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db"), Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static async Task EnforcesQueueAndConcurrencyLimits()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeSessionFactory(target => target == "device-1"
            ? new FakeBehavior(async (command, token) =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
                return $"echo:{command}";
            })
            : new FakeBehavior((command, _) => Task.FromResult($"echo:{command}")));

        await using var scheduler = new BoundedCollectionScheduler(factory, maxConcurrency: 1, queueCapacity: 1);
        Assert(scheduler.TrySchedule(Request("device-1", "display version"), out var firstId), "first task should be accepted");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(scheduler.TrySchedule(Request("device-2", "display interface brief"), out var queuedId), "one waiting task should be accepted");
        Assert(!scheduler.TrySchedule(Request("device-3", "display version"), out _), "full queue must reject instead of growing");
        Assert(scheduler.Status.Active == 1 && scheduler.Status.Queued == 1 && scheduler.Status.Rejected == 1, scheduler.Status.ToString()!);
        Assert(scheduler.Cancel(queuedId), "queued request should be cancellable");
        Assert((await WaitTerminal(scheduler, queuedId)).State == "CANCELLED", "queued request should become cancelled");

        releaseFirst.TrySetResult();
        var first = await WaitTerminal(scheduler, firstId);
        Assert(first.State == "SUCCEEDED" && first.Result == "echo:display version", first.ToString()!);
        await WaitUntil(() => scheduler.Status.Active == 0 && scheduler.Status.Queued == 0);
        Assert(factory.CreatedCount == 1, $"cancelled queued work must not open a device session (created {factory.CreatedCount})");
        Assert(factory.DisposedCount == 1, $"completed session should be disposed (disposed {factory.DisposedCount})");
        Assert(factory.MaximumActiveSessions == 1, $"concurrency exceeded one session ({factory.MaximumActiveSessions})");
    }

    private static async Task CancelsQueuedAndRunningRequests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeSessionFactory(_ => new FakeBehavior(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }));
        await using var scheduler = new BoundedCollectionScheduler(factory, maxConcurrency: 1, queueCapacity: 2);
        Assert(scheduler.TrySchedule(Request("device-cancel", "display version"), out var id), "cancellable request should be accepted");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(scheduler.Cancel(id), "running request should be cancellable");
        var cancelled = await WaitTerminal(scheduler, id);
        Assert(cancelled.State == "CANCELLED", cancelled.ToString()!);
        await WaitUntil(() => scheduler.Status.Active == 0);
        Assert(factory.DisposedCount == 1, "cancelled session should be disposed");
    }

    private static async Task TimesOutAndDisposesSession()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeSessionFactory(_ => new FakeBehavior(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }));
        await using var scheduler = new BoundedCollectionScheduler(factory, maxConcurrency: 1, queueCapacity: 1);
        Assert(scheduler.TrySchedule(new("device-timeout", "display version", TimeSpan.FromMilliseconds(150)), out var id), "timed request should be accepted");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var timedOut = await WaitTerminal(scheduler, id);
        Assert(timedOut.State == "TIMED_OUT", timedOut.ToString()!);
        Assert(timedOut.Error?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true, timedOut.ToString()!);
        Assert(factory.DisposedCount == 1, "timed-out session should be disposed");
    }

    private static async Task ReportsConnectionFailuresAndDisposesSession()
    {
        var factory = new FakeSessionFactory(_ => new FakeBehavior((_, _) => Task.FromResult("unused"), FailConnect: true));
        await using var scheduler = new BoundedCollectionScheduler(factory, maxConcurrency: 1, queueCapacity: 1);
        Assert(scheduler.TrySchedule(Request("device-connect-failure", "display version"), out var id), "connection request should be accepted");
        var failed = await WaitTerminal(scheduler, id);
        Assert(failed.State == "FAILED" && failed.Error == "IOException", failed.ToString()!);
        Assert(factory.DisposedCount == 1, "failed connection session should be disposed");
    }

    private static async Task StopsActiveSessionsDuringSchedulerShutdown()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeSessionFactory(_ => new FakeBehavior(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }));
        var scheduler = new BoundedCollectionScheduler(factory, maxConcurrency: 1, queueCapacity: 1);
        Assert(scheduler.TrySchedule(Request("device-shutdown", "display version"), out var id), "shutdown request should be accepted");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await scheduler.DisposeAsync();
        Assert(scheduler.Get(id)?.State == "CANCELLED", scheduler.Get(id)?.ToString() ?? "shutdown task disappeared");
        Assert(factory.DisposedCount == 1, "shutdown must dispose the active device session");
        Assert(scheduler.Status.Active == 0 && scheduler.Status.Queued == 0, scheduler.Status.ToString()!);
        Assert(!scheduler.Cancel(id), "shutdown scheduler must not accept cancellation after disposal");
    }

    private static void VerifiesRollingLogSizeAndRetention()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-logs-" + Guid.NewGuid().ToString("N"));
        var diagnostics = new FileLogDiagnostics();
        using (var provider = new RollingFileLoggerProvider(root, diagnostics, maximumFileBytes: 1024, retainedFileCount: 2))
        {
            var logger = provider.CreateLogger("LogRotationTest");
            for (var i = 0; i < 30; i++) logger.LogInformation("entry-{Entry} {Payload}", i, new string('x', 96));
        }

        var files = Directory.GetFiles(Path.Combine(root, "logs"), "service-*.log");
        Assert(files.Length == 2, $"expected two retained log files, found {files.Length}");
        Assert(files.All(path => new FileInfo(path).Length <= 1024), "a log exceeded its configured file size limit");
        Assert(files.Any(path => File.ReadAllText(path).Contains("entry-29", StringComparison.Ordinal)), "latest log record was pruned");
        Assert(diagnostics.WriteFailures == 0, $"logger reported {diagnostics.WriteFailures} write failures");
        Directory.Delete(root, recursive: true);
    }

    private static void RejectsInvalidRequests()
    {
        var factory = new FakeSessionFactory(_ => new FakeBehavior((_, _) => Task.FromResult("unused")));
        using var scheduler = new SynchronousSchedulerScope(factory);
        AssertThrows<ArgumentException>(() => scheduler.Scheduler.TrySchedule(new("device", "display version\nreboot", TimeSpan.FromSeconds(1)), out _));
        AssertThrows<ArgumentOutOfRangeException>(() => scheduler.Scheduler.TrySchedule(new("device", "display version", TimeSpan.FromMilliseconds(10)), out _));
    }

    private static DeviceCollectionRequest Request(string target, string command) => new(target, command, TimeSpan.FromSeconds(5));

    private static async Task<DeviceCollectionSnapshot> WaitTerminal(BoundedCollectionScheduler scheduler, string id)
    {
        DeviceCollectionSnapshot? snapshot = null;
        await WaitUntil(() =>
        {
            snapshot = scheduler.Get(id);
            return snapshot is not null && snapshot.State is "SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED";
        });
        return snapshot!;
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition was not reached in the test deadline.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FakeSessionFactory(Func<string, FakeBehavior> behaviorFactory) : IDeviceSessionFactory
    {
        private int _active;
        private int _maximumActive;
        private int _created;
        private int _disposed;

        public int CreatedCount => Volatile.Read(ref _created);
        public int DisposedCount => Volatile.Read(ref _disposed);
        public int MaximumActiveSessions => Volatile.Read(ref _maximumActive);

        public IDeviceSession Create(string target)
        {
            Interlocked.Increment(ref _created);
            return new FakeSession(this, behaviorFactory(target));
        }

        private void Connected()
        {
            var active = Interlocked.Increment(ref _active);
            int current;
            while (active > (current = Volatile.Read(ref _maximumActive)) &&
                   Interlocked.CompareExchange(ref _maximumActive, active, current) != current) { }
        }

        private void Disconnected() => Interlocked.Decrement(ref _active);
        private void Disposed() => Interlocked.Increment(ref _disposed);

        private sealed class FakeSession(FakeSessionFactory owner, FakeBehavior behavior) : IDeviceSession
        {
            private bool _connected;

            public Task ConnectAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (behavior.FailConnect) throw new IOException("simulated connect failure");
                owner.Connected();
                _connected = true;
                return Task.CompletedTask;
            }

            public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
            {
                if (!_connected) throw new InvalidOperationException("session is not connected");
                return behavior.Execute(command, cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                if (_connected) owner.Disconnected();
                owner.Disposed();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed record FakeBehavior(Func<string, CancellationToken, Task<string>> Execute, bool FailConnect = false);

    private sealed class FailAfterBytesWriterFactory(long byteLimit) : IRawSegmentWriterFactory
    {
        public RawSegmentWriter Create(Catalog catalog) => new(catalog, path => new FailAfterBytesStream(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32768, FileOptions.Asynchronous), byteLimit));
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
        public override void Write(byte[] buffer, int offset, int count) => WriteLimited(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => WriteLimited(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteLimited(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private async ValueTask WriteLimited(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            var remaining = byteLimit - _written;
            if (buffer.Length > remaining)
            {
                if (remaining > 0)
                {
                    await inner.WriteAsync(buffer[..(int)remaining], cancellationToken);
                    _written += remaining;
                }
                throw new IOException("No space left on device (deterministic test injection).");
            }
            await inner.WriteAsync(buffer, cancellationToken);
            _written += buffer.Length;
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

    private sealed class SynchronousSchedulerScope(FakeSessionFactory factory) : IDisposable
    {
        public BoundedCollectionScheduler Scheduler { get; } = new(factory, 1, 1);
        public void Dispose() => Scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
