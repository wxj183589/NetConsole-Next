using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class NetmikoRetryTransportTests
{
    private const string WorkerMode = "NETCONSOLE_NEXT_TEST_RETRY_WORKER";
    private const string KeyA = "SHA256:ERERERERERERERERERERERERERERERERERERERERERE";
    private const string KeyB = "SHA256:IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI";
    private const string KeyC = "SHA256:MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM";
    private static int _checks;
    internal static bool IsWorker => Environment.GetEnvironmentVariable(WorkerMode) == "1";

    internal static async Task RunWorkerAsync()
    {
        string? host = null;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var operation = request.RootElement.GetProperty("op").GetString();
            if (operation == "connect")
            {
                host = request.RootElement.GetProperty("host").GetString();
                if (host == "connect-exit") return;
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true,
                    result = new { driver = "hp_comware", host_key_type = "ssh-ed25519", host_key_sha256 = KeyA,
                        jump_host_key_sha256 = KeyA } }));
            }
            else if (host == "command-exit") return;
            else if (host == "preflight-loss") Console.WriteLine("{\"ok\":false,\"error\":\"ConnectionLostError\",\"failure_kind\":\"CONNECTION_LOST\"}");
            else if (host == "malformed") Console.WriteLine("{invalid-json");
            else Console.WriteLine("{\"ok\":true,\"result\":{}}");
            await Console.Out.FlushAsync();
        }
    }

    public static async Task RunAsync()
    {
        _checks = 0;
        await ConnectRetriesOnlyExplicitTransientFailures();
        await ReplaysOnlyTheCurrentExactReadOnlyCommand();
        await CommandFailuresDoNotReplay();
        await ReconnectBudgetSpansConnectAndCommands();
        await CancellationAbortAndDisposeStopRecovery();
        await AuthorizationAndObserverFailuresStopBeforeCommands();
        await CatalogHostKeysCommitOnlySuccessfulConnections();
        await ZteCatalogKeysRotateAcrossBothReconnects();
        await FactoryKeepsJumpConfigurationBoundToTheSession();
        VerifyVendorAndFailureClassification();
        await WorkerProtocolAndProcessExitAreNotSuccessfulOutput();
        Console.WriteLine($"NETMIKO_RETRY_TESTS={_checks}/{_checks} PASS");
    }

    private static H3cDeviceProfileSnapshot Profile(string host = "192.0.2.72") => new(
        Guid.NewGuid().ToString("N"), host, 22, "reader", "FIXTURE", KeyA, true, 0, 0,
        TerminalProtocol: "SSH", DeviceType: "hp_comware");

    private static async Task ConnectRetriesOnlyExplicitTransientFailures()
    {
        foreach (var error in new Exception[] { new H3cConnectionLostException(), new H3cConnectionTimeoutException(),
            new H3cJumpHostConnectionException("JUMP_HOST_UNREACHABLE", "synthetic"),
            new H3cJumpHostConnectionException("JUMP_HOST_CHANNEL_FAILED", "synthetic") })
        {
            var f = new FixtureFactory([new() { ConnectFailure = error }, new()]);
            using var recovery = f.CreateRetry();
            var boundaries = 0;
            recovery.BeforeReconnect = () => boundaries++;
            await recovery.ConnectAsync((_, _) => true, CancellationToken.None);
            Assert(f.Created.Count == 2 && f.Created[0].DisposeCount == 1 && boundaries == 1,
                "a transient connect failure closes its worker and checks the boundary before recreating it");
            Assert(f.Backoffs.SequenceEqual(new[] { TimeSpan.FromMilliseconds(250) }), "first recovery waits 250ms");
        }
        foreach (var error in NonRetryableFailures())
        {
            var f = new FixtureFactory([new() { ConnectFailure = error }, new()]);
            using var recovery = f.CreateRetry();
            await ThrowsSame(error, () => recovery.ConnectAsync((_, _) => true, CancellationToken.None));
            Assert(f.Created.Count == 1 && f.Created[0].DisposeCount == 1, "non-transient connection failure cannot recreate a worker");
        }
    }

    private static IEnumerable<Exception> NonRetryableFailures() => [
        new H3cAuthenticationFailedException(), new H3cHostKeyMismatchException(), new H3cHostKeyMissingException(),
        new H3cJumpHostKeyMismatchException(), new H3cJumpHostKeyMissingException(),
        new H3cJumpHostConnectionException("JUMP_HOST_AUTHENTICATION_FAILED", "synthetic"),
        new H3cJumpHostConnectionException("JUMP_HOST_FORWARD_DENIED", "synthetic"),
        new H3cJumpHostConnectionException("JUMP_HOST_PROTOCOL_FAILED", "synthetic"),
        new H3cJumpHostConnectionException("JUMP_HOST_CREDENTIAL_UNAVAILABLE", "synthetic"),
        new H3cJumpHostConnectionException("JUMP_HOST_CONFIGURATION_CHANGED", "synthetic"),
        new H3cProtocolNegotiationException(), new ArgumentException("driver"), new H3cWorkerProtocolException(),
        new H3cCommandTimeoutException(), new H3cCommandInterruptedException(), new H3cOutputLimitException(),
        new H3cPaginationDetectedException(), new H3cInvalidEncodingException(), new IOException("unproven failure"),
        new H3cPartialOutputException("partial", "PARTIAL", false), new OperationCanceledException()
    ];

    private static async Task ReplaysOnlyTheCurrentExactReadOnlyCommand()
    {
        var first = new Transport { Outputs = new Queue<object>(["version complete", new H3cConnectionLostException()]) };
        var second = new Transport { Key = KeyB, Outputs = new Queue<object>(["ports complete"]) };
        var f = new FixtureFactory([first, second]);
        using var recovery = f.CreateRetry();
        var keys = new List<string?>();
        await recovery.ConnectAsync((_, key) => { keys.Add(key); return true; }, CancellationToken.None);
        Assert(await recovery.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None) == "version complete", "first command completes once");
        Assert(await recovery.ExecuteAsync(H3cCommandPolicy.DisplayInterfaceBrief, CancellationToken.None) == "ports complete", "lost connection recovers the current read-only command");
        Assert(first.Commands.SequenceEqual(new[] { H3cCommandPolicy.DisplayVersion, H3cCommandPolicy.DisplayInterfaceBrief }) &&
            second.Commands.SequenceEqual(new[] { H3cCommandPolicy.DisplayInterfaceBrief }), "recovery never replays a completed command");
        Assert(keys.SequenceEqual(new[] { KeyA, KeyB }) && first.DisposeCount == 1, "each physical connection invokes the original host-key verifier");

        foreach (var command in new[] { "display version ", "DISPLAY VERSION", "display version; reboot", "display version\nreboot",
                     "display unknown", "reboot", "system-view", ZteCommandPolicy.ShowVersion })
        {
            var source = new Transport { Outputs = new Queue<object>([new H3cConnectionLostException()]) };
            var rejected = new FixtureFactory([source, new()]);
            using var transport = rejected.CreateRetry();
            await transport.ConnectAsync((_, _) => true, CancellationToken.None);
            await Throws<H3cConnectionLostException>(() => transport.ExecuteAsync(command, CancellationToken.None));
            Assert(rejected.Created.Count == 1, "only exact selected-vendor commands qualify for replay");
        }
    }

    private static async Task CommandFailuresDoNotReplay()
    {
        foreach (var error in NonRetryableFailures().Concat([new H3cConnectionTimeoutException()]))
        {
            var f = new FixtureFactory([new() { Outputs = new Queue<object>([error]) }, new()]);
            using var recovery = f.CreateRetry();
            await recovery.ConnectAsync((_, _) => true, CancellationToken.None);
            await ThrowsSame(error, () => recovery.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None));
            Assert(f.Created.Count == 1 && !recovery.IsConnected && f.Created[0].DisposeCount == 1,
                "timeout, partial, cancelled and unclassified command failures release without replay");
        }
    }

    private static async Task ReconnectBudgetSpansConnectAndCommands()
    {
        var f = new FixtureFactory([new() { ConnectFailure = new H3cConnectionTimeoutException() },
            new() { Outputs = new Queue<object>([new H3cConnectionLostException()]) },
            new() { Outputs = new Queue<object>(["recovered", new H3cConnectionLostException()]) }, new()]);
        using var recovery = f.CreateRetry();
        await recovery.ConnectAsync((_, _) => true, CancellationToken.None);
        Assert(await recovery.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None) == "recovered", "connect and command recovery share the same budget");
        await Throws<H3cConnectionLostException>(() => recovery.ExecuteAsync(H3cCommandPolicy.DisplayInterfaceBrief, CancellationToken.None));
        Assert(f.Created.Count == 3 && f.Backoffs.SequenceEqual(new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(1000) }),
            "a session has at most two extra connections with 250ms and 1000ms backoffs");
        Assert(f.Created.All(item => item.DisposeCount == 1), "exhaustion releases all physical workers once");

        var budget = new NetmikoReconnectBudget();
        var ssh = new FixtureFactory([new() { ConnectFailure = new H3cConnectionTimeoutException() },
            new() { ConnectFailure = new H3cConnectionTimeoutException() }, new() { ConnectFailure = new H3cConnectionTimeoutException() }]);
        using var firstProtocol = ssh.CreateRetry(); firstProtocol.ReconnectBudget = budget;
        await Throws<H3cConnectionTimeoutException>(() => firstProtocol.ConnectAsync((_, _) => true, CancellationToken.None));
        var telnet = new FixtureFactory([new() { ConnectFailure = new H3cConnectionTimeoutException() }, new()]);
        using var alternate = telnet.CreateRetry(); alternate.ReconnectBudget = budget;
        await Throws<H3cConnectionTimeoutException>(() => alternate.ConnectAsync((_, _) => true, CancellationToken.None));
        Assert(telnet.Created.Count == 1, "AUTO alternate transport cannot reset the session recovery budget");
    }

    private static async Task CancellationAbortAndDisposeStopRecovery()
    {
        foreach (var stopping in new[] { "cancel", "abort", "dispose" })
        {
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var f = new FixtureFactory([new() { ConnectFailure = new H3cConnectionLostException() }, new()]);
            using var recovery = f.CreateRetry(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); });
            var pending = recovery.ConnectAsync((_, _) => true, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (stopping == "cancel") cancellation.Cancel();
            else if (stopping == "abort") recovery.Abort();
            else recovery.Dispose();
            await Throws<OperationCanceledException>(() => pending);
            Assert(f.Created.Count == 1 && f.Created[0].DisposeCount == 1, "cancellation, Abort and Dispose cancel backoff without creating another worker");
        }

        foreach (var stopping in new[] { "abort", "dispose" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new Transport { Connecting = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); } };
            var f = new FixtureFactory([source, new()]);
            using var recovery = f.CreateRetry();
            var pending = recovery.ConnectAsync((_, _) => true, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(Task.Run(() => { if (stopping == "abort") recovery.Abort(); else recovery.Dispose(); }),
                Task.Run(recovery.Dispose));
            await Throws<OperationCanceledException>(() => pending);
            Assert(source.DisposeCount == 1 && f.Created.Count == 1, "concurrent Abort/Dispose and connect completion release the worker once");
        }
    }

    private static async Task AuthorizationAndObserverFailuresStopBeforeCommands()
    {
        foreach (var duringConnect in new[] { false, true })
        {
            var failure = new InvalidOperationException("scope revoked");
            var source = new Transport { ConnectFailure = duringConnect ? new H3cConnectionLostException() : null,
                Outputs = new Queue<object>([new H3cConnectionLostException()]) };
            var f = new FixtureFactory([source, new()]);
            using var recovery = f.CreateRetry();
            recovery.BeforeReconnect = () => throw failure;
            if (duringConnect) await ThrowsSame(failure, () => recovery.ConnectAsync((_, _) => true, CancellationToken.None));
            else
            {
                await recovery.ConnectAsync((_, _) => true, CancellationToken.None);
                await ThrowsSame(failure, () => recovery.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None));
            }
            Assert(f.Created.Count == 1, "revocation blocks initial and later reconnects before the next worker");
        }
        var original = new Transport { Outputs = new Queue<object>([new H3cConnectionLostException()]) };
        var reconnected = new Transport();
        var observer = new FixtureFactory([original, reconnected, new()]);
        using var transport = observer.CreateRetry();
        transport.Reconnected = () => throw new H3cHostKeyMismatchException();
        await transport.ConnectAsync((_, _) => true, CancellationToken.None);
        await Throws<H3cHostKeyMismatchException>(() => transport.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None));
        Assert(observer.Created.Count == 2 && reconnected.DisposeCount == 1 && reconnected.Commands.Count == 0,
            "a failed host-key commit releases the new connection and never sends the command");
    }

    private static async Task CatalogHostKeysCommitOnlySuccessfulConnections()
    {
        using var data = new CatalogFixture();
        var profile = Profile() with { HostKeySha256 = "" };
        data.Catalog.CreateH3cDeviceProfile(profile);
        var failed = new Transport { Key = KeyC, FailureAfterVerification = new H3cWorkerProtocolException() };
        var f = new FixtureFactory([new() { Outputs = new Queue<object>([new H3cConnectionLostException()]) },
            new() { Key = KeyB, Outputs = new Queue<object>(["rotated", new H3cConnectionLostException()]) }, failed]);
        await using var session = new H3cSshDeviceSession(profile, new FixedCredentials(), f, data.Catalog);
        var boundaries = 0; session.SetOperationBoundary(() => boundaries++);
        await session.ConnectAsync(CancellationToken.None);
        Assert(data.Catalog.GetH3cDeviceProfile(profile.Id)!.HostKeySha256 == KeyA, "Catalog commits the first successful physical connection");
        Assert(await session.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None) == "rotated" &&
            data.Catalog.GetH3cDeviceProfile(profile.Id)!.HostKeySha256 == KeyB, "Catalog automatically commits a verified replacement key on reconnect");
        await Throws<H3cWorkerProtocolException>(() => session.ExecuteAsync(H3cCommandPolicy.DisplayInterfaceBrief, CancellationToken.None));
        var history = data.Catalog.SshHostKeyHistory(profile.Id);
        using var db = data.Open();
        using var pending = db.CreateCommand();
        pending.CommandText = "SELECT COUNT(*) FROM ssh_host_key_pending WHERE state='PENDING'";
        Assert(boundaries == 3 && data.Catalog.GetH3cDeviceProfile(profile.Id)!.HostKeySha256 == KeyB &&
            history.Any(item => item.Sha256 == KeyC && item.State == "FAILED") && Convert.ToInt64(pending.ExecuteScalar()) == 0,
            "failed reconnect marks pending key failed and retains the last effective fingerprint");
    }

    private static async Task ZteCatalogKeysRotateAcrossBothReconnects()
    {
        using var data = new CatalogFixture();
        var profile = new ZteDeviceProfileSnapshot(Guid.NewGuid().ToString("N"), "192.0.2.76", 22,
            "reader", "FIXTURE", "", true, 0, 0);
        data.Catalog.CreateZteDeviceProfile(profile, "synthetic");
        var f = new FixtureFactory([new() { Outputs = new Queue<object>([new H3cConnectionLostException()]) },
            new() { Key = KeyB, Outputs = new Queue<object>(["second", new H3cConnectionLostException()]) },
            new() { Key = KeyC, Outputs = new Queue<object>(["third"]) }], ZteCommandPolicy.IsAllowed);
        await using var session = new ZteSshDeviceSession(profile, new FixedCredentials(), f, data.Catalog);
        var boundaries = 0; session.SetOperationBoundary(() => boundaries++);
        await session.ConnectAsync(CancellationToken.None);
        Assert(data.Catalog.GetZteDeviceProfile(profile.Id)!.HostKeySha256 == KeyA, "ZTE first trust commits to its own Catalog record");
        Assert(await session.ExecuteAsync(ZteCommandPolicy.ShowVersion, CancellationToken.None) == "second" &&
            data.Catalog.GetZteDeviceProfile(profile.Id)!.HostKeySha256 == KeyB, "ZTE reconnect key commits after first rotation");
        Assert(await session.ExecuteAsync(ZteCommandPolicy.ShowInterfaceBrief, CancellationToken.None) == "third" &&
            data.Catalog.GetZteDeviceProfile(profile.Id)!.HostKeySha256 == KeyC && boundaries == 3,
            "ZTE refreshes expected fingerprint across both permitted reconnects");
    }

    private static void VerifyVendorAndFailureClassification()
    {
        Assert((Exception)new H3cConnectionLostException() is not IOException &&
            (Exception)new H3cJumpHostConnectionException("JUMP_HOST_UNREACHABLE", "synthetic") is not IOException,
            "network connection failures cannot be mistaken for storage IO failures");
        var h3c = Profile(); var zte = h3c with { DeviceType = "zte_zxros" };
        Assert(NetmikoWorkerH3cSshTransportFactory.CanReplay(h3c, "h3c", H3cCommandPolicy.DisplayVersion) &&
            !NetmikoWorkerH3cSshTransportFactory.CanReplay(h3c, "h3c", ZteCommandPolicy.ShowVersion) &&
            NetmikoWorkerH3cSshTransportFactory.CanReplay(zte, "zte", ZteCommandPolicy.ShowVersion) &&
            !NetmikoWorkerH3cSshTransportFactory.CanReplay(zte, "zte", H3cCommandPolicy.DisplayVersion) &&
            !NetmikoWorkerH3cSshTransportFactory.CanReplay(h3c, "huawei", H3cCommandPolicy.DisplayVersion) &&
            !NetmikoWorkerH3cSshTransportFactory.CanReplay(h3c with { DeviceType = "cisco_ios" }, "h3c", H3cCommandPolicy.DisplayVersion),
            "replay is selected by actual vendor and exact supported driver, never a union of commands");
        foreach (var connecting in new[] { false, true })
        {
            Assert(NetmikoWorkerH3cSshTransport.MapWorkerFailure("EOFError", connecting: connecting).GetType() ==
                (connecting ? typeof(H3cConnectionLostException) : typeof(H3cCommandInterruptedException)), "legacy EOF is classified by operation stage");
            Assert(NetmikoWorkerH3cSshTransport.MapWorkerFailure("ReadTimeout", "COMMAND_TIMEOUT", connecting) is H3cCommandTimeoutException,
                "prompt/read timeout never becomes a reconnectable connection timeout");
            Assert(NetmikoWorkerH3cSshTransport.MapWorkerFailure("OSError", "OSError", connecting) is H3cWorkerProtocolException &&
                NetmikoWorkerH3cSshTransport.MapWorkerFailure("ValueError", connecting: connecting) is ArgumentException &&
                NetmikoWorkerH3cSshTransport.MapWorkerFailure("Unknown", connecting: connecting) is H3cWorkerProtocolException,
                "unknown IO, invalid arguments and worker protocol errors cannot authorize recovery");
        }
    }

    private static async Task FactoryKeepsJumpConfigurationBoundToTheSession()
    {
        using var data = new CatalogFixture();
        var initial = new SiteSshJumpHostSnapshot("local", "192.0.2.80", 22, "jump", "FIXTURE", "synthetic", "", true, 0);
        data.Catalog.UpsertSiteSshJumpHost(initial);
        var profile = Profile();
        using var bound = new NetmikoWorkerH3cSshTransportFactory(data.Catalog).Create(profile, "synthetic");
        data.Catalog.UpsertSiteSshJumpHost(initial with { Host = "192.0.2.81" });
        await Throws<InvalidOperationException>(() => bound.ConnectAsync((_, _) => true, CancellationToken.None));
        Assert(NetmikoWorkerBudget.AvailableSlots == 16, "changed Jump business configuration is rejected before a worker starts");
    }

    private static async Task WorkerProtocolAndProcessExitAreNotSuccessfulOutput()
    {
        var names = new[] { WorkerMode, "NETCONSOLE_NEXT_NETMIKO_PYTHON", "NETCONSOLE_NEXT_NETMIKO_WORKER" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var slots = NetmikoWorkerBudget.AvailableSlots;
        try
        {
            Environment.SetEnvironmentVariable(WorkerMode, "1");
            Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_PYTHON", "dotnet");
            Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_WORKER", typeof(NetmikoRetryTransportTests).Assembly.Location);
            foreach (var mode in new[] { "command-exit", "malformed", "missing-output" })
            {
                var creates = 0;
                using var transport = new NetmikoRetryTransport(() => { creates++; return new NetmikoWorkerH3cSshTransport(Profile(mode), "synthetic"); },
                    H3cCommandPolicy.IsAllowed, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
                await transport.ConnectAsync((_, key) => key == KeyA, CancellationToken.None);
                if (mode == "command-exit") await Throws<H3cCommandInterruptedException>(() => transport.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None));
                else await Throws<H3cWorkerProtocolException>(() => transport.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None));
                Assert(creates == 1, "worker EOF, malformed JSON and missing output after a request never replay or return empty success");
            }
            var attempts = 0;
            using var connect = new NetmikoRetryTransport(() => { attempts++; return new NetmikoWorkerH3cSshTransport(Profile("connect-exit"), "synthetic"); },
                H3cCommandPolicy.IsAllowed, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
            await Throws<H3cConnectionLostException>(() => connect.ConnectAsync((_, _) => true, CancellationToken.None));
            Assert(attempts == 3, "worker exit during initial connect observes the shared two-reconnect limit");

            using var data = new CatalogFixture();
            var jump = new SiteSshJumpHostSnapshot("local", "192.0.2.80", 22, "jump", "FIXTURE", "synthetic", "", true, 0);
            data.Catalog.UpsertSiteSshJumpHost(jump);
            using (var telnet = new NetmikoWorkerH3cSshTransportFactory(data.Catalog).Create(Profile() with { TerminalProtocol = "TELNET", DeviceType = "hp_comware_telnet" }, "synthetic"))
            {
                data.Catalog.UpsertSiteSshJumpHost(jump with { Host = "192.0.2.81" });
                var verified = false;
                await telnet.ConnectAsync((_, _) => { verified = true; return true; }, CancellationToken.None);
                Assert(telnet.IsConnected && !verified && data.Catalog.GetSiteSshJumpHost("local")!.HostKeySha256 == "",
                    "Telnet never reads or validates an unrelated SSH Jump configuration or key");
            }
            data.Catalog.UpsertSiteSshJumpHost(jump);
            using (var routed = new NetmikoWorkerH3cSshTransportFactory(data.Catalog).Create(Profile("preflight-loss"), "synthetic"))
            {
                var verified = 0;
                await routed.ConnectAsync((_, _) => { verified++; return true; }, CancellationToken.None);
                Assert(data.Catalog.GetSiteSshJumpHost("local")!.HostKeySha256 == KeyA, "initial routed connection records Jump first trust");
                data.Catalog.UpsertSiteSshJumpHost(jump with { HostKeySha256 = KeyB });
                await Throws<H3cConnectionLostException>(() => routed.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None));
                Assert(verified == 3 && data.Catalog.GetSiteSshJumpHost("local")!.HostKeySha256 == KeyA,
                    "reconnect refreshes Jump fingerprint snapshots for CAS while retaining the original business endpoint and two-reconnect budget");
            }
            await JumpConfigurationCasFailureCannotDisableCatalog();
        }
        finally { foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
        for (var i = 0; i < 100 && NetmikoWorkerBudget.AvailableSlots != slots; i++) await Task.Delay(20);
        Assert(NetmikoWorkerBudget.AvailableSlots == slots, "all recovery processes exit and return the global worker slots");
    }

    private static async Task JumpConfigurationCasFailureCannotDisableCatalog()
    {
        using var data = new CatalogFixture();
        var jump = new SiteSshJumpHostSnapshot("local", "192.0.2.80", 22, "jump", "FIXTURE", "synthetic", "", true, 0);
        data.Catalog.UpsertSiteSshJumpHost(jump);
        var factory = new JumpCasSessionFactory(data.Catalog, jump);
        var persistenceFailures = 0;
        await using var scheduler = new BoundedCollectionScheduler(factory, 1, 1, data.Catalog,
            persistenceFailure: error => { Interlocked.Increment(ref persistenceFailures); data.Catalog.MarkUnavailable(error); });
        Assert(scheduler.TrySchedule(new("jump-cas", H3cCommandPolicy.DisplayVersion, TimeSpan.FromSeconds(5)), out var id),
            "Jump configuration CAS regression task is admitted");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        DeviceCollectionSnapshot result;
        while (true)
        {
            result = scheduler.Get(id)!;
            if (result.State is "SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMED_OUT") break;
            await Task.Delay(10, timeout.Token);
        }
        Assert(factory.Failure is H3cJumpHostConnectionException { Code: "JUMP_HOST_CONFIGURATION_CHANGED" } &&
            result.State == "FAILED" && result.Error == nameof(H3cJumpHostConnectionException),
            "the real worker transport rejects the stale Jump fingerprint CAS as a configuration failure");
        Assert(factory.Attempts == 1 && factory.Commands == 0,
            "a changed Jump configuration cannot retry a worker or execute a business command");
        Assert(Volatile.Read(ref persistenceFailures) == 0, "Jump configuration changes never call the scheduler persistence-failure handler");
        var current = data.Catalog.GetSiteSshJumpHost("local")!;
        Assert(current.Host == "192.0.2.81" && current.HostKeySha256 == "",
            "Catalog remains available and preserves the edited Jump endpoint without publishing the stale fingerprint");
    }

    private sealed class JumpCasSessionFactory(Catalog catalog, SiteSshJumpHostSnapshot jump) : IDeviceSessionFactory
    {
        public int Attempts;
        public int Commands;
        public Exception? Failure;
        public IDeviceSession Create(string target) => new Session(this, new NetmikoRetryTransport(
            () => { Attempts++; return new NetmikoWorkerH3cSshTransport(Profile(), "synthetic", jump, catalog); },
            H3cCommandPolicy.IsAllowed));
        private bool VerifyHostKey(string? type, string? key)
        {
            catalog.UpsertSiteSshJumpHost(jump with { Host = "192.0.2.81" });
            return key == KeyA;
        }
        private sealed class Session(JumpCasSessionFactory owner, NetmikoRetryTransport transport) : IDeviceSession
        {
            public async Task ConnectAsync(CancellationToken cancellationToken)
            {
                try { await transport.ConnectAsync(owner.VerifyHostKey, cancellationToken); }
                catch (Exception error) { owner.Failure = error; throw; }
            }
            public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
            {
                owner.Commands++;
                return transport.ExecuteAsync(command, cancellationToken);
            }
            public ValueTask DisposeAsync() { transport.Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Transport : IH3cSshTransport
    {
        public string Key { get; init; } = KeyA;
        public Exception? ConnectFailure { get; init; }
        public Exception? FailureAfterVerification { get; init; }
        public Func<CancellationToken, Task>? Connecting { get; init; }
        public Queue<object> Outputs { get; init; } = new(["complete"]);
        public List<string> Commands { get; } = [];
        public bool IsConnected { get; private set; }
        public int DisposeCount;
        public async Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Connecting is not null) await Connecting(cancellationToken);
            if (ConnectFailure is not null) throw ConnectFailure;
            if (!verifyHostKey("ssh-ed25519", Key)) throw new H3cHostKeyMismatchException();
            if (FailureAfterVerification is not null) throw FailureAfterVerification;
            IsConnected = true;
        }
        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Commands.Add(command);
            var value = Outputs.Dequeue();
            return value is Exception error ? Task.FromException<string>(error) : Task.FromResult((string)value);
        }
        public void Abort() => IsConnected = false;
        public void Dispose() { Interlocked.Increment(ref DisposeCount); IsConnected = false; }
    }

    private sealed class FixtureFactory(IReadOnlyList<Transport> transports, Func<string, bool>? policy = null) : IH3cSshTransportFactory
    {
        public List<Transport> Created { get; } = [];
        public List<TimeSpan> Backoffs { get; } = [];
        public IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password) => CreateRetry();
        public NetmikoRetryTransport CreateRetry(Func<TimeSpan, CancellationToken, Task>? delaying = null) => new(
            () => { var next = transports[Created.Count]; Created.Add(next); return next; }, policy ?? H3cCommandPolicy.IsAllowed,
            delaying ?? ((backoff, token) => { token.ThrowIfCancellationRequested(); Backoffs.Add(backoff); return Task.CompletedTask; }));
    }

    private sealed class FixedCredentials : IH3cCredentialProvider { public string? GetPassword(string reference) => "synthetic"; }
    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _root = Path.Combine("D:\\study\\NetConsole-Next-Workspace", "test-data", "netmiko-retry-tests", Guid.NewGuid().ToString("N"));
        public Catalog Catalog { get; }
        public SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "catalog.db"), Pooling = false }.ToString());
            db.Open(); return db;
        }
        public CatalogFixture()
        {
            Catalog = new Catalog(new RuntimeSettings { DataRoot = _root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 65536 });
            Catalog.Initialize();
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { _checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static async Task ThrowsSame(Exception expected, Func<Task> action)
    {
        try { await action(); } catch (Exception actual) when (ReferenceEquals(actual, expected)) { _checks++; return; }
        throw new InvalidOperationException("Original failure must be preserved: " + expected.GetType().Name);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; }
}
