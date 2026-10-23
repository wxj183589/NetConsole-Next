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

    public static async Task RunAsync()
    {
        await EnforcesQueueAndConcurrencyLimits();
        await CancelsQueuedAndRunningRequests();
        await TimesOutAndDisposesSession();
        await ReportsConnectionFailuresAndDisposesSession();
        await StopsActiveSessionsDuringSchedulerShutdown();
        await PersistsDefinitionsRunsAndRestartState();
        VerifiesH3cGoldenParsersPinsAndAlgorithmPolicy();
        await VerifiesH3cOutputCollectionLimitsAndDisconnects();
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
        VerifiesRollingLogSizeAndRetention();
        await TerminalCoreTests.RunAsync();
        RejectsInvalidRequests();
        Console.WriteLine("PASS: bounded/persisted scheduling, H3C parser and Raw output, migration, incremental recovery, SQLite/permission fault injection, queue rejection, timeout, cancellation and session lifecycle");
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
                Assert(Convert.ToInt32(version.ExecuteScalar()) == 9, "v9 Web authentication migration should be present after repeated initialization");
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
