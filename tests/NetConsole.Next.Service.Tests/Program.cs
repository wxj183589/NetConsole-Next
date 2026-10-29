using NetConsole.Next.Service;
using NetConsole.Next.Service.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NetConsole.Next.Credentials;

if (args.Length > 0 && args[0] == "--ssh-wire")
    await SchedulerTests.RunSshWireAsync(args[1..]);
else if (args.Length > 0 && args[0] == "--sqlite-probe")
    SqliteSecurityTests.Run();
else if (args.Length > 0 && args[0] == "--p2b2-migrate")
    await P2B2MigrationChild.RunAsync(args[1..]);
else if (args.Length > 0 && args[0] == "--p2b4-migrate")
    await P2B4MigrationChild.RunAsync(args[1..]);
else if (args.Length > 0 && args[0] == "--p2b5-site-scope-migrate")
    await P2B5SiteScopeMigrationChild.RunAsync(args[1..]);
else if (args.Length > 0 && args[0] == "--p2b5-export-migrate")
    await P2B5ExportMigrationChild.RunAsync(args[1..]);
else if (args.Length > 0 && args[0] == "--p2b5-rail-reference-migrate")
    await P2B5RailReferenceMigrationChild.RunAsync(args[1..]);
else if (args.Length > 0 && args[0] == "--runtime-settings-tests")
    RuntimeSettingsTests.Run();
else if (args.Length > 0 && args[0] == "--archive-tests")
    await SchedulerTests.RunArchiveTestsAsync();
else
    await SchedulerTests.RunAsync();

internal static class SchedulerTests
{
    public static async Task RunSshWireAsync(string[] arguments)
    {
        if (arguments.Length != 3 || arguments[0] is not ("success" or "host-key" or "auth" or "timeout" or "disconnect" or "concurrent"))
            throw new ArgumentException("Expected: --ssh-wire <success|host-key|auth|timeout|disconnect|concurrent> <port> <sha256-pin>.");
        var mode = arguments[0];
        var port = int.Parse(arguments[1], System.Globalization.CultureInfo.InvariantCulture);
        var pin = arguments[2];
        var password = Environment.GetEnvironmentVariable("NETCONSOLE_TEST_SSH_PASSWORD") ?? "synthetic-test-password";
        var profile = new H3cDeviceProfileSnapshot(Guid.NewGuid().ToString("N"), "127.0.0.1", port,
            "reader", "TEST_CREDENTIAL", pin, true, 0, 0);
        var session = new H3cSshDeviceSession(profile, new FixedH3cCredentialProvider(password),
            new SshNetH3cSshTransportFactory());
        var sessionsBefore = H3cCollectionMetrics.Snapshot().ActiveSshSessions;
        try
        {
            switch (mode)
            {
                case "success":
                    await session.ConnectAsync(CancellationToken.None);
                    var output = await session.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None);
                    Assert(output == "H3C Comware synthetic response\n", "SSH.NET Exec Channel should return the simulated Comware response intact");
                    var connectedMetrics = H3cCollectionMetrics.Snapshot();
                    Assert(connectedMetrics.ConnectAuthenticationCount > 0 && connectedMetrics.ConnectAuthenticationTotalMs > 0 &&
                           connectedMetrics.ConnectionSetupCount > 0 && connectedMetrics.AuthenticationCount > 0 &&
                           connectedMetrics.ConnectionSetupTotalMs > 0 && connectedMetrics.AuthenticationTotalMs > 0,
                        "SSH protocol telemetry should retain combined connect/auth timing and report connection/key-exchange and post-pin authentication time separately");
                    Console.WriteLine("PASS: SSH.NET wire authentication, pinned host key and Exec Channel output");
                    break;
                case "host-key":
                    try
                    {
                        await session.ConnectAsync(CancellationToken.None);
                        throw new InvalidOperationException("an unknown SSH host key must be rejected");
                    }
                    catch (H3cHostKeyMismatchException) { }
                    Console.WriteLine("PASS: SSH.NET wire host-key mismatch was rejected");
                    break;
                case "auth":
                    try
                    {
                        await session.ConnectAsync(CancellationToken.None);
                        throw new InvalidOperationException("bad SSH credentials must be rejected");
                    }
                    catch (H3cAuthenticationFailedException) { }
                    Console.WriteLine("PASS: SSH.NET wire password authentication failure was rejected");
                    break;
                case "timeout":
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
                    {
                        var started = Stopwatch.GetTimestamp();
                        try
                        {
                            await session.ConnectAsync(timeout.Token);
                            throw new InvalidOperationException("a silent SSH peer must not complete a connection");
                        }
                        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
                        Assert(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3),
                            "SSH connect cancellation must finish promptly when the peer never sends its banner");
                    }
                    Console.WriteLine("PASS: SSH.NET wire connection cancellation released a silent peer");
                    break;
                case "disconnect":
                    await session.ConnectAsync(CancellationToken.None);
                    try
                    {
                        await session.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None);
                        throw new InvalidOperationException("a reset after command output must not report complete collection");
                    }
                    catch (H3cPartialOutputException ex)
                    {
                        Assert(ex.FailureCode == "H3C_DISCONNECTED_PARTIAL" &&
                               ex.Output == "partial Comware response\n",
                            "SSH reset should retain and classify the received command output prefix");
                    }
                    Console.WriteLine("PASS: SSH.NET wire reset preserved only the bounded partial command output");
                    break;
                case "concurrent":
                    var second = new H3cSshDeviceSession(profile, new FixedH3cCredentialProvider(password),
                        new SshNetH3cSshTransportFactory());
                    try
                    {
                        await Task.WhenAll(session.ConnectAsync(CancellationToken.None),
                            second.ConnectAsync(CancellationToken.None));
                        Assert(H3cCollectionMetrics.Snapshot().ActiveSshSessions == sessionsBefore + 2,
                            "two independent SSH.NET sessions should be tracked concurrently");
                        var outputs = await Task.WhenAll(
                            session.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None),
                            second.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None));
                        Assert(outputs.All(value => value == "H3C Comware synthetic response\n"),
                            "concurrent SSH exec channels should retain both synthetic responses");
                    }
                    finally { await second.DisposeAsync(); }
                    Console.WriteLine("PASS: two concurrent SSH.NET wire sessions completed and released");
                    break;
            }
        }
        finally { await session.DisposeAsync(); }

        Assert(H3cCollectionMetrics.Snapshot().ActiveSshSessions == sessionsBefore,
            "SSH.NET protocol simulation must not leave an active session behind");
    }

    public static async Task RunArchiveTestsAsync()
    {
        VerifiesArchiveResourceBoundariesAndAdmissionRollback();
        await VerifiesArchiveDiskAdmissionAndRuntimeFailure();
        await VerifiesArchiveCompletionCancellationReleasesInputAsync();
        await VerifiesArchiveTerminalAdmissionConsistencyAsync();
        await VerifiesArchiveLifecycleCleanupAsync();
        Console.WriteLine("PASS: archive lifecycle and admission tests");
    }

    public static async Task RunAsync()
    {
        await EnforcesQueueAndConcurrencyLimits();
        await CancelsQueuedAndRunningRequests();
        await TimesOutAndDisposesSession();
        await ReportsConnectionFailuresAndDisposesSession();
        await StopsActiveSessionsDuringSchedulerShutdown();
        await PersistsDefinitionsRunsAndRestartState();
        VerifiesH3cGoldenParsersPinsAndAlgorithmPolicy();
        VerifiesDeviceInventoryMappingAndCounts();
        VerifiesDeviceManagementPhase1();
        await VerifiesDeviceManagementSafetyBoundaries();
        await VerifiesDeviceManagementPhase2A();
        P2B1ContractTests.Run();
        P2B2SchemaV13Tests.Run();
        P2B5SiteScopeTests.Run();
        H3cCapabilityTests.Run();
        await P2B5RailReferenceTests.RunAsync();
        SqliteSecurityTests.Run();
        P2B3DiffContractTests.Run();
        await VerifiesH3cOutputCollectionLimitsAndDisconnects();
        await VerifiesRawPublishFailureIsRetryable();
        await VerifiesPreservedRawRejectsFurtherOperations();
        await VerifiesConcurrentRawSealIsSerialized();
        VerifiesCatalogRegistrationRejectsMetadataConflict();
        VerifiesLocalSecurityTokenRotationAndDpapiBoundary();
        await PersistsH3cTaskOutputAsVerifiedRaw();
        await ReconcilesLegacyWindowsRawPathAliases();
        await RejectsAmbiguousH3cRecoveryAtomically();
        VerifiesExistingV1CatalogUpgradeReadsEachRawOnce();
        VerifiesIncrementalRecoverySkipsLargeTrustedHistory();
        VerifiesMaintenanceFailureCanResolveAfterVerifiedRetry();
        VerifiesQuarantineDispositionPreservesRawAndReopensHealthOnlyForAuditedIssues();
        VerifiesFlushWaitDeadlineIsClamped();
        VerifiesIngressQueueHasAPayloadByteLimit();
        await MeasuresSimulatedDeviceConcurrencyWithSyslog();
        await VerifiesSparseSyslogFlushesWithinConfiguredWindow();
        VerifiesSqliteWriteFailureRetainsRawFile();
        VerifiesSqliteBusyIsVisibleAndPreservesRawFile();
        VerifiesRawPermissionRevocationIsVisible();
        await VerifiesCaptureDiskFullRetainsPartialRawAndFaults();
        await MeasuresConcurrentSimulatedDeviceAndSyslogLoad();
        VerifiesArchiveResourceBoundariesAndAdmissionRollback();
        await VerifiesArchiveDiskAdmissionAndRuntimeFailure();
        await VerifiesArchiveCompletionCancellationReleasesInputAsync();
        await VerifiesArchiveTerminalAdmissionConsistencyAsync();
        await VerifiesArchiveLifecycleCleanupAsync();
        VerifiesRollingLogSizeAndRetention();
        await TerminalCoreTests.RunAsync();
        RejectsInvalidRequests();
        Console.WriteLine("PASS: bounded/persisted scheduling, H3C parser and Raw output, migration, incremental recovery, SQLite/permission fault injection, queue rejection, timeout, cancellation and session lifecycle");
    }

    private static async Task VerifiesRawPublishFailureIsRetryable()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-raw-seal-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            var writer = new RawSegmentWriterFactory().Create(catalog);
            await writer.AppendAsync(new SyslogPacket(7100, "127.0.0.1", 5514, new byte[] { 0, 0xff, 0x0d, 0x0a }));
            using (var db = OpenCatalog(root))
            using (var trigger = db.CreateCommand())
            {
                trigger.CommandText = "CREATE TRIGGER fail_raw_seal BEFORE INSERT ON raw_segments BEGIN SELECT RAISE(ABORT,'injected seal failure'); END";
                trigger.ExecuteNonQuery();
            }

            AssertThrows<SqliteException>(() => writer.SealAsync().GetAwaiter().GetResult());
            var published = Directory.GetFiles(catalog.RawRoot, "*.ndjson", SearchOption.AllDirectories);
            Assert(published.Length == 1 && !Directory.GetFiles(catalog.RawRoot, "*.open", SearchOption.AllDirectories).Any(),
                "a Catalog failure after publish must retain one final Raw file and no stale .open file");
            var rawBeforeRetry = File.ReadAllBytes(published[0]);
            using (var db = OpenCatalog(root))
            using (var drop = db.CreateCommand())
            {
                drop.CommandText = "DROP TRIGGER fail_raw_seal";
                drop.ExecuteNonQuery();
            }
            await writer.SealAsync();
            await writer.SealAsync();
            Assert(rawBeforeRetry.SequenceEqual(File.ReadAllBytes(published[0])),
                "retry after Catalog failure must not rewrite published Raw bytes");
            Assert(catalog.GetRawCounts().Finalized == 1, "retry after Catalog failure must create one finalized Catalog row");
        }
        finally { await DeleteTestDirectoryWithRetryAsync(root); }
    }

    private static async Task VerifiesConcurrentRawSealIsSerialized()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-raw-seal-concurrent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            var writer = new RawSegmentWriterFactory().Create(catalog);
            for (var index = 0; index < 32; index++)
                await writer.AppendAsync(new SyslogPacket(7200 + index, "127.0.0.1", 5514, new byte[index % 31 + 1]));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => writer.SealAsync()));
            Assert(Directory.GetFiles(catalog.RawRoot, "*.ndjson", SearchOption.AllDirectories).Length == 1,
                "concurrent SealAsync calls must publish exactly one Raw segment");
            Assert(catalog.GetRawCounts().Finalized == 1, "concurrent SealAsync calls must create one Catalog row");
        }
        finally { await DeleteTestDirectoryWithRetryAsync(root); }
    }

    private static async Task VerifiesPreservedRawRejectsFurtherOperations()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-raw-preserved-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            var writer = new RawSegmentWriter(catalog, path => new FlushFailureStream(
                new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32768, FileOptions.Asynchronous)));
            await writer.AppendAsync(new SyslogPacket(7250, "127.0.0.1", 5514, Encoding.UTF8.GetBytes("preserve-state")));
            AssertThrows<IOException>(() => writer.FlushAsync().GetAwaiter().GetResult());
            AssertThrows<IOException>(() => writer.PreserveOpenAsync().GetAwaiter().GetResult());

            var openPath = Directory.GetFiles(catalog.RawRoot, "*.open", SearchOption.AllDirectories).Single();
            var preservedBytes = File.ReadAllBytes(openPath);
            Assert(preservedBytes.Length > 0, "a failed flush must retain bytes already written to the .open file");
            AssertThrows<InvalidOperationException>(() => writer.AppendAsync(
                new SyslogPacket(7251, "127.0.0.1", 5514, Encoding.UTF8.GetBytes("must-reject")))
                .GetAwaiter().GetResult());
            AssertThrows<InvalidOperationException>(() => writer.FlushAsync().GetAwaiter().GetResult());
            AssertThrows<InvalidOperationException>(() => writer.SealAsync().GetAwaiter().GetResult());
            Assert(preservedBytes.SequenceEqual(File.ReadAllBytes(openPath)),
                "operations after PreserveOpenAsync must not change the retained Raw bytes");
            Assert(catalog.GetRawCounts().Finalized == 0,
                "a preserved incomplete segment must not create a FINALIZED Catalog row");

            var recovery = catalog.RecoverRawSegments();
            Assert(recovery.RecoveredOpenFiles == 1 && recovery.FailedFiles == 0,
                $"G1 recovery should reconcile the preserved .open file: {recovery}");
            Assert(catalog.GetRawCounts().Incomplete == 1 && catalog.GetRawCounts().Finalized == 0,
                "recovered preserved content must remain INCOMPLETE");
        }
        finally { await DeleteTestDirectoryWithRetryAsync(root); }
    }

    private static void VerifiesCatalogRegistrationRejectsMetadataConflict()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-catalog-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            const string relative = "raw/syslog/catalog-conflict.ndjson";
            var sha = new string('a', 64);
            catalog.RegisterFinalizedSegment(relative, 1, 7300, 7300, sha, 42);
            AssertThrows<InvalidOperationException>(() => catalog.RegisterFinalizedSegment(relative, 2, 7300, 7300, sha, 43));
            using var db = OpenCatalog(root);
            using var command = db.CreateCommand();
            command.CommandText = "SELECT record_count,byte_count,sha256,state FROM raw_segments WHERE relative_path=$path";
            command.Parameters.AddWithValue("$path", relative);
            using var reader = command.ExecuteReader();
            Assert(reader.Read() && reader.GetInt64(0) == 1 && reader.GetInt64(1) == 42 &&
                   reader.GetString(2) == sha && reader.GetString(3) == "FINALIZED",
                "a conflicting registration must preserve the original Catalog evidence");
        }
        finally { Directory.Delete(root, recursive: true); }
    }


    private static void VerifiesArchiveResourceBoundariesAndAdmissionRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-archive-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new RuntimeSettings
            {
                DataRoot = root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024, ArchiveMaxFileEntriesPerJob = 2,
                ArchiveMaxDirectoryDepth = 1, ArchiveMaxMetadataEntriesPerJob = 16,
                ArchiveMaxMetadataBytesPerJob = 16 * 512, ArchiveMaxActiveJobsPerUser = 1,
                ArchiveMaxQueuedJobs = 2,
                ArchiveMaxAggregateReservedBytes = 1024 * 1024
            };
            var raw = Path.Combine(root, "raw");
            Directory.CreateDirectory(Path.Combine(raw, "deep", "level", "too-deep"));
            File.WriteAllText(Path.Combine(raw, "deep", "level", "too-deep", "value.txt"), "value");
            File.WriteAllText(Path.Combine(raw, "one.txt"), "one");
            File.WriteAllText(Path.Combine(raw, "two.txt"), "two");
            File.WriteAllText(Path.Combine(raw, "three.txt"), "three");
            var files = new FileManagerService(settings);
            var identity = new WebUserIdentity("admin", "admin", "ADMIN", "session-a");
            AssertThrows<ArchiveRejectedException>(() => files.Expand(
                new ArchiveSelection("raw", ["deep"]), new ArchiveExpansionBudget(settings),
                new HashSet<string>(StringComparer.Ordinal), CancellationToken.None));
            AssertThrows<ArchiveRejectedException>(() => files.Expand(
                new ArchiveSelection("raw", [""]), new ArchiveExpansionBudget(settings),
                new HashSet<string>(StringComparer.Ordinal), CancellationToken.None));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                AssertThrows<OperationCanceledException>(() => files.Expand(
                    new ArchiveSelection("raw", ["deep"]), new ArchiveExpansionBudget(settings),
                    new HashSet<string>(StringComparer.Ordinal), cancelled.Token));
            }

            var archive = new FileArchiveService(files, settings);
            var request = new ArchiveRequest([new ArchiveSelection("raw", ["one.txt"])]);
            var first = archive.Create(request, identity);
            AssertThrows<ArchiveRejectedException>(() => archive.Create(request, identity));
            var other = archive.Create(request, new WebUserIdentity("other", "other", "ADMIN", "session-b"));
            AssertThrows<ArchiveRejectedException>(() => archive.Create(request,
                new WebUserIdentity("third", "third", "ADMIN", "session-c")));
            Assert(archive.Cancel(first.Id, identity), "queued archive cancellation should be accepted");
            Assert(archive.Cancel(first.Id, identity), "queued archive cancellation should be idempotent");
            var afterCancel = archive.Create(request, identity);
            Assert(afterCancel.State == "QUEUED", "a cancelled queued job must release its admission slot");
            archive.Cancel(afterCancel.Id, identity);
            archive.Cancel(other.Id, new WebUserIdentity("other", "other", "ADMIN", "session-b"));

            var concurrent = new FileArchiveService(files, new RuntimeSettings
            {
                DataRoot = root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024, ArchiveMaxFileEntriesPerJob = 2,
                ArchiveMaxQueuedJobs = 2, ArchiveMaxActiveJobsPerUser = 1,
                ArchiveMaxAggregateReservedBytes = 1024 * 1024
            });
            var accepted = new System.Collections.Concurrent.ConcurrentBag<ArchiveJobSnapshot>();
            Parallel.For(0, 16, index =>
            {
                try
                {
                    accepted.Add(concurrent.Create(request,
                        new WebUserIdentity($"parallel-{index}", $"parallel-{index}", "ADMIN", $"session-{index}")));
                }
                catch (ArchiveRejectedException) { }
            });
            Assert(accepted.Count > 0 && accepted.Count <= 3 && accepted.Select(job => job.Id).Distinct().Count() == accepted.Count,
                "simultaneous archive admission must remain bounded and must not duplicate jobs");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifiesArchiveDiskAdmissionAndRuntimeFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-archive-disk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var minimum = 1024L;
            var settings = new RuntimeSettings
            {
                DataRoot = root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024, ArchiveMinFreeDiskBytes = minimum,
                ArchiveMaxActiveJobsPerUser = 1, ArchiveMaxQueuedJobs = 2,
                ArchiveMaxAggregateReservedBytes = 1024 * 1024
            };
            var raw = Path.Combine(root, "raw");
            Directory.CreateDirectory(raw);
            File.WriteAllText(Path.Combine(raw, "admission.txt"), "admission");
            var files = new FileManagerService(settings);
            var identity = new WebUserIdentity("admin", "admin", "ADMIN", "disk-admission");
            var request = new ArchiveRequest([new ArchiveSelection("raw", ["admission.txt"])]);
            var disk = new FakeArchiveDiskSpaceProvider(minimum - 1);
            var archive = new FileArchiveService(files, settings, disk);
            var beforeAdmission = archive.AdmissionState;

            ArchiveRejectedException? low = null;
            try { archive.Create(request, identity); }
            catch (ArchiveRejectedException ex) { low = ex; }
            Assert(low is not null && low.Code == "ARCHIVE_DISK_SPACE_LOW" && low.StatusCode == 503,
                "low archive disk admission must return the stable 503 error");
            Assert(files.ExpansionCallCount == 0, "low archive disk admission must reject before directory expansion");
            Assert(archive.AdmissionState == beforeAdmission, "low archive disk admission must release every reservation and create no job");
            Assert(disk.Paths.Count == 1 && Path.GetFullPath(disk.Paths[0]) == Path.GetFullPath(Path.Combine(root, "archive-tmp")),
                "archive admission must inspect the archive temporary volume");
            Assert(!Directory.Exists(Path.Combine(root, "archive-tmp")), "low archive admission must not create the archive temp directory");

            disk.Available = null;
            ArchiveRejectedException? unavailable = null;
            try { archive.Create(request, identity); }
            catch (ArchiveRejectedException ex) { unavailable = ex; }
            Assert(unavailable is not null && unavailable.Code == "ARCHIVE_STORAGE_UNAVAILABLE" && unavailable.StatusCode == 503,
                "unverifiable archive storage must fail closed with a diagnostic error");
            Assert(files.ExpansionCallCount == 0, "unverifiable archive storage must reject before directory expansion");
            Assert(archive.AdmissionState == beforeAdmission, "unverifiable archive storage must not leak admission state");

            disk.Available = minimum;
            var atBoundary = archive.Create(request, identity);
            Assert(atBoundary.State == "QUEUED", "available space exactly at the configured threshold must be admitted");
            archive.Cancel(atBoundary.Id, identity);
            disk.Available = minimum + 1;
            var recovered = archive.Create(request, identity);
            Assert(recovered.State == "QUEUED", "disk recovery must release all failed admission reservations");
            archive.Cancel(recovered.Id, identity);
            Assert(!Directory.Exists(Path.Combine(root, "archive-tmp")), "admission-only disk checks must not create temp output");

            var runRoot = Path.Combine(root, "runtime");
            var runSettings = new RuntimeSettings
            {
                DataRoot = runRoot, WebPort = 18772, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024, ArchiveMinFreeDiskBytes = minimum,
                ArchiveMaxActiveJobsPerUser = 1, ArchiveMaxQueuedJobs = 1,
                ArchiveTestCopyDelayMilliseconds = 1, ArchiveMaxInputBytesPerJob = 32 * 1024 * 1024
            };
            var runRaw = Path.Combine(runRoot, "raw");
            Directory.CreateDirectory(runRaw);
            var large = new byte[8 * 1024 * 1024];
            RandomNumberGenerator.Fill(large);
            File.WriteAllBytes(Path.Combine(runRaw, "runtime.bin"), large);
            var runFiles = new FileManagerService(runSettings);
            var runDisk = new FakeArchiveDiskSpaceProvider(minimum + 1);
            var running = new FileArchiveService(runFiles, runSettings, runDisk);
            await running.StartAsync(CancellationToken.None);
            try
            {
                var runningJob = running.Create(new ArchiveRequest([new ArchiveSelection("raw", ["runtime.bin"])]), identity);
                await WaitUntil(() => running.Get(runningJob.Id, identity)?.State == "RUNNING", TimeSpan.FromSeconds(3));
                runDisk.Available = minimum - 1;
                await WaitUntil(() => running.Get(runningJob.Id, identity)?.State == "FAILED", TimeSpan.FromSeconds(10));
                var failed = running.Get(runningJob.Id, identity);
                Assert(failed is not null && failed.ErrorCode == "ARCHIVE_DISK_SPACE_LOW", "a runtime disk drop must fail the ZIP safely");
                Assert(running.AdmissionState is { ActiveJobs: 0, QueuedJobs: 0, RunningJobs: 0, ReservedInputBytes: 0 },
                    $"FAILED must not be externally visible before admission release: {running.AdmissionState}");
                Assert(!File.Exists(Path.Combine(runRoot, "archive-tmp", runningJob.Id + ".zip.part")), "runtime disk failure must delete the partial ZIP");
                Assert(!File.Exists(Path.Combine(runRoot, "archive-tmp", runningJob.Id + ".zip")), "runtime disk failure must not publish a partial ZIP");
                runDisk.Available = minimum + 1;
                var retry = running.Create(new ArchiveRequest([new ArchiveSelection("raw", ["runtime.bin"])]), identity);
                Assert(retry.State is "QUEUED" or "RUNNING", "runtime disk failure must release the user admission slot");
                running.Cancel(retry.Id, identity);
            }
            finally { await running.StopAsync(CancellationToken.None); }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifiesArchiveCompletionCancellationReleasesInputAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-archive-completion-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        FileArchiveService? service = null;
        try
        {
            var settings = new RuntimeSettings
            {
                DataRoot = root, WebPort = 18774, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024, ArchiveMinFreeDiskBytes = 1,
                ArchiveMaxActiveJobsPerUser = 1, ArchiveMaxQueuedJobs = 1,
                ArchiveMaxAggregateReservedBytes = 1024 * 1024
            };
            var raw = Path.Combine(root, "raw");
            Directory.CreateDirectory(raw);
            File.WriteAllText(Path.Combine(raw, "completion-race.txt"), "completion-race");
            service = new FileArchiveService(new FileManagerService(settings), settings,
                new FakeArchiveDiskSpaceProvider(1024));
            var identity = new WebUserIdentity("archive-race-user", "archive-race-user", "ADMIN", "archive-race");
            ArchiveTestHooks.BeforePublishCompleted = cancel =>
            {
                entered.TrySetResult(true);
                cancel();
                release.Task.GetAwaiter().GetResult();
            };

            await service.StartAsync(CancellationToken.None);
            var job = service.Create(new ArchiveRequest([new ArchiveSelection("raw", ["completion-race.txt"])]), identity);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult(true);
            await WaitUntil(() => service.Get(job.Id, identity)?.State == "CANCELLED", TimeSpan.FromSeconds(5));
            Assert(service.AdmissionState is { ActiveJobs: 0, QueuedJobs: 0, RunningJobs: 0, ReservedInputBytes: 0 },
                $"cancelled completion fallback must release aggregate input reservation: {service.AdmissionState}");
            Assert(!File.Exists(Path.Combine(root, "archive-tmp", job.Id + ".zip")),
                "cancelled completion fallback must not retain a published ZIP");
            var retry = service.Create(new ArchiveRequest([new ArchiveSelection("raw", ["completion-race.txt"])]), identity);
            Assert(retry.State is "QUEUED" or "RUNNING", "a cancelled completion must release the user's admission slot");
            service.Cancel(retry.Id, identity);
            Console.WriteLine("ARCHIVE_COMPLETION_CANCEL_RESERVATION_RACE=PASS");
        }
        finally
        {
            release.TrySetResult(true);
            ArchiveTestHooks.BeforePublishCompleted = null;
            if (service is not null) await service.StopAsync(CancellationToken.None);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task VerifiesArchiveLifecycleCleanupAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-archive-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FileArchiveService? service = null;
        try
        {
            var settings = new RuntimeSettings
            {
                DataRoot = root, WebPort = 18773, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024, ArchiveMinFreeDiskBytes = 1,
                ArchiveCleanupIntervalSeconds = 1, ArchiveMaxRetainedTerminalJobs = 1,
                ArchiveMaxActiveJobsPerUser = 2, ArchiveMaxQueuedJobs = 2,
                ArchiveMaxAggregateReservedBytes = 1024 * 1024
            };
            var raw = Path.Combine(root, "raw");
            var temp = Path.Combine(root, "archive-tmp");
            Directory.CreateDirectory(raw);
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(raw, "lifecycle.txt"), "lifecycle");
            var startupOrphan = Path.Combine(temp, "startup-orphan.zip.part");
            File.WriteAllText(startupOrphan, "orphan");
            File.SetLastWriteTimeUtc(startupOrphan, DateTime.UtcNow.AddDays(-2));

            var settingsFiles = new FileManagerService(settings);
            var disk = new FakeArchiveDiskSpaceProvider(1024);
            service = new FileArchiveService(settingsFiles, settings, disk);
            var identity = new WebUserIdentity("admin", "admin", "ADMIN", "lifecycle");
            await service.StartAsync(CancellationToken.None);
            await WaitUntil(() => !File.Exists(startupOrphan), TimeSpan.FromSeconds(3));
            Console.WriteLine("STARTUP_ORPHAN_CLEANUP=PASS");

            var idleOrphan = Path.Combine(temp, "idle-orphan.zip.part");
            File.WriteAllText(idleOrphan, "orphan");
            File.SetLastWriteTimeUtc(idleOrphan, DateTime.UtcNow.AddDays(-2));
            await WaitUntil(() => !File.Exists(idleOrphan), TimeSpan.FromSeconds(5));
            Console.WriteLine("PERIODIC_CLEANUP_TIME_ELAPSED=PASS");
            Console.WriteLine("IDLE_CLEANUP_TIME_ELAPSED=PASS");

            var request = new ArchiveRequest([new ArchiveSelection("raw", ["lifecycle.txt"])]);
            var completed = service.Create(request, identity);
            await WaitUntil(() => service.Get(completed.Id, identity)?.State == "COMPLETED", TimeSpan.FromSeconds(5));
            var download = service.OpenDownload(completed.Id, identity);
            Assert(download is not null, "a completed archive must provide a download lease");
            var trigger = service.Create(request, identity);
            service.Cancel(trigger.Id, identity);
            Assert(service.Get(completed.Id, identity) is not null,
                "maintenance must not remove an archive while its download lease is active");
            download!.Stream.Dispose();
            var releaseTrigger = service.Create(request, identity);
            service.Cancel(releaseTrigger.Id, identity);
            Assert(service.Get(completed.Id, identity) is null,
                "maintenance must remove a retained terminal archive after its download lease is released");
            Console.WriteLine("DOWNLOAD_CLEANUP_RACE=PASS");
            Console.WriteLine("RETAINED_JOB_LIMIT_EXHAUSTION=PASS");
            await service.StopAsync(CancellationToken.None);
            service = null;
        }
        finally
        {
            if (service is not null) await service.StopAsync(CancellationToken.None);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task VerifiesArchiveTerminalAdmissionConsistencyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-archive-terminal-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FileArchiveService? service = null;
        try
        {
            const long minimum = 1024;
            var settings = new RuntimeSettings
            {
                DataRoot = root, WebPort = 18774, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024, ArchiveMinFreeDiskBytes = minimum,
                ArchiveMaxActiveJobsPerUser = 1, ArchiveMaxQueuedJobs = 1,
                ArchiveMaxAggregateReservedBytes = 32 * 1024 * 1024,
                ArchiveTestCopyDelayMilliseconds = 1
            };
            var raw = Path.Combine(root, "raw");
            Directory.CreateDirectory(raw);
            File.WriteAllBytes(Path.Combine(raw, "terminal.bin"), new byte[8 * 1024 * 1024]);
            var files = new FileManagerService(settings);
            var disk = new FakeArchiveDiskSpaceProvider(minimum + 1);
            service = new FileArchiveService(files, settings, disk);
            var identity = new WebUserIdentity("archive-order-user", "archive-order-user", "ADMIN", "archive-order-session");
            var request = new ArchiveRequest([new ArchiveSelection("raw", ["terminal.bin"])]);
            await service.StartAsync(CancellationToken.None);

            var failed = service.Create(request, identity);
            await WaitUntil(() => service.Get(failed.Id, identity)?.State == "RUNNING", TimeSpan.FromSeconds(3));
            disk.Available = minimum - 1;
            await WaitUntil(() => service.Get(failed.Id, identity)?.State == "FAILED", TimeSpan.FromSeconds(10));
            Assert(service.AdmissionState is { ActiveJobs: 0, QueuedJobs: 0, RunningJobs: 0, ReservedInputBytes: 0 },
                $"FAILED must be published only after admission release: {service.AdmissionState}");
            disk.Available = minimum + 1;
            var immediateRetry = service.Create(request, identity);
            Assert(immediateRetry.State is "QUEUED" or "RUNNING", "FAILED must allow an immediate same-user retry");
            service.Cancel(immediateRetry.Id, identity);
            await WaitUntil(() => service.Get(immediateRetry.Id, identity)?.State == "CANCELLED");
            Assert(service.AdmissionState.ActiveJobs == 0, $"queued cancellation must release admission: {service.AdmissionState}");

            var completed = service.Create(request, identity);
            await WaitUntil(() => service.Get(completed.Id, identity)?.State == "COMPLETED", TimeSpan.FromSeconds(10));
            Assert(service.AdmissionState is { ActiveJobs: 0, QueuedJobs: 0, RunningJobs: 0 },
                $"COMPLETED must be published only after active admission release: {service.AdmissionState}");
            Assert(service.AdmissionState.ReservedInputBytes == completed.TotalBytes,
                $"COMPLETED may retain only its bounded retention reservation: {service.AdmissionState}");
            var afterCompleted = service.Create(request, identity);
            Assert(afterCompleted.State is "QUEUED" or "RUNNING", "COMPLETED must allow a subsequent same-user create");
            service.Cancel(afterCompleted.Id, identity);
            await WaitUntil(() => service.Get(afterCompleted.Id, identity)?.State == "CANCELLED");

            var runningCancel = service.Create(request, identity);
            await WaitUntil(() => service.Get(runningCancel.Id, identity)?.State == "RUNNING", TimeSpan.FromSeconds(3));
            service.Cancel(runningCancel.Id, identity);
            await WaitUntil(() => service.Get(runningCancel.Id, identity)?.State == "CANCELLED", TimeSpan.FromSeconds(5));
            Assert(service.AdmissionState is { ActiveJobs: 0, QueuedJobs: 0, RunningJobs: 0 } &&
                   service.AdmissionState.ReservedInputBytes == completed.TotalBytes,
                $"RUNNING cancellation must release admission before CANCELLED: {service.AdmissionState}");
            Assert(!File.Exists(Path.Combine(root, "archive-tmp", runningCancel.Id + ".zip.part")),
                "running cancellation must remove the partial archive");
            await service.StopAsync(CancellationToken.None);
            service = null;
            Console.WriteLine("ARCHIVE_TERMINAL_ADMISSION_ORDER=PASS");

            var stopRoot = Path.Combine(root, "stop-race");
            Directory.CreateDirectory(Path.Combine(stopRoot, "raw"));
            File.WriteAllBytes(Path.Combine(stopRoot, "raw", "terminal.bin"), new byte[8 * 1024 * 1024]);
            var stopSettings = new RuntimeSettings
            {
                DataRoot = stopRoot, WebPort = 18775, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024, ArchiveMinFreeDiskBytes = minimum,
                ArchiveMaxActiveJobsPerUser = 1, ArchiveMaxQueuedJobs = 1,
                ArchiveMaxAggregateReservedBytes = 32 * 1024 * 1024,
                ArchiveTestCopyDelayMilliseconds = 1
            };
            var stopping = new FileArchiveService(new FileManagerService(stopSettings), stopSettings, new FakeArchiveDiskSpaceProvider(minimum + 1));
            var stopIdentity = new WebUserIdentity("archive-stop-user", "archive-stop-user", "ADMIN", "archive-stop-session");
            await stopping.StartAsync(CancellationToken.None);
            var stopJob = stopping.Create(request, stopIdentity);
            await WaitUntil(() => stopping.Get(stopJob.Id, stopIdentity)?.State == "RUNNING", TimeSpan.FromSeconds(3));
            await stopping.StopAsync(CancellationToken.None);
            Assert(stopping.Get(stopJob.Id, stopIdentity)?.State == "CANCELLED" &&
                   stopping.AdmissionState is { ActiveJobs: 0, QueuedJobs: 0, RunningJobs: 0, ReservedInputBytes: 0 },
                $"service stop must converge cancellation and admission release: {stopping.AdmissionState}");
            Console.WriteLine("ARCHIVE_SERVICE_STOP_ADMISSION_ORDER=PASS");
        }
        finally
        {
            if (service is not null) await service.StopAsync(CancellationToken.None);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void VerifiesH3cGoldenParsersPinsAndAlgorithmPolicy()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c");
        var version = H3cComwareParser.ParseVersion(File.ReadAllText(Path.Combine(fixtures, "display_version.txt")));
        Assert(version.Recognized && version.Vendor == "H3C Comware" && version.Version == "7.1.070" &&
               version.Release == "6607P20", version.ToString()!);

        var interfaces = H3cComwareParser.ParseInterfaceBrief(File.ReadAllText(Path.Combine(fixtures, "display_interface_brief.txt")));
        Assert(interfaces.Recognized && interfaces.Interfaces.Count == 4, interfaces.ToString()!);
        Assert(interfaces.Interfaces.Single(x => x.InterfaceName == "GE1/0/1").IpAddress == "192.0.2.10",
            "route-mode Comware interface should parse its IP address");
        var bridgePort = interfaces.Interfaces.Single(x => x.InterfaceName == "GE1/0/2");
        Assert(bridgePort.LinkStatus == "DOWN" && bridgePort.Speed == "auto" && bridgePort.Vlan == "20",
            bridgePort.ToString()!);

        var v9InterfaceBrief = string.Join(Environment.NewLine, new[]
        {
            "Brief information on interfaces in route mode:",
            "Link: ADM - administratively down; Stby - standby",
            "Protocol: (s) - spoofing",
            "Interface                         Link Protocol Primary IP        Description",
            "Vlan250                           UP   UP       10.92.250.251      AC",
            "Brief information on interfaces in bridge mode:",
            "Link: ADM - administratively down; Stby - standby",
            "Speed: (a) - auto",
            "Duplex: (a)/A - auto; H - half; F - full",
            "Type: A - access; T - trunk; H - hybrid",
            "Interface                         Link Speed     Duplex Type PVID Description",
            "GE1/0/1                           UP   1000M     F      T    1    uplink"
        });
        var v9Interfaces = H3cComwareParser.ParseInterfaceBrief(v9InterfaceBrief);
        Assert(v9Interfaces.Recognized && v9Interfaces.Interfaces.Count == 2,
            "Comware V9 explanation rows must not become interfaces: " + v9Interfaces);
        Assert(v9Interfaces.Interfaces.All(x => x.InterfaceName is not ("Protocol:" or "Speed:" or "Duplex:" or "Type:")),
            "Comware V9 explanation labels must be excluded from interface results");

        var fingerprint = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()).TrimEnd('=');
        Assert(H3cHostKeyFingerprint.Matches("SHA256:" + fingerprint, fingerprint), "SHA256 pin should match SSH.NET fingerprint form");
        Assert(!H3cHostKeyFingerprint.Matches("SHA256:" + fingerprint, new string('A', 43)), "wrong host key must not match");
        AssertThrows<ArgumentException>(() => H3cHostKeyFingerprint.Normalize("MD5:aa:bb"));

        var connectionInfo = new Renci.SshNet.ConnectionInfo("192.0.2.1", 22, "lab",
            new Renci.SshNet.PasswordAuthenticationMethod("lab", "unit-test-placeholder"));
        H3cSshAlgorithmPolicy.Apply(connectionInfo);
        Assert(!connectionInfo.KeyExchangeAlgorithms.ContainsKey("diffie-hellman-group1-sha1") &&
               !connectionInfo.Encryptions.ContainsKey("3des-cbc") &&
               !connectionInfo.HmacAlgorithms.ContainsKey("hmac-sha1") &&
               !connectionInfo.HostKeyAlgorithms.ContainsKey("ssh-rsa"), "weak SSH algorithms must be removed from the connection allowlist");
        var legacyConnectionInfo = new Renci.SshNet.ConnectionInfo("192.0.2.1", 22, "lab",
            new Renci.SshNet.PasswordAuthenticationMethod("lab", "unit-test-placeholder"));
        H3cSshAlgorithmPolicy.Apply(legacyConnectionInfo, allowLegacyAlgorithms: true);
        Assert(legacyConnectionInfo.KeyExchangeAlgorithms.ContainsKey("diffie-hellman-group14-sha1") ||
               legacyConnectionInfo.Encryptions.ContainsKey("aes128-cbc") ||
               legacyConnectionInfo.HmacAlgorithms.ContainsKey("hmac-sha1") ||
               legacyConnectionInfo.HostKeyAlgorithms.ContainsKey("ssh-rsa"),
            "device-scoped legacy compatibility should retain only explicitly allowed legacy algorithms");

        Assert(H3cComwareParser.Parse(H3cCommandPolicy.DisplayVersion, string.Empty).Status == "EMPTY",
            "empty command output is a capture result, not a parser success");
        Assert(H3cComwareParser.Parse(H3cCommandPolicy.DisplayVersion, "vendor format changed").Status == "UNRECOGNIZED",
            "unknown Comware output must stay visibly unparsed");
        Assert(H3cComwareParser.ContainsPaginationPrompt("\u001b[7m---- More ----\u001b[0m"),
            "ANSI decorated Comware paging prompt should be detected");
        Assert(!H3cComwareParser.ContainsPaginationPrompt("---- More ----\b\b\b\b\b\b\b\bREADY"),
            "terminal backspaces must be applied before paging prompt detection");
    }

    private static async Task VerifiesH3cOutputCollectionLimitsAndDisconnects()
    {
        const int boundary = H3cRawOutput.MaximumBytes;
        var exact = Enumerable.Repeat((byte)'x', boundary).ToArray();
        var exactOutput = await H3cCommandOutputCollector.CollectAsync(new MemoryStream(exact),
            new MemoryStream(), Task.CompletedTask, boundary, () => throw new InvalidOperationException("unexpected abort"));
        Assert(Encoding.UTF8.GetByteCount(exactOutput.Text) == boundary && exactOutput.StderrByteCount == 0,
            "the output byte limit must accept an exactly full UTF-8 response");

        var aborts = 0;
        var overflow = Enumerable.Repeat((byte)'y', boundary + 1).ToArray();
        try
        {
            await H3cCommandOutputCollector.CollectAsync(new MemoryStream(overflow), new MemoryStream(),
                Task.CompletedTask, boundary, () => aborts++);
            throw new InvalidOperationException("output larger than the configured limit should fail");
        }
        catch (H3cPartialOutputException ex)
        {
            Assert(ex.Output.Length == boundary && ex.FailureCode == "H3C_OUTPUT_LIMIT_PARTIAL",
                "over-limit stdout must preserve exactly the bounded prefix");
        }
        Assert(aborts == 1, "output overflow must abort the SSH transport");

        var truncatedUtf8 = Enumerable.Repeat((byte)'u', boundary - 2)
            .Concat(new byte[] { 0xe2, 0x82, 0xac }).ToArray();
        try
        {
            await H3cCommandOutputCollector.CollectAsync(new MemoryStream(truncatedUtf8), new MemoryStream(),
                Task.CompletedTask, boundary, () => { });
            throw new InvalidOperationException("overflow cut through a UTF-8 character must stay visibly partial");
        }
        catch (H3cPartialOutputException ex)
        {
            Assert(ex.Output == new string('u', boundary - 2) && ex.FailureCode == "H3C_OUTPUT_LIMIT_PARTIAL",
                "a byte-capped UTF-8 tail must retain its valid prefix without inserting replacement characters");
        }

        var stderrOverflow = Enumerable.Repeat((byte)'e', boundary + 1).ToArray();
        try
        {
            await H3cCommandOutputCollector.CollectAsync(new MemoryStream(), new MemoryStream(stderrOverflow),
                Task.CompletedTask, boundary, () => { });
            throw new InvalidOperationException("stderr must count toward the combined SSH output limit");
        }
        catch (H3cOutputLimitException) { }

        try
        {
            await H3cCommandOutputCollector.CollectAsync(new MemoryStream(Encoding.UTF8.GetBytes("partial response")),
                new MemoryStream(), Task.FromException(new IOException("simulated SSH connection reset")), boundary, () => { });
            throw new InvalidOperationException("a reset after partial output must not be reported as complete");
        }
        catch (H3cPartialOutputException ex)
        {
            Assert(ex.Output == "partial response" && ex.FailureCode == "H3C_DISCONNECTED_PARTIAL",
                "partial command output must remain available with a disconnect status");
        }

        try
        {
            await H3cCommandOutputCollector.CollectAsync(new MemoryStream(new byte[] { 0xff }), new MemoryStream(),
                Task.CompletedTask, boundary, () => { });
            throw new InvalidOperationException("invalid UTF-8 must be diagnosed, not silently replaced");
        }
        catch (H3cInvalidEncodingException) { }

        using (var cancellation = new CancellationTokenSource())
        using (var blockedOutput = new CancellationReadStream())
        {
            var cancelAborts = 0;
            var execution = Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            var collection = H3cCommandOutputCollector.CollectAsync(blockedOutput, new MemoryStream(), execution,
                boundary, () => { cancelAborts++; blockedOutput.Release(); }, cancellation.Token);
            await blockedOutput.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();
            try
            {
                await collection.WaitAsync(TimeSpan.FromSeconds(1));
                throw new InvalidOperationException("cancellation must stop a blocked SSH output read");
            }
            catch (OperationCanceledException) { }
            Assert(cancelAborts == 1 && collection.IsCompleted,
                "cancellation must abort the transport once and leave no output-drain task behind");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            var partialOutput = H3cCommandOutputCollector.CollectAsync(
                new MemoryStream(Encoding.UTF8.GetBytes("partial before cancel")), new MemoryStream(),
                Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token), boundary, () => { }, cancellation.Token);
            cancellation.Cancel();
            try
            {
                await partialOutput.WaitAsync(TimeSpan.FromSeconds(1));
                throw new InvalidOperationException("cancelled output must not be reported as complete");
            }
            catch (H3cPartialOutputException ex)
            {
                Assert(ex.IsCancellation && ex.FailureCode == "H3C_CANCELLED_PARTIAL" &&
                       ex.Output == "partial before cancel",
                    "cancelled partial output must retain its bytes with an explicit cancellation state");
            }
        }
    }

    private static void VerifiesLocalSecurityTokenRotationAndDpapiBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-local-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = LocalApiTokenStore.GetOrCreate(root);
            Assert(first.Length == 43 && LocalApiTokenStore.ReadExisting(root) == first,
                "local API bearer token must be high entropy and persist across service starts");
            var rotated = LocalApiTokenStore.Rotate(root);
            Assert(rotated != first && LocalApiTokenStore.ReadExisting(root) == rotated,
                "rotating the local API token must revoke the previous bearer immediately");
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(Path.Combine(root, LocalApiTokenStore.FileName));
                Assert((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0,
                    "the local API token file must not be readable by other Unix users");
            }
            else
            {
                const string secret = "synthetic-only-not-a-device-credential";
                WindowsDpapiCredentialVault.Set(root, "LAB_SWITCH_A", secret);
                var vaultFile = Path.Combine(root, "credentials", "h3c-credentials.v1.json");
                Assert(WindowsDpapiCredentialVault.Get(root, "LAB_SWITCH_A") == secret,
                    "DPAPI protected credential should round-trip for the local machine service identity");
                Assert(File.ReadAllBytes(vaultFile).AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0,
                    "credential vault must not persist the plaintext secret");
                WindowsDpapiCredentialVault.Set(root, "LAB_SWITCH_A", "updated-synthetic-only");
                Assert(WindowsDpapiCredentialVault.Get(root, "LAB_SWITCH_A") == "updated-synthetic-only",
                    "DPAPI credential update must take effect without a database change");
                Assert(WindowsDpapiCredentialVault.Revoke(root, "LAB_SWITCH_A") &&
                       WindowsDpapiCredentialVault.Get(root, "LAB_SWITCH_A") is null,
                    "credential revocation must make the reference unavailable");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifiesDeviceInventoryMappingAndCounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-device-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            using (var db = OpenCatalog(root))
            {
                using (var sources = db.CreateCommand())
                {
                    sources.CommandText = @"INSERT INTO collection_sources(id,site_id,kind,label) VALUES
                        ('ui-source-online','local','H3C_SSH','UI online'),
                        ('ui-source-offline','local','H3C_SSH','UI offline'),
                        ('ui-source-unknown','local','H3C_SSH','UI unknown'),
                        ('ui-source-invalid','local','H3C_SSH','UI invalid')";
                    sources.ExecuteNonQuery();
                }
                using (var profiles = db.CreateCommand())
                {
                    profiles.CommandText = @"INSERT INTO h3c_device_profiles
                        (id,source_id,host,port,username,credential_reference,host_key_sha256,enabled,created_ms,updated_ms,
                         host_key_update_policy,allow_legacy_algorithms,terminal_encoding,terminal_protocol,allow_telnet_fallback)
                        VALUES
                        ('device-online','ui-source-online','192.0.2.10',22,'reader','CRED','SHA256:online',1,1,400,'AUTO_UPDATE',0,'AUTO','SSH',0),
                        ('device-offline','ui-source-offline','192.0.2.11',22,'reader','CRED','SHA256:offline',1,2,300,'AUTO_UPDATE',0,'AUTO','SSH',0),
                        ('device-unknown','ui-source-unknown','192.0.2.12',22,'reader','CRED','SHA256:unknown',1,3,200,'AUTO_UPDATE',0,'AUTO','SSH',0),
                        ('','ui-source-invalid','',22,'','CRED','SHA256:invalid',0,4,100,'AUTO_UPDATE',0,'AUTO','',0)";
                    profiles.ExecuteNonQuery();
                }
                using var sessions = db.CreateCommand();
                sessions.CommandText = @"INSERT INTO terminal_sessions
                    (id,target_id,target_name,protocol,mode,host,port,state,started_ms,last_activity_ms,disconnect_reason)
                    VALUES
                    ('ui-session-online','device-online','device-online','SSH','RESTRICTED_READONLY','192.0.2.10',22,'CONNECTED',500,500,NULL),
                    ('ui-session-offline','device-offline','device-offline','SSH','RESTRICTED_READONLY','192.0.2.11',22,'DISCONNECTED',400,400,'synthetic test')";
                sessions.ExecuteNonQuery();
            }

            var page = catalog.QueryDeviceInventory(page: 1, pageSize: 2);
            Assert(page.Total == 4 && page.Items.Count == 2 && page.HasMore,
                $"device inventory pagination should expose all records without rendering all rows: {page}");
            Assert(page.Summary.TotalRecords == 4 && page.Summary.ValidDevices == 3 && page.Summary.InvalidRecords == 1 &&
                   page.Summary.EnabledDevices == 3 && page.Summary.DisabledDevices == 1 &&
                   page.Summary.OnlineDevices == 1 && page.Summary.OfflineDevices == 1 && page.Summary.UnknownStatus == 1,
                $"device inventory summary counts must distinguish validity, enablement and connection status: {page.Summary}");

            var invalid = catalog.QueryDeviceInventory(pageSize: 20).Items.Single(item => !item.IsValid);
            Assert(invalid.Name == "未命名设备" && invalid.ManagementAddress == "—" && invalid.Protocol == "—" &&
                   invalid.ConnectionStatus == "DISABLED" && invalid.QualityIssues.Contains("MISSING_ID") &&
                   invalid.QualityIssues.Contains("MISSING_ADDRESS") && invalid.QualityIssues.Contains("MISSING_PROTOCOL"),
                $"invalid device fields must map to truthful display fallbacks and quality issues: {invalid}");

            Assert(catalog.QueryDeviceInventory(pageSize: 20, status: "ONLINE").Total == 1 &&
                   catalog.QueryDeviceInventory(pageSize: 20, status: "UNKNOWN").Total == 1 &&
                   catalog.QueryDeviceInventory(pageSize: 20, status: "DISABLED").Total == 1 &&
                   catalog.QueryDeviceInventory(pageSize: 20, search: "192.0.2.11").Total == 1,
                "device inventory status and search filters must operate on real catalog/session fields");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifiesDeviceManagementPhase1()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-device-phase1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            var group = catalog.CreateDeviceGroup("local", "Phase1", "isolated fixture", "test");
            var created = catalog.CreateDeviceInventory(new(null, "Phase 1 AP", "192.0.2.50", "H3C", "S", "NETWORK_DEVICE", "local", group.Id, "fixture", true, null), "test");
            Assert(created.Item.ManagementIp == "192.0.2.50" && created.Item.GroupId == group.Id && created.Item.TerminalProfileId is null,
                "device inventory create must persist the normalized management IP, site/group and optional terminal-profile link");

            var updated = catalog.UpdateDeviceInventory(created.Item.Id,
                new(created.Item.Id, "Phase 1 AP renamed", "192.0.2.51", "H3C", "S", "NETWORK_DEVICE", "local", group.Id, "updated", true, null), "test");
            Assert(updated.Item.ManagementIp == "192.0.2.51" && updated.Item.Name == "Phase 1 AP renamed",
                "device inventory edit must keep a stable device id while changing the management IP");

            var preview = catalog.PreviewDeviceImport($"deviceId,name,managementIp,vendor,model,deviceType,siteId,groupName,description,enabled\n,Imported AP,192.0.2.52,H3C,X,AP,local,Phase1,from csv,true\n{created.Item.Id},Phase 1 AP renamed,192.0.2.53,H3C,S,NETWORK_DEVICE,local,Phase1,move,true", "test");
            Assert(preview.CreateCount == 1 && preview.UpdateCount == 1 && preview.ConflictCount == 0 && preview.ErrorCount == 0,
                "CSV preview must classify create/update rows without writing them");
            var committed = catalog.CommitDeviceImport(preview.PreviewId, true, "test");
            Assert(committed.Created == 1 && committed.Updated == 1 && catalog.QueryManagedDeviceInventory(pageSize: 20).Total == 2,
                "CSV commit must atomically create/update the previewed inventory rows");

            var conflict = catalog.PreviewDeviceImport("name,managementIp\nDuplicate,192.0.2.52\nDuplicate again,192.0.2.52", "test");
            Assert(conflict.ConflictCount == 1 && conflict.ErrorCount == 1,
                "CSV duplicate management IP must be reported as a conflict");
            AssertThrows<InvalidOperationException>(() => catalog.CommitDeviceImport(conflict.PreviewId, true, "test"));
            Assert(catalog.QueryManagedDeviceInventory(pageSize: 20).Total == 2,
                "conflicted import must roll back the whole batch");
            AssertThrows<ArgumentException>(() => catalog.PreviewDeviceImport("name,managementIp,password\nBad,192.0.2.61,nope", "test"));

            var profileId = Guid.NewGuid().ToString("N");
            var fingerprint = "SHA256:" + Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()).TrimEnd('=');
            catalog.CreateH3cDeviceProfile(new(profileId, "192.0.2.70", 22, "reader", "PHASE1_CREDENTIAL", fingerprint, true, 0, 0));
            using (var db = OpenCatalog(root))
            using (var session = db.CreateCommand())
            {
                session.CommandText = @"INSERT INTO terminal_sessions
                    (id,target_id,target_name,protocol,mode,host,port,state,started_ms,last_activity_ms,disconnect_reason)
                    VALUES('phase1-session',$target,'profile','SSH','RESTRICTED_READONLY','192.0.2.70',22,'CLOSED',900,900,'fixture')";
                session.Parameters.AddWithValue("$target", profileId); session.ExecuteNonQuery();
            }
            var bridge = catalog.GetDeviceInventoryDetail(profileId)!;
            var profileUpdated = catalog.UpdateDeviceInventory(profileId,
                new(profileId, "Linked terminal", "192.0.2.71", "H3C", "S", "H3C_TERMINAL", "local", null, "IP changed", true, profileId), "test");
            Assert(profileUpdated.TerminalProfileId == profileId && profileUpdated.RecentSessions.Any(item => item.Id == "phase1-session") &&
                   catalog.GetH3cDeviceProfile(profileId)!.Host == "192.0.2.70",
                "editing inventory IP must preserve the terminal profile host and existing session history");
            var audits = catalog.GetDeviceInventoryDetail(created.Item.Id)!.AuditEvents;
            Assert(audits.Any(item => item.Action.StartsWith("DEVICE_", StringComparison.Ordinal)),
                "device mutations must leave audit_events evidence without credential fields");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifiesDeviceManagementPhase2A()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-device-p2a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            using (var db = OpenCatalog(root))
            using (var sites = db.CreateCommand())
            {
                sites.CommandText = "INSERT INTO sites(id,name) VALUES('site-2','P2A Site 2')";
                sites.ExecuteNonQuery();
            }

            var localGroup = catalog.CreateDeviceGroup("local", "P2A Local", "batch fixture", "p2a");
            var first = catalog.CreateDeviceInventory(new(null, "P2A One", "192.0.2.121", "H3C", "S", "AP", "local", localGroup.Id, "one", true, null), "p2a");
            var second = catalog.CreateDeviceInventory(new(null, "P2A Two", "192.0.2.122", "H3C", "S", "AP", "local", localGroup.Id, "two", true, null), "p2a");
            var third = catalog.CreateDeviceInventory(new(null, "P2A Three", "192.0.2.123", "H3C", "S", "AP", "local", localGroup.Id, "three", true, null), "p2a");
            var ids = new[] { first.Item.Id, second.Item.Id, third.Item.Id };

            var disableRequest = new DeviceBatchOperationRequest("BATCH_DISABLE", "p2a-disable", ids, null, null);
            var disablePreview = catalog.CreateDeviceBatchOperation(disableRequest, "p2a");
            var disableRetry = catalog.CreateDeviceBatchOperation(disableRequest, "p2a");
            Assert(disablePreview.Id == disableRetry.Id && disablePreview.State == "PENDING_CONFIRM" && disablePreview.TargetCount == 3,
                "batch preview must persist explicit targets and return the same operation for an identical idempotency key");
            var disabled = catalog.ConfirmDeviceBatchOperation(disablePreview.Id, "p2a");
            Assert(disabled.State == "SUCCEEDED" && disabled.CompletedCount == 3 && ids.All(id => catalog.GetDeviceInventoryDetail(id)!.Item.Enabled == false),
                "a confirmed disable batch must atomically update every selected device");
            Assert(catalog.ConfirmDeviceBatchOperation(disablePreview.Id, "p2a").State == "SUCCEEDED",
                "repeating a successful batch confirmation must be idempotent");

            var groupOperation = catalog.CreateDeviceBatchOperation(new DeviceBatchOperationRequest("BATCH_SET_GROUP", "p2a-group", ids, null, localGroup.Id), "p2a");
            var grouped = catalog.ConfirmDeviceBatchOperation(groupOperation.Id, "p2a");
            Assert(grouped.State == "SUCCEEDED" && ids.All(id => catalog.GetDeviceInventoryDetail(id)!.Item.GroupId == localGroup.Id),
                "batch group assignment must validate the group/site boundary and update all targets");

            var siteOperation = catalog.CreateDeviceBatchOperation(new DeviceBatchOperationRequest("BATCH_SET_SITE", "p2a-site", ids, "site-2"), "p2a");
            var moved = catalog.ConfirmDeviceBatchOperation(siteOperation.Id, "p2a");
            var movedItems = ids.Select(id => catalog.GetDeviceInventoryDetail(id)!.Item).ToArray();
            Assert(moved.State == "SUCCEEDED" && movedItems.All(item => item.SiteId == "site-2" && string.IsNullOrEmpty(item.GroupId)),
                $"batch site assignment must clear the old group and preserve all-or-nothing semantics: {moved.State}; {string.Join(" | ", movedItems.Select(item => $"{item.Id}:{item.SiteId}:{item.GroupId}"))}; {string.Join(" | ", moved.Items.Select(item => $"{item.DeviceId}:{item.Outcome}:{item.Detail}"))}");

            var metadata = catalog.CreateDeviceBatchOperation(new DeviceBatchOperationRequest("BATCH_UPDATE_METADATA", "p2a-metadata", new[] { first.Item.Id, second.Item.Id }, Name: "P2A Updated", Vendor: "H3C"), "p2a");
            Assert(catalog.ConfirmDeviceBatchOperation(metadata.Id, "p2a").State == "SUCCEEDED" &&
                   catalog.GetDeviceInventoryDetail(first.Item.Id)!.Item.Name == "P2A Updated" &&
                   catalog.GetDeviceInventoryDetail(second.Item.Id)!.Item.Name == "P2A Updated",
                "metadata batches must update only explicitly requested fields");

            var conflict = catalog.CreateDeviceBatchOperation(new DeviceBatchOperationRequest("BATCH_ENABLE", "p2a-conflict", new[] { first.Item.Id, second.Item.Id }), "p2a");
            var current = catalog.GetDeviceInventoryDetail(first.Item.Id)!;
            catalog.UpdateDeviceInventory(first.Item.Id, new(first.Item.Id, current.Item.Name, current.Item.ManagementIp, "H3C", "S", "AP", "site-2", null, current.Description, true, null), "p2a");
            AssertThrows<DeviceBatchConflictException>(() => catalog.ConfirmDeviceBatchOperation(conflict.Id, "p2a"));
            var conflicted = catalog.GetDeviceBatchOperation(conflict.Id, "p2a")!;
            Assert(conflicted.State == "CONFLICT" && conflicted.ConflictCount == 1 && catalog.GetDeviceInventoryDetail(second.Item.Id)!.Item.Enabled == false,
                "a version conflict must write no device from the batch and must remain explicitly auditable");

            var missing = catalog.CreateDeviceBatchOperation(new DeviceBatchOperationRequest("BATCH_ENABLE", "p2a-missing", new[] { "missing-device" }), "p2a");
            Assert(missing.State == "CONFLICT" && missing.Items.Single().Detail == "DEVICE_NOT_FOUND",
                "preview must retain a missing explicit target as a conflict instead of dropping it");
            AssertThrows<InvalidOperationException>(() => catalog.ConfirmDeviceBatchOperation(missing.Id, "p2a"));

            var csvRace = catalog.CreateDeviceBatchOperation(new DeviceBatchOperationRequest("BATCH_DISABLE", "p2a-csv-race", new[] { first.Item.Id }), "p2a");
            var csvCurrent = catalog.GetDeviceInventoryDetail(first.Item.Id)!.Item;
            var csvPreview = catalog.PreviewDeviceImport($"deviceId,name,managementIp,vendor,model,deviceType,siteId,description,enabled\n{csvCurrent.Id},CSV race,{csvCurrent.ManagementIp},H3C,S,AP,{csvCurrent.SiteId},csv race,true", "p2a-csv-race");
            catalog.CommitDeviceImport(csvPreview.PreviewId, true, "p2a-csv-race");
            AssertThrows<DeviceBatchConflictException>(() => catalog.ConfirmDeviceBatchOperation(csvRace.Id, "p2a"));
            Assert(catalog.GetDeviceBatchOperation(csvRace.Id, "p2a")!.State == "CONFLICT" &&
                   catalog.GetDeviceInventoryDetail(first.Item.Id)!.Item.Name == "CSV race",
                "a CSV commit after batch preview must win its own revision and force the stale batch into conflict");

            var cancelled = catalog.CreateDeviceBatchOperation(new DeviceBatchOperationRequest("BATCH_ENABLE", "p2a-cancel", new[] { third.Item.Id }), "p2a");
            Assert(catalog.CancelDeviceBatchOperation(cancelled.Id, "p2a").State == "CANCELLED" &&
                   AssertThrowsReturn<InvalidOperationException>(() => catalog.ConfirmDeviceBatchOperation(cancelled.Id, "p2a")),
                "a pending operation must support explicit cancellation and reject later confirmation");

            var cancelRace = catalog.CreateDeviceBatchOperation(new DeviceBatchOperationRequest("BATCH_ENABLE", "p2a-cancel-race", new[] { second.Item.Id }), "p2a");
            var cancelRaceResults = await Task.WhenAll(
                Task.Run(() => TryConfirmBatch(catalog, cancelRace.Id)),
                Task.Run(() => TryCancelBatch(catalog, cancelRace.Id)));
            var cancelRaceFinal = catalog.GetDeviceBatchOperation(cancelRace.Id, "p2a")!;
            Assert(cancelRaceFinal.State is "SUCCEEDED" or "CANCELLED" &&
                   cancelRaceResults.Any(result => result is "SUCCEEDED" or "CANCELLED"),
                $"confirm/cancel race must converge to one terminal state: {cancelRaceFinal.State}; {string.Join(" | ", cancelRaceResults)}");

            var retry = catalog.CreateDeviceBatchOperation(new DeviceBatchOperationRequest("BATCH_ENABLE", "p2a-retry", new[] { second.Item.Id }), "p2a");
            using (var db = OpenCatalog(root))
            using (var fail = db.CreateCommand())
            {
                fail.CommandText = "UPDATE device_operations SET state='FAILED' WHERE id=$id; UPDATE device_operation_items SET outcome='FAILED',detail='INJECTED_FAILURE' WHERE operation_id=$id";
                fail.Parameters.AddWithValue("$id", retry.Id); fail.ExecuteNonQuery();
            }
            Assert(catalog.ConfirmDeviceBatchOperation(retry.Id, "p2a").State == "SUCCEEDED" && catalog.GetDeviceInventoryDetail(second.Item.Id)!.Item.Enabled,
                "a failed operation must reset only its failed items before a safe retry");

            var concurrent = catalog.CreateDeviceBatchOperation(new DeviceBatchOperationRequest("BATCH_DISABLE", "p2a-concurrent", new[] { third.Item.Id }), "p2a");
            var confirmations = await Task.WhenAll(
                Task.Run(() => TryConfirmBatch(catalog, concurrent.Id)),
                Task.Run(() => TryConfirmBatch(catalog, concurrent.Id)));
            Assert(confirmations.Count(result => result == "SUCCEEDED") >= 1 && catalog.GetDeviceInventoryDetail(third.Item.Id)!.Item.Enabled == false,
                "concurrent confirmation must converge on one committed operation without double-writing the device");

            AssertThrows<UnauthorizedAccessException>(() => catalog.GetDeviceBatchOperation(disablePreview.Id, "other-admin"));
            AssertThrows<DeviceBatchResourceLimitException>(() => catalog.CreateDeviceBatchOperation(
                new DeviceBatchOperationRequest("BATCH_ENABLE", "p2a-limit", Enumerable.Range(0, 101).Select(index => $"device-{index}").ToArray()), "p2a"));
            using (var db = OpenCatalog(root))
            using (var audits = db.CreateCommand())
            {
                audits.CommandText = "SELECT action,detail_json FROM audit_events WHERE action LIKE 'DEVICE_BATCH_%'";
                using var rows = audits.ExecuteReader();
                var auditRows = new List<(string Action, string Detail)>();
                while (rows.Read()) auditRows.Add((rows.GetString(0), rows.GetString(1)));
                Assert(auditRows.Any(item => item.Action == "DEVICE_BATCH_SUCCESS") && auditRows.All(item => !item.Detail.Contains("password", StringComparison.OrdinalIgnoreCase)),
                    "batch success and audit payloads must be queryable without credentials or secret fields");
            }

            await Task.Yield();
        }
        finally { Directory.Delete(root, true); }
    }

    private static string TryConfirmBatch(Catalog catalog, string operationId)
    {
        try { return catalog.ConfirmDeviceBatchOperation(operationId, "p2a").State; }
        catch (Exception exception) { return exception.GetType().Name; }
    }

    private static string TryCancelBatch(Catalog catalog, string operationId)
    {
        try { return catalog.CancelDeviceBatchOperation(operationId, "p2a").State; }
        catch (Exception exception) { return exception.GetType().Name; }
    }

    private static bool AssertThrowsReturn<T>(Action action) where T : Exception
    {
        AssertThrows<T>(action);
        return true;
    }

    private static async Task VerifiesDeviceManagementSafetyBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-device-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            var fingerprint = "SHA256:" + Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()).TrimEnd('=');
            var profileId = Guid.NewGuid().ToString("N");
            catalog.CreateH3cDeviceProfile(new(profileId, "192.0.2.80", 22, "reader", "PHASE1_SAFETY_A", fingerprint, true, 0, 0));
            var linked = catalog.GetDeviceInventoryDetail(profileId)!;
            var duplicateHostProfileId = Guid.NewGuid().ToString("N");
            catalog.CreateH3cDeviceProfile(new(duplicateHostProfileId, "192.0.2.80", 22, "reader", "PHASE1_SAFETY_DUPLICATE", fingerprint, true, 0, 0));
            Assert(catalog.QueryManagedDeviceInventory(pageSize: 100).Items.Count(item => item.ManagementIp == "192.0.2.80") == 1 &&
                   catalog.GetH3cDeviceProfile(duplicateHostProfileId) is not null,
                "legacy terminal-profile bridge must not create duplicate inventory rows for one site-scoped management IP");

            using (var db = OpenCatalog(root))
            using (var disable = db.CreateCommand())
            {
                disable.CommandText = "UPDATE h3c_device_profiles SET enabled=0 WHERE id=$id; UPDATE collection_sources SET enabled=0 WHERE id=(SELECT source_id FROM h3c_device_profiles WHERE id=$id);";
                disable.Parameters.AddWithValue("$id", profileId);
                disable.ExecuteNonQuery();
            }
            catalog.UpdateDeviceInventory(profileId, new(profileId, linked.Item.Name, linked.Item.ManagementIp, "H3C", "S", "H3C_TERMINAL", "local", null, "disabled terminal fixture", true, profileId), "test");
            Assert(ReadProfileAndSourceEnabled(root, profileId) == (false, false),
                "device inventory enablement must not re-enable a manually disabled terminal profile or collection source");
            catalog.UpdateDeviceInventory(profileId, new(profileId, linked.Item.Name, linked.Item.ManagementIp, "H3C", "S", "H3C_TERMINAL", "local", null, "disabled inventory", false, profileId), "test");
            catalog.UpdateDeviceInventory(profileId, new(profileId, linked.Item.Name, linked.Item.ManagementIp, "H3C", "S", "H3C_TERMINAL", "local", null, "re-enabled inventory", true, profileId), "test");
            Assert(ReadProfileAndSourceEnabled(root, profileId) == (false, false),
                "device disable and re-enable must not mutate terminal or collection-source state");

            var secondProfileId = Guid.NewGuid().ToString("N");
            catalog.CreateH3cDeviceProfile(new(secondProfileId, "192.0.2.81", 22, "reader", "PHASE1_SAFETY_B", fingerprint, true, 0, 0));
            var second = catalog.GetDeviceInventoryDetail(secondProfileId)!;
            catalog.UpdateDeviceInventory(secondProfileId, new(secondProfileId, second.Item.Name, second.Item.ManagementIp, "H3C", "S", "H3C_TERMINAL", "local", null, "unlinked", true, null), "test");
            catalog.UpdateDeviceInventory(profileId, new(profileId, linked.Item.Name, linked.Item.ManagementIp, "H3C", "S", "H3C_TERMINAL", "local", null, "reassigned", false, secondProfileId), "test");
            Assert(ReadProfileAndSourceEnabled(root, profileId) == (false, false) && ReadProfileAndSourceEnabled(root, secondProfileId) == (true, true),
                "profile reassignment must not rewrite either the old or the new profile/source state");
            AssertThrows<InvalidOperationException>(() => catalog.CreateDeviceInventory(new(null, "duplicate profile", "192.0.2.82", "H3C", "S", "H3C_TERMINAL", "local", null, "", true, secondProfileId), "test"));

            catalog.CreateTaskDefinition("phase1-safety-task", "H3C_SSH_READONLY", secondProfileId, "display version", TimeSpan.FromSeconds(1));
            Assert(!string.IsNullOrWhiteSpace(catalog.CreateTaskRun("phase1-safety-task")),
                "inventory disablement must not silently cancel or bypass the separately governed terminal task policy");

            AssertThrows<ArgumentException>(() => catalog.PreviewDeviceImport("name,managementIp\n\"bad\"suffix,192.0.2.90", "test"));
            AssertThrows<ArgumentException>(() => catalog.PreviewDeviceImport("name,name,managementIp\nDuplicate,Duplicate,192.0.2.91", "test"));
            AssertThrows<ArgumentException>(() => catalog.PreviewDeviceImport("name,managementIp\nExtra,192.0.2.92,ignored", "test"));
            var quoted = catalog.PreviewDeviceImport("name,managementIp,description\n\"Quoted, \"\"device\"\"\",192.0.2.93,fixture", "quoted-test");
            Assert(quoted.ErrorCount == 0 && quoted.CreateCount == 1, "CSV quoted commas and escaped quotes must remain one strict record");
            var duplicateId = catalog.PreviewDeviceImport("deviceId,name,managementIp\nsame-id,One,192.0.2.94\nsame-id,Two,192.0.2.95", "edge-test");
            Assert(duplicateId.ConflictCount == 1 && duplicateId.ErrorCount == 1 && duplicateId.CreateCount == 1,
                "CSV must reject duplicate device ids within one batch instead of failing during commit");
            var formula = catalog.PreviewDeviceImport("name,managementIp\n=SUM,192.0.2.96", "edge-test");
            Assert(formula.ConflictCount == 1 && formula.ErrorCount == 1, "CSV formula-like text must be rejected before persistence");

            var stale = catalog.PreviewDeviceImport($"deviceId,name,managementIp\n{profileId},stale preview,192.0.2.80", "stale-test");
            catalog.UpdateDeviceInventory(profileId, new(profileId, "changed after preview", "192.0.2.80", "H3C", "S", "H3C_TERMINAL", "local", null, "changed", false, secondProfileId), "test");
            AssertThrows<InvalidOperationException>(() => catalog.CommitDeviceImport(stale.PreviewId, true, "stale-test"));
            Assert(catalog.GetDeviceInventoryDetail(profileId)!.Item.Name == "changed after preview", "stale CSV commit must not overwrite a newer device update");

            var idempotent = catalog.PreviewDeviceImport("name,managementIp\nIdempotent,192.0.2.97", "commit-test");
            var firstCommit = catalog.CommitDeviceImport(idempotent.PreviewId, true, "commit-test");
            var retryCommit = catalog.CommitDeviceImport(idempotent.PreviewId, true, "commit-test");
            Assert(firstCommit == retryCommit && catalog.QueryManagedDeviceInventory(pageSize: 100).Items.Count(item => item.ManagementIp == "192.0.2.97") == 1,
                "repeating a successful CSV commit must return the bounded receipt instead of applying the batch twice");
            AssertThrows<UnauthorizedAccessException>(() => catalog.CommitDeviceImport(idempotent.PreviewId, true, "other-admin"));

            var resourceCsv = "name,managementIp\nResource,192.0.2.100";
            for (var index = 0; index < 8; index++) catalog.PreviewDeviceImport(resourceCsv, "resource-admin");
            AssertThrows<DeviceImportResourceLimitException>(() => catalog.PreviewDeviceImport(resourceCsv, "resource-admin"));
            var tooManyRows = new StringBuilder("name,managementIp\n");
            for (var index = 0; index < 10_001; index++) tooManyRows.Append($"Row{index},192.0.2.{(index % 200) + 1}\n");
            AssertThrows<DeviceImportResourceLimitException>(() => catalog.PreviewDeviceImport(tooManyRows.ToString(), "row-admin"));

            await Task.Yield();
        }
        finally { Directory.Delete(root, true); }
    }

    private static (bool ProfileEnabled, bool SourceEnabled) ReadProfileAndSourceEnabled(string root, string profileId)
    {
        using var db = OpenCatalog(root);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT p.enabled,cs.enabled FROM h3c_device_profiles p JOIN collection_sources cs ON cs.id=p.source_id WHERE p.id=$id";
        command.Parameters.AddWithValue("$id", profileId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("profile fixture was not found");
        return (reader.GetInt32(0) != 0, reader.GetInt32(1) != 0);
    }

    private static async Task PersistsH3cTaskOutputAsVerifiedRaw()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-h3c-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = TestSettings(root);
            var catalog = new Catalog(settings);
            catalog.Initialize();
            catalog.ProbeRawWritable();
            var profileId = Guid.NewGuid().ToString("N");
            var fingerprint = "SHA256:" + Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()).TrimEnd('=');
            catalog.CreateH3cDeviceProfile(new(profileId, "192.0.2.10", 22, "readonly", "LAB_SWITCH_A",
                fingerprint, true, 0, 0));
            AssertThrows<ArgumentException>(() => catalog.CreateH3cDeviceProfile(new(Guid.NewGuid().ToString("N"),
                "192.0.2.11", 22, "readonly", "LAB_SWITCH_A", string.Empty, true, 0, 0)));
            AssertThrows<InvalidOperationException>(() => catalog.CreateH3cDeviceProfile(new(profileId,
                "192.0.2.11", 22, "readonly", "LAB_SWITCH_A", fingerprint, true, 0, 0)));
            await VerifiesH3cTransportLifecycle(catalog.GetH3cDeviceProfile(profileId)!, fingerprint);
            var noSecretTransport = new FakeH3cSshTransportFactory(fingerprint);
            var noSecretSession = new H3cSshDeviceSessionFactory(catalog,
                new FixedH3cCredentialProvider(null), noSecretTransport).Create(profileId);
            try
            {
                await noSecretSession.ConnectAsync(CancellationToken.None);
                throw new InvalidOperationException("Missing H3C secret must fail before opening a network connection.");
            }
            catch (H3cCredentialUnavailableException) { }
            finally { await noSecretSession.DisposeAsync(); }
            Assert(noSecretTransport.LastTransport is null,
                "an unavailable credential must fail before creating or opening an SSH transport");
            AssertThrows<ArgumentException>(() => catalog.CreateTaskDefinition("h3c-denied", "H3C_SSH_READONLY",
                profileId, "display current-configuration", TimeSpan.FromSeconds(10)));
            catalog.CreateTaskDefinition("h3c-version", "H3C_SSH_READONLY", profileId,
                H3cCommandPolicy.DisplayVersion, TimeSpan.FromSeconds(10));

            var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "display_version.txt"));
            var fakeFactory = new FakeSessionFactory(_ => new FakeBehavior((_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(output);
            }));
            var sink = new H3cTaskRawResultSink(catalog);
            await using (var scheduler = new BoundedCollectionScheduler(fakeFactory, 1, 2, catalog, resultSink: sink))
            {
                Assert(scheduler.TryScheduleDefinition("h3c-version", out var runId), "persisted H3C task should be admitted");
                var terminal = await WaitTerminal(scheduler, runId);
                Assert(terminal.State == "SUCCEEDED", terminal.ToString()!);
            }

            var persisted = catalog.RecentTaskRuns().Single(x => x.TaskType == "H3C_SSH_READONLY");
            Assert(persisted.State == "SUCCEEDED" && persisted.SourceId == "h3c-" + profileId &&
                   persisted.RawOutputPath is not null && persisted.RawOutputSha256 is { Length: 64 } &&
                   persisted.RawOutputBytes == Encoding.UTF8.GetByteCount(output), persisted.ToString()!);
            var rawPath = Path.Combine(root, persisted.RawOutputPath!.Replace('/', Path.DirectorySeparatorChar));
            var rawBytes = File.ReadAllBytes(rawPath);
            Assert(Convert.ToHexString(SHA256.HashData(rawBytes)).Equals(
                ReadRawSegmentSha(root, persisted.RawOutputPath), StringComparison.OrdinalIgnoreCase), "sealed H3C Raw checksum should match Catalog");
            using (var rawDocument = System.Text.Json.JsonDocument.Parse(rawBytes.AsMemory(0, rawBytes.Length - 1)))
            {
                var record = rawDocument.RootElement;
                Assert(record.GetProperty("record_type").GetString() == "h3c_command" &&
                       record.GetProperty("source_id").GetString() == persisted.SourceId &&
                       record.GetProperty("task_run_id").GetString() == persisted.Id &&
                       record.GetProperty("command").GetString() == H3cCommandPolicy.DisplayVersion,
                    "Raw record should be attributable to source, command and persisted run");
                var roundTrip = Convert.FromBase64String(record.GetProperty("output_utf8_base64").GetString()!);
                Assert(roundTrip.SequenceEqual(Encoding.UTF8.GetBytes(output)), "Raw should preserve the command response UTF-8 bytes");
                Assert(record.GetProperty("output_sha256").GetString() == persisted.RawOutputSha256,
                    "result manifest and Raw output hashes should match");
            }
            const string fakeSecret = "synthetic-test-secret-never-for-a-device";
            const string secretKey = "NETCONSOLE_NEXT_CREDENTIAL_LAB_SWITCH_A";
            var previousSecret = Environment.GetEnvironmentVariable(secretKey);
            try
            {
                Environment.SetEnvironmentVariable(secretKey, fakeSecret);
                Assert(new EnvironmentH3cCredentialProvider(enabled: true).GetPassword("LAB_SWITCH_A") == fakeSecret,
                    "the explicit development provider should resolve a credential by reference only at use time");
                Assert(new EnvironmentH3cCredentialProvider(enabled: false).GetPassword("LAB_SWITCH_A") is null,
                    "the environment credential provider must be disabled unless explicitly enabled");
                Assert(File.ReadAllBytes(Path.Combine(root, "catalog.db")).AsSpan().IndexOf(Encoding.UTF8.GetBytes(fakeSecret)) < 0,
                    "credential material must not be persisted in the catalog");
            }
            finally { Environment.SetEnvironmentVariable(secretKey, previousSecret); }

            var maximumOutputRun = catalog.CreateTaskRun("h3c-version");
            catalog.TransitionTaskRun(maximumOutputRun, "CONNECTING");
            catalog.TransitionTaskRun(maximumOutputRun, "RUNNING");
            var maximumOutput = new string('x', H3cRawOutput.MaximumBytes);
            var maximumManifest = await sink.PersistAsync(maximumOutputRun,
                catalog.GetTaskDefinition("h3c-version")!, maximumOutput, "COMPLETE");
            var maximumRawPath = Path.Combine(root, maximumManifest.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert(new FileInfo(maximumRawPath).Length < 512 * 1024,
                "Base64 H3C Raw line at the output maximum must remain under the recovery line-size limit");
            catalog.CompleteTaskRunWithRawOutput(maximumOutputRun, maximumManifest, "SUCCEEDED", null);

            var blockingSink = new BlockingAfterSealResultSink(sink);
            var commitFactory = new FakeSessionFactory(_ => new FakeBehavior((_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(output);
            }));
            await using (var scheduler = new BoundedCollectionScheduler(commitFactory, 1, 2, catalog,
                resultSink: blockingSink))
            {
                Assert(scheduler.TryScheduleDefinition("h3c-version", out var scheduledRun),
                    "scheduled task should be admitted for the seal/cancel race test");
                await blockingSink.Sealed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(!scheduler.Cancel(scheduledRun),
                    "cancellation after Raw sealing has entered the atomic commit phase must be rejected");
                blockingSink.Release.TrySetResult();
                Assert((await WaitTerminal(scheduler, scheduledRun)).State == "SUCCEEDED",
                    "the sealed output and SUCCEEDED terminal state should commit together");
            }

            var partialRun = catalog.CreateTaskRun("h3c-version");
            catalog.TransitionTaskRun(partialRun, "CONNECTING");
            catalog.TransitionTaskRun(partialRun, "RUNNING");
            var partialManifest = await sink.PersistAsync(partialRun,
                catalog.GetTaskDefinition("h3c-version")!, "partial decoded output", "PARTIAL");
            catalog.CompleteTaskRunWithRawOutput(partialRun, partialManifest, "FAILED", "H3C_DISCONNECTED_PARTIAL");
            var persistedPartial = catalog.RecentTaskRuns().Single(x => x.Id == partialRun);
            Assert(persistedPartial.State == "FAILED" && persistedPartial.CaptureStatus == "PARTIAL" &&
                   persistedPartial.ParseStatus == "PARTIAL" && persistedPartial.RawOutputPath is not null,
                "partial decoded output must be retained and cannot be reported as successful capture or parsing");

            var interruptedId = catalog.CreateTaskRun("h3c-version");
            catalog.TransitionTaskRun(interruptedId, "CONNECTING");
            catalog.TransitionTaskRun(interruptedId, "RUNNING");
            var interruptedManifest = await sink.PersistAsync(interruptedId,
                catalog.GetTaskDefinition("h3c-version")!, output, "COMPLETE");
            using (var db = OpenCatalog(root))
            using (var trigger = db.CreateCommand())
            {
                trigger.CommandText = $@"CREATE TRIGGER fail_test_h3c_terminal BEFORE UPDATE OF state ON task_runs
                    WHEN OLD.id='{interruptedId}' AND NEW.state='SUCCEEDED'
                    BEGIN SELECT RAISE(ABORT,'injected terminal write failure'); END";
                trigger.ExecuteNonQuery();
            }
            AssertThrows<SqliteException>(() => catalog.CompleteTaskRunWithRawOutput(
                interruptedId, interruptedManifest, "SUCCEEDED", null));
            var beforeRestart = catalog.RecentTaskRuns().Single(x => x.Id == interruptedId);
            Assert(beforeRestart.State == "RUNNING" && beforeRestart.RawOutputPath is null,
                "manifest insert and terminal-state update must roll back together when the task transition fails");
            using (var db = OpenCatalog(root))
            using (var drop = db.CreateCommand())
            {
                drop.CommandText = "DROP TRIGGER fail_test_h3c_terminal";
                drop.ExecuteNonQuery();
            }
            var openRunId = catalog.CreateTaskRun("h3c-version");
            Assert(interruptedId != openRunId, "separate crash and open-segment cases must use different persisted task run ids");
            catalog.TransitionTaskRun(openRunId, "CONNECTING");
            catalog.TransitionTaskRun(openRunId, "RUNNING");
            var openWriter = new RawSegmentWriterFactory().CreateH3c(catalog, profileId);
            openWriter.AppendH3cCommand(profileId, openRunId, "192.0.2.10", H3cCommandPolicy.DisplayVersion,
                output, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "COMPLETE");
            await openWriter.FlushAsync();
            await openWriter.PreserveOpenAsync();
            var preservedH3cPath = Directory.GetFiles(catalog.RawRoot, "*.open", SearchOption.AllDirectories).Single();
            var preservedH3cBytes = File.ReadAllBytes(preservedH3cPath);
            AssertThrows<InvalidOperationException>(() => openWriter.AppendH3cCommand(profileId, openRunId,
                "192.0.2.10", H3cCommandPolicy.DisplayVersion, output,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "COMPLETE"));
            AssertThrows<InvalidOperationException>(() => openWriter.FlushAsync().GetAwaiter().GetResult());
            AssertThrows<InvalidOperationException>(() => openWriter.SealAsync().GetAwaiter().GetResult());
            Assert(preservedH3cBytes.SequenceEqual(File.ReadAllBytes(preservedH3cPath)),
                "H3C Append/Flush/Seal after PreserveOpenAsync must not mutate the retained Raw output");
            var recovered = new Catalog(settings);
            recovered.Initialize();
            var recovery = recovered.RecoverRawSegments();
            Assert(recovery.FailedFiles == 0 && recovery.TrustedFilesSkipped == 4 && recovery.RecoveredOpenFiles == 1,
                $"verified indexed H3C output and crash recovery should reconcile idempotently: {recovery}; " +
                string.Join(" | ", recovery.Issues.Select(issue =>
                    $"{issue.Category}:{issue.RelativePath}:{issue.Detail}")) + "; " + DescribeH3cRawEvidence(root));
            var afterRecovery = recovered.RecentTaskRuns().Single(x => x.Id == persisted.Id);
            Assert(afterRecovery.State == "SUCCEEDED" && afterRecovery.RawOutputPath == persisted.RawOutputPath &&
                   afterRecovery.CaptureStatus == "COMPLETE" && afterRecovery.ParseStatus == "PARSED",
                "recovery should preserve task output index and terminal state");
            var interruptedRecovered = recovered.RecentTaskRuns().Single(x => x.Id == interruptedId);
            Assert(interruptedRecovered.State == "INTERRUPTED" && interruptedRecovered.RawOutputPath is not null &&
                   interruptedRecovered.ParseStatus == "PARSED",
                "a process crash after Raw sealing must reconstruct its result manifest without falsely succeeding the run");
            var openRecovered = recovered.RecentTaskRuns().Single(x => x.Id == openRunId);
            Assert(openRecovered.State == "INTERRUPTED" && openRecovered.RawOutputPath is not null &&
                   openRecovered.CaptureStatus == "COMPLETE" && openRecovered.ParseStatus == "PARSED",
                "a complete newline-terminated H3C record recovered from .open must remain interrupted and linked to its Raw evidence");
            _ = recovered.RecoverRawSegments();
            using (var db = OpenCatalog(root))
            using (var count = db.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM task_run_raw_outputs WHERE task_run_id=$run";
                count.Parameters.AddWithValue("$run", interruptedId);
                Assert(Convert.ToInt32(count.ExecuteScalar()) == 1, "repeated crash recovery must not duplicate a task result");
                count.Parameters["$run"].Value = openRunId;
                Assert(Convert.ToInt32(count.ExecuteScalar()) == 1, "repeated .open recovery must not duplicate a task result");
            }
            var cleanRawSize = new FileInfo(rawPath).Length;
            using (var damaged = new FileStream(rawPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                damaged.Write(Encoding.UTF8.GetBytes("{\"record_type\":\"h3c_command\",\"bad\":\n"));
                damaged.Flush(flushToDisk: true);
            }
            var damagedRecovery = recovered.RecoverRawSegments();
            Assert(damagedRecovery.RepairedTailFiles == 1 && damagedRecovery.RecoveryEvidenceFiles == 1 &&
                   damagedRecovery.FailedFiles == 0, $"damaged H3C tail should be repaired with evidence: {damagedRecovery}");
            Assert(File.Exists(rawPath) && new FileInfo(rawPath).Length == cleanRawSize,
                "verified H3C prefix must remain in place and the damaged suffix must not delete valid Raw");
            var damagedAudit = recovered.RecentIntegrityAudits(20).Single(x => x.Reason == "CORRUPT_TAIL_REPAIRED");
            Assert(damagedAudit.EvidencePath is not null &&
                   File.Exists(Path.Combine(root, damagedAudit.EvidencePath.Replace('/', Path.DirectorySeparatorChar))),
                "damaged H3C tail evidence should remain on disk and be indexed");
            Assert(recovered.GetHealthSnapshot().UnresolvedIntegrityIssues > 0,
                "H3C Raw corruption should remain visible as a degraded historical issue");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string ReadRawSegmentSha(string root, string relativePath)
    {
        using var db = OpenCatalog(root);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT sha256 FROM raw_segments WHERE relative_path=$path AND state='FINALIZED'";
        command.Parameters.AddWithValue("$path", relativePath);
        return Convert.ToString(command.ExecuteScalar()) ?? throw new InvalidOperationException("H3C Raw segment was not finalized in SQLite.");
    }

    private static async Task RejectsAmbiguousH3cRecoveryAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-h3c-ambiguous-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = TestSettings(root);
        try
        {
            var catalog = new Catalog(settings);
            catalog.Initialize();
            catalog.ProbeRawWritable();
            var profileId = Guid.NewGuid().ToString("N");
            var fingerprint = "SHA256:" + Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()).TrimEnd('=');
            catalog.CreateH3cDeviceProfile(new(profileId, "192.0.2.20", 22, "readonly", "LAB_SWITCH_B",
                fingerprint, true, 0, 0));
            catalog.CreateTaskDefinition("h3c-ambiguous", "H3C_SSH_READONLY", profileId,
                H3cCommandPolicy.DisplayVersion, TimeSpan.FromSeconds(10));
            var runId = catalog.CreateTaskRun("h3c-ambiguous");
            catalog.TransitionTaskRun(runId, "CONNECTING");
            catalog.TransitionTaskRun(runId, "RUNNING");
            const string output = "Synthetic Comware response for ambiguity recovery test";
            var definition = catalog.GetTaskDefinition("h3c-ambiguous")!;
            _ = await new H3cTaskRawResultSink(catalog).PersistAsync(runId, definition, output, "COMPLETE");

            var duplicate = new RawSegmentWriterFactory().CreateH3c(catalog, profileId);
            duplicate.AppendH3cCommand(profileId, runId, "192.0.2.20", H3cCommandPolicy.DisplayVersion,
                output, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "COMPLETE");
            await duplicate.FlushAsync();
            await duplicate.SealAsync();

            var recovered = new Catalog(settings);
            recovered.Initialize();
            var first = recovered.RecoverRawSegments();
            Assert(first.FailedFiles == 1 && first.Issues.Any(issue => issue.Category == "H3C_RESULT_RECONCILIATION" &&
                   issue.Detail.Contains("multiple segments for one task run", StringComparison.Ordinal)),
                $"ambiguous Raw evidence must be visible and block result reconstruction: {first}");
            Assert(Directory.EnumerateFiles(Path.Combine(root, "raw"), "*.ndjson", SearchOption.AllDirectories).Count() == 2,
                "ambiguous recovery must retain both valid Raw files");
            using (var db = OpenCatalog(root))
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM task_run_raw_outputs WHERE task_run_id=$run";
                command.Parameters.AddWithValue("$run", runId);
                Assert(Convert.ToInt32(command.ExecuteScalar()) == 0,
                    "ambiguous recovery must not partially insert a task result manifest");
            }

            var second = recovered.RecoverRawSegments();
            Assert(second.FailedFiles == 1 && Directory.EnumerateFiles(Path.Combine(root, "raw"), "*.ndjson", SearchOption.AllDirectories).Count() == 2,
                $"repeated ambiguous recovery must remain visible and retain evidence: {second}");
            using (var db = OpenCatalog(root))
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM task_run_raw_outputs WHERE task_run_id=$run";
                command.Parameters.AddWithValue("$run", runId);
                Assert(Convert.ToInt32(command.ExecuteScalar()) == 0,
                    "repeated ambiguous recovery must remain idempotent without producing a partial result");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ReconcilesLegacyWindowsRawPathAliases()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-h3c-path-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = TestSettings(root);
        try
        {
            var catalog = new Catalog(settings);
            catalog.Initialize();
            catalog.ProbeRawWritable();
            var profileId = Guid.NewGuid().ToString("N");
            var fingerprint = "SHA256:" + Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()).TrimEnd('=');
            catalog.CreateH3cDeviceProfile(new(profileId, "192.0.2.30", 22, "readonly", "LAB_SWITCH_C",
                fingerprint, true, 0, 0));
            catalog.CreateTaskDefinition("h3c-path-alias", "H3C_SSH_READONLY", profileId,
                H3cCommandPolicy.DisplayVersion, TimeSpan.FromSeconds(10));
            var runId = catalog.CreateTaskRun("h3c-path-alias");
            catalog.TransitionTaskRun(runId, "CONNECTING");
            catalog.TransitionTaskRun(runId, "RUNNING");
            const string output = "Synthetic Comware response for legacy Windows path test";
            var definition = catalog.GetTaskDefinition("h3c-path-alias")!;
            var manifest = await new H3cTaskRawResultSink(catalog).PersistAsync(runId, definition, output, "COMPLETE");
            var physicalRaw = Path.Combine(root, manifest.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert(File.Exists(physicalRaw), "test setup must create one sealed H3C Raw file");

            var legacyPath = manifest.RelativePath.Replace('/', '\\');
            using (var db = OpenCatalog(root))
            using (var alias = db.CreateCommand())
            {
                alias.CommandText = @"UPDATE raw_segments SET relative_path=$legacy WHERE relative_path=$current;
                    INSERT INTO raw_segments(id,source_id,relative_path,state,first_received_ms,last_received_ms,record_count,
                        byte_count,sha256,sealed_ms,checkpoint_size,checkpoint_write_ticks,last_verified_ms,task_run_id)
                    SELECT $id,source_id,$current,state,first_received_ms,last_received_ms,record_count,byte_count,sha256,
                        sealed_ms,checkpoint_size,checkpoint_write_ticks,last_verified_ms,task_run_id
                    FROM raw_segments WHERE relative_path=$legacy";
                alias.Parameters.AddWithValue("$legacy", legacyPath);
                alias.Parameters.AddWithValue("$current", manifest.RelativePath);
                alias.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                alias.ExecuteNonQuery();
            }

            var recovered = new Catalog(settings);
            recovered.Initialize();
            var first = recovered.RecoverRawSegments();
            Assert(first.FailedFiles == 0 && first.IndexedOrphanFiles == 0 && File.Exists(physicalRaw),
                $"legacy separator aliases for one Raw file must reconcile without re-indexing or deleting Raw: {first}");
            Assert(recovered.GetRawCounts().Finalized == 1,
                "Windows Raw health counts must treat slash aliases as one physical segment");
            using (var db = OpenCatalog(root))
            using (var command = db.CreateCommand())
            {
                command.CommandText = @"SELECT
                    (SELECT COUNT(*) FROM raw_segments WHERE task_run_id=$run),
                    (SELECT COUNT(*) FROM task_run_raw_outputs WHERE task_run_id=$run)";
                command.Parameters.AddWithValue("$run", runId);
                using var reader = command.ExecuteReader();
                Assert(reader.Read() && reader.GetInt32(0) == 2 && reader.GetInt32(1) == 1,
                    "legacy path aliases may remain as metadata evidence but recovery must not add another Raw row or duplicate the result");
            }
            var second = recovered.RecoverRawSegments();
            Assert(second.FailedFiles == 0 && File.Exists(physicalRaw),
                $"repeated startup must remain idempotent for legacy separator aliases: {second}");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string DescribeH3cRawEvidence(string root)
    {
        var onDisk = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "raw"), "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetExtension(path) is ".open" or ".ndjson"))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var end = bytes.Length > 0 && bytes[^1] == (byte)'\n' ? bytes.Length - 1 : bytes.Length;
                using var document = System.Text.Json.JsonDocument.Parse(bytes.AsMemory(0, end));
                var record = document.RootElement;
                if (record.TryGetProperty("record_type", out var type) && type.GetString() == "h3c_command")
                    onDisk.Add($"{record.GetProperty("task_run_id").GetString()}={Path.GetRelativePath(root, path).Replace('\\', '/')}");
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                onDisk.Add($"unreadable:{Path.GetRelativePath(root, path).Replace('\\', '/')}");
            }
        }

        var indexed = new List<string>();
        using (var db = OpenCatalog(root))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT task_run_id,relative_path FROM raw_segments WHERE source_id LIKE 'h3c-%' AND task_run_id IS NOT NULL ORDER BY task_run_id,relative_path";
            using var reader = command.ExecuteReader();
            while (reader.Read()) indexed.Add($"{reader.GetString(0)}={reader.GetString(1)}");
        }
        return $"disk=[{string.Join(",", onDisk)}]; catalog=[{string.Join(",", indexed)}]";
    }

    private static async Task VerifiesH3cTransportLifecycle(H3cDeviceProfileSnapshot profile, string fingerprint)
    {
        var activeBefore = H3cCollectionMetrics.Snapshot().ActiveSshSessions;
        var goodFactory = new FakeH3cSshTransportFactory(fingerprint);
        var good = new H3cSshDeviceSession(profile, new FixedH3cCredentialProvider("synthetic-test-secret"), goodFactory);
        await good.ConnectAsync(CancellationToken.None);
        Assert(H3cCollectionMetrics.Snapshot().ActiveSshSessions == activeBefore + 1,
            "authenticated SSH session should be reflected in the active-session gauge");
        Assert(await good.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None) == "simulated Comware output",
            "read-only Exec command should return its simulated response");
        await good.DisposeAsync();
        Assert(goodFactory.LastTransport!.DisposeCount == 1 &&
               H3cCollectionMetrics.Snapshot().ActiveSshSessions == activeBefore,
            "normal session completion must release the transport and active-session gauge");

        var wrongKeyFactory = new FakeH3cSshTransportFactory("changed-host-key");
        var wrongKey = new H3cSshDeviceSession(profile, new FixedH3cCredentialProvider("synthetic"), wrongKeyFactory);
        try
        {
            await wrongKey.ConnectAsync(CancellationToken.None);
            throw new InvalidOperationException("changed host key must not be trusted");
        }
        catch (H3cHostKeyMismatchException) { }
        finally { await wrongKey.DisposeAsync(); }
        Assert(wrongKeyFactory.LastTransport!.DisposeCount == 1 && wrongKeyFactory.LastTransport.IsConnected == false,
            "host-key mismatch must leave no connected transport behind");

        var missingKeyFactory = new FakeH3cSshTransportFactory(null);
        var missingKey = new H3cSshDeviceSession(profile, new FixedH3cCredentialProvider("synthetic"), missingKeyFactory);
        try
        {
            await missingKey.ConnectAsync(CancellationToken.None);
            throw new InvalidOperationException("missing server host key must be rejected");
        }
        catch (H3cHostKeyMissingException) { }
        finally { await missingKey.DisposeAsync(); }

        var authenticationFactory = new FakeH3cSshTransportFactory(fingerprint, failAuthentication: true);
        var authentication = new H3cSshDeviceSession(profile, new FixedH3cCredentialProvider("synthetic"), authenticationFactory);
        try
        {
            await authentication.ConnectAsync(CancellationToken.None);
            throw new InvalidOperationException("failed SSH authentication must remain a failed task");
        }
        catch (H3cAuthenticationFailedException) { }
        finally { await authentication.DisposeAsync(); }

        var cancelFactory = new FakeH3cSshTransportFactory(fingerprint, waitForConnectCancellation: true);
        var cancelled = new H3cSshDeviceSession(profile, new FixedH3cCredentialProvider("synthetic"), cancelFactory);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            var connect = cancelled.ConnectAsync(cancellation.Token);
            try
            {
                await connect.WaitAsync(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException("cancelled SSH connect should complete as cancelled");
            }
            catch (OperationCanceledException) { }
            Assert(connect.IsCompleted, "SSH connect cancellation must not leave a waiter or background task");
        }
        await cancelled.DisposeAsync();
        Assert(cancelFactory.LastTransport!.DisposeCount == 1 && !cancelFactory.LastTransport.IsConnected,
            "cancelled SSH connect must close and dispose the transport");

        var resetFactory = new FakeH3cSshTransportFactory(fingerprint, resetOnCommand: true);
        var reset = new H3cSshDeviceSession(profile, new FixedH3cCredentialProvider("synthetic"), resetFactory);
        await reset.ConnectAsync(CancellationToken.None);
        try
        {
            await reset.ExecuteAsync(H3cCommandPolicy.DisplayInterfaceBrief, CancellationToken.None);
            throw new InvalidOperationException("device reset must fail collection");
        }
        catch (IOException) { }
        try
        {
            await reset.ExecuteAsync("display current-configuration", CancellationToken.None);
            throw new InvalidOperationException("commands outside the SSH read-only allowlist must be rejected");
        }
        catch (ArgumentException) { }
        await reset.DisposeAsync();
        Assert(resetFactory.LastTransport!.DisposeCount == 1 && !resetFactory.LastTransport.IsConnected,
            "device disconnect must be followed by deterministic session cleanup");
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
            var staleDefinition = catalog.GetTaskDefinition("sim-echo-1")!;
            catalog.CreateTaskDefinition("sim-echo-1", "SIMULATED_DEVICE_ECHO", "sim://device-1",
                "show interfaces", TimeSpan.FromSeconds(3));
            AssertThrows<InvalidOperationException>(() => catalog.CreateTaskRun(staleDefinition));
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
                Assert(Convert.ToInt32(version.ExecuteScalar()) == 12, "v12 device batch operation migration should be present after repeated initialization");
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
            Assert(audit?.Outcome == "INTEGRITY_ISSUE", audit?.Detail ?? "expected low-priority audit to detect same-size, same-time tampering");
            Assert(catalog.GetRawCounts().Incomplete == 1 && File.Exists(tampered), "maintenance audit must retain and mark changed Raw evidence");
            Assert(catalog.GetIntegrityAuditCount() == 1, "checksum mismatch should have a durable audit record");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifiesMaintenanceFailureCanResolveAfterVerifiedRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-maintenance-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(TestSettings(root));
            catalog.Initialize();
            var relative = "raw/syslog/2026/10/19/retry.ndjson";
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var raw = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
            {
                received_ms = 1_800_000_000_010L,
                sender_ip = "192.0.2.60",
                sender_port = 514,
                payload_base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("maintenance retry fixture"))
            }) + "\n");
            File.WriteAllBytes(path, raw);
            Assert(catalog.RecoverRawSegments().IndexedOrphanFiles == 1, "valid fixture should be indexed before maintenance");

            var displaced = Path.Combine(root, "temporarily-unavailable.ndjson");
            File.Move(path, displaced);
            var failed = catalog.AuditOneFinalizedSegment();
            Assert(failed?.Outcome == "FAILED" && catalog.GetUnresolvedIntegrityIssueCount() == 1,
                "transiently missing maintenance input should record a visible unresolved audit");

            File.Move(displaced, path);
            var verified = catalog.AuditOneFinalizedSegment();
            Assert(verified?.Outcome == "VERIFIED" && catalog.GetUnresolvedIntegrityIssueCount() == 0,
                "a later full verification should resolve the prior MAINTENANCE_FAILED audit");
            Assert(catalog.RecentIntegrityAudits().Single().Disposition == "RESOLVED_VERIFIED",
                "successful retry should preserve an explicit auditable resolution");
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
            status.SetInterruptedTaskRuns(1);
            catalog.QuarantineIntegrityAudit(audit.Id, "test-operator", "Crash artifact reviewed and retained.");
            Assert(File.Exists(retained), "quarantine must preserve Raw bytes on disk");
            Assert(catalog.GetIntegrityAuditCount() == 1 && catalog.GetUnresolvedIntegrityIssueCount() == 0,
                "a disposition must remain auditable and stop counting as unresolved");
            var disposed = catalog.GetHealthSnapshot().RecentIntegrityAudits.Single();
            Assert(disposed.Disposition == "QUARANTINED" && disposed.DisposedBy == "test-operator" &&
                   disposed.DispositionDetail == "Crash artifact reviewed and retained." && disposed.ObservedSha256 == originalHash,
                "quarantine evidence must identify the actor, reason and preserved byte hash");
            status.ReassessIntegrityIssues(catalog.GetUnresolvedIntegrityIssueCount());
            Assert(status.HealthState == "DEGRADED" && status.HealthError?.Contains("task run", StringComparison.OrdinalIgnoreCase) == true,
                "clearing the Raw issue must preserve the independent interrupted-task degradation");
            status.SetInterruptedTaskRuns(0);
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

    private static void VerifiesFlushWaitDeadlineIsClamped()
    {
        var oldTimestamp = Stopwatch.GetTimestamp() - 2 * Stopwatch.Frequency;
        Assert(UdpSyslogWorker.RemainingFlushDelay(oldTimestamp) == TimeSpan.Zero,
            "a delayed writer must clamp an expired flush deadline instead of passing a negative timer duration");
        var current = UdpSyslogWorker.RemainingFlushDelay(Stopwatch.GetTimestamp());
        Assert(current >= TimeSpan.Zero && current <= UdpSyslogWorker.MaximumUnflushedDelay,
            "a live pending batch must retain a bounded nonnegative flush wait");
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
            var workerStarted = false;
            var workerStopped = false;
            try
            {
                await worker.StartAsync(shutdown.Token);
                workerStarted = true;
                await WaitUntil(() => status.CollectionState == "listening");
                using (var sender = new System.Net.Sockets.UdpClient())
                    await sender.SendAsync(new byte[] { 1, 2, 3, 4 }, new IPEndPoint(IPAddress.Loopback, port));
                await WaitUntil(() => stats.Flushed == 1, TimeSpan.FromSeconds(5));
                var open = Directory.GetFiles(catalog.RawRoot, "*.open", SearchOption.AllDirectories).Single();
                Assert(new FileInfo(open).Length > 0 && stats.QueueDepth == 0,
                    "a sparse packet must reach its synced open Raw segment without waiting for the next packet");
                await worker.StopAsync(CancellationToken.None);
                workerStopped = true;
                Assert(catalog.GetRawCounts().Finalized == 1 && stats.Flushed == 1,
                    "cooperative stop must seal and catalog the sparse Raw segment");
                Assert(!File.Exists(open) && Directory.GetFiles(catalog.RawRoot, "*.open", SearchOption.AllDirectories).Length == 0,
                    "a sealed Raw segment must release and rename every .open path before stop returns");
                var finalized = Directory.GetFiles(catalog.RawRoot, "*.ndjson", SearchOption.AllDirectories).Single();
                Assert(File.ReadLines(finalized).Count() == 1,
                    "a synced sparse packet must remain readable after cooperative seal");
            }
            finally
            {
                if (workerStarted && !workerStopped)
                {
                    try { await worker.StopAsync(CancellationToken.None); }
                    catch (Exception ex) { Console.Error.WriteLine($"WARN: sparse Syslog worker stop failed during test cleanup: {ex.Message}"); }
                }
            }
        }
        finally
        {
            try { await DeleteTestDirectoryWithRetryAsync(root); }
            catch (IOException ex) when (OperatingSystem.IsWindows())
            {
                // Cleanup is best effort here so a Windows file-lock diagnostic does not
                // replace the assertion or exception that caused the test to fail.
                Console.Error.WriteLine($"WARN: test directory cleanup was blocked: {ex.Message}");
            }
        }
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

    private static async Task MeasuresSimulatedDeviceConcurrencyWithSyslog()
    {
        const int datagrams = 128;
        foreach (var concurrency in new[] { 2, 4, 8, 16 })
        {
            var root = Path.Combine(Path.GetTempPath(), "netconsole-next-concurrency-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var port = GetFreeUdpPort();
                var settings = new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = port,
                    SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
                var catalog = new Catalog(settings);
                catalog.Initialize();
                catalog.ProbeRawWritable();
                var sessionFactory = new FakeSessionFactory(_ => new FakeBehavior(async (_, token) =>
                {
                    await Task.Delay(25, token);
                    return "simulated read-only result";
                }));
                await using var scheduler = new BoundedCollectionScheduler(sessionFactory, concurrency,
                    concurrency * 2, catalog);
                var stats = new IngressStats();
                var status = new RuntimeStatus();
                using var loggerFactory = LoggerFactory.Create(_ => { });
                var worker = new UdpSyslogWorker(settings, catalog, new RawSegmentWriterFactory(), stats, status,
                    loggerFactory.CreateLogger<UdpSyslogWorker>());
                var process = System.Diagnostics.Process.GetCurrentProcess();
                process.Refresh();
                var cpuBefore = process.TotalProcessorTime;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                using var shutdown = new CancellationTokenSource();
                await worker.StartAsync(shutdown.Token);
                await WaitUntil(() => status.CollectionState == "listening");
                var runIds = new List<string>();
                for (var index = 0; index < concurrency * 2; index++)
                {
                    Assert(scheduler.TrySchedule(Request("simulated-h3c-device", "display interface brief"), out var runId),
                        $"bounded simulated task admission failed at concurrency {concurrency}");
                    runIds.Add(runId);
                }
                using (var sender = new System.Net.Sockets.UdpClient())
                {
                    for (var index = 0; index < datagrams; index++)
                    {
                        await sender.SendAsync(new byte[128], new IPEndPoint(IPAddress.Loopback, port));
                        await Task.Delay(1);
                    }
                }
                await WaitUntil(() => stats.Flushed == datagrams && runIds.All(id =>
                    scheduler.Get(id)?.State is "SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED"),
                    TimeSpan.FromSeconds(15));
                await worker.StopAsync(CancellationToken.None);
                timer.Stop();
                process.Refresh();
                var endedRuns = runIds.Select(id => scheduler.Get(id)!).ToArray();
                var record = new
                {
                    scenario = "simulated_device_concurrency_plus_syslog",
                    maxConcurrency = concurrency,
                    activePeak = sessionFactory.MaximumActiveSessions,
                    elapsedMs = timer.ElapsedMilliseconds,
                    processCpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                    rssWorkingSetBytes = process.WorkingSet64,
                    threadCount = process.Threads.Count,
                    udpAttempted = datagrams,
                    udpReceived = stats.Received,
                    udpAccepted = stats.Accepted,
                    udpApplicationDropped = stats.Dropped,
                    rawPersisted = stats.Flushed,
                    queueHighWatermark = stats.QueueHighWatermark,
                    deviceTaskCount = endedRuns.Length,
                    deviceSucceeded = endedRuns.Count(run => run.State == "SUCCEEDED"),
                    deviceLatencyP50Ms = Percentile(endedRuns.Select(run => (double)(run.EndedMs!.Value - run.StartedMs!.Value)).ToArray(), .50),
                    deviceLatencyP95Ms = Percentile(endedRuns.Select(run => (double)(run.EndedMs!.Value - run.StartedMs!.Value)).ToArray(), .95),
                    dataDirectoryBytes = DirectoryBytes(root)
                };
                Console.WriteLine("PERF " + System.Text.Json.JsonSerializer.Serialize(record));
                Assert(record.activePeak == concurrency && record.deviceSucceeded == concurrency * 2,
                    $"simulated device concurrency did not respect the configured cap: {System.Text.Json.JsonSerializer.Serialize(record)}");
                Assert(record.udpReceived == datagrams && record.udpAccepted == datagrams &&
                       record.udpApplicationDropped == 0 && record.rawPersisted == datagrams,
                    $"paced Syslog regression during device concurrency {concurrency}: {System.Text.Json.JsonSerializer.Serialize(record)}");
            }
            finally { await DeleteTestDirectoryWithRetryAsync(root); }
        }
    }

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values);
        var index = (int)Math.Ceiling(percentile * values.Length) - 1;
        return values[Math.Clamp(index, 0, values.Length - 1)];
    }

    private static long DirectoryBytes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);

    private static async Task DeleteTestDirectoryWithRetryAsync(string root)
    {
        IOException? lastIoError = null;
        UnauthorizedAccessException? lastAccessError = null;
        for (var attempt = 0; attempt < 100 && Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException ex) when (OperatingSystem.IsWindows())
            {
                lastIoError = ex;
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException ex) when (OperatingSystem.IsWindows())
            {
                lastAccessError = ex;
                await Task.Delay(100);
            }
        }
        if (Directory.Exists(root))
        {
            var remaining = Directory.GetFiles(root, "*.open", SearchOption.AllDirectories);
            throw new IOException($"Could not remove test data root; remaining .open files: {string.Join(",", remaining.Select(Path.GetFileName))}",
                lastIoError ?? (Exception?)lastAccessError);
        }
    }

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

    private sealed class FakeArchiveDiskSpaceProvider(long? available) : IArchiveDiskSpaceProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _paths = new();

        public long? Available { get; set; } = available;
        public IReadOnlyList<string> Paths => _paths.ToArray();

        public long? GetAvailableFreeSpace(string path)
        {
            _paths.Enqueue(path);
            return Available;
        }
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

    private sealed class FixedH3cCredentialProvider(string? value) : IH3cCredentialProvider
    {
        public string? GetPassword(string reference) => value;
    }

    private sealed class BlockingAfterSealResultSink(ITaskResultSink inner) : ITaskResultSink
    {
        public TaskCompletionSource Sealed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<H3cRawOutputManifest> PersistAsync(string runId, TaskDefinitionSnapshot definition,
            string output, string captureStatus)
        {
            var manifest = await inner.PersistAsync(runId, definition, output, captureStatus);
            Sealed.TrySetResult();
            await Release.Task;
            return manifest;
        }
    }

    private sealed class FakeH3cSshTransportFactory(string? fingerprint, bool failAuthentication = false,
        bool waitForConnectCancellation = false, bool resetOnCommand = false) : IH3cSshTransportFactory
    {
        public FakeH3cSshTransport? LastTransport { get; private set; }
        public IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password)
        {
            Assert(password == "synthetic-test-secret" || password == "synthetic",
                "transport should receive only the resolved test credential");
            LastTransport = new FakeH3cSshTransport(fingerprint, failAuthentication, waitForConnectCancellation, resetOnCommand);
            return LastTransport;
        }
    }

    private sealed class FakeH3cSshTransport(string? fingerprint, bool failAuthentication,
        bool waitForConnectCancellation, bool resetOnCommand) : IH3cSshTransport
    {
        public bool IsConnected { get; private set; }
        public int DisposeCount { get; private set; }
        public int AbortCount { get; private set; }

        public async Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
        {
            if (waitForConnectCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (fingerprint is null) throw new H3cHostKeyMissingException();
            if (!verifyHostKey("ssh-ed25519", fingerprint)) throw new H3cHostKeyMismatchException();
            if (failAuthentication) throw new H3cAuthenticationFailedException();
            IsConnected = true;
        }

        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new IOException("simulated peer disconnect");
            if (resetOnCommand)
            {
                IsConnected = false;
                throw new IOException("simulated TCP reset during SSH exec");
            }
            return Task.FromResult("simulated Comware output");
        }

        public void Abort()
        {
            AbortCount++;
            IsConnected = false;
        }

        public void Dispose()
        {
            DisposeCount++;
            IsConnected = false;
        }
    }

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

    private sealed class FlushFailureStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new IOException("deterministic flush failure");
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new IOException("deterministic flush failure"));
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);
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

    private sealed class CancellationReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public void Release() => _release.TrySetResult(0);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            return new ValueTask<int>(_release.Task);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) Release();
            base.Dispose(disposing);
        }
    }

    private sealed class SynchronousSchedulerScope(FakeSessionFactory factory) : IDisposable
    {
        public BoundedCollectionScheduler Scheduler { get; } = new(factory, 1, 1);
        public void Dispose() => Scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
