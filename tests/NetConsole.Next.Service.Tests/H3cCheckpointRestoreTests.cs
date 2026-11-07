using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;

internal static class H3cCheckpointRestoreTests
{
    private static readonly WebUserIdentity Actor = new("local-api-token", "local-api-token", "SUPERADMIN", "test");
    private const string Original = "#\n version 7.1.070, Alpha 7170\n#\n sysname fixture\n#\ninterface GigabitEthernet1/0/48\n description original\n#\nreturn\n";
    public static async Task RunAsync()
    {
        await Success();
        await UncertainWriteRecovery();
        await RestoreFailureRecovery();
        await RollbackFailure();
        await ServiceMismatch();
        await InvalidCheckpointTables();
        await MissingServiceBaseline();
        await BaselineDrift();
        await NoCheckpointCreated();
        await UnverifiedReplaceBlocked();
        await RawEchoNoCheckpointCreated();
        await RawEchoCheckpointTable();
        RawEchoAndPromptForms();
        RejectUnexpectedTranscriptContent();
        Console.WriteLine("H3C_CHECKPOINT_RESTORE_TESTS=14/14 PASS");
    }
    private static async Task Success()
    {
        using var fixture = new Fixture();
        var result = await fixture.Run();
        Assert(result.State == "SERVICE_VERIFIED" && result.ConfigWriteSucceeded && result.ConfigReadbackVerified && result.ServiceVerified &&
            !result.RollbackVerified && result.CheckpointId == "1000000001", "success requires native checkpoint + controlled write + full readback + independent service probes");
        Assert(result.BeforeBackupId is not null && result.ChangedBackupId is not null && result.RestoredBackupId is not null &&
            fixture.Factory.Text == Original && fixture.Factory.Opens == 3 && fixture.Factory.Restores == 1 && fixture.Factory.Descriptions == 1,
            "normal path must reconnect after exact checkpoint replacement and restore the full original");
        Assert(fixture.Service.CheckpointOperations(fixture.Profile, Actor, P2B5SiteScopeDecision.Global).Single().Id == result.Id,
            "checkpoint operation evidence must survive as actor-scoped history");
    }
    private static async Task UncertainWriteRecovery()
    {
        using var fixture = new Fixture(); fixture.Factory.Mode = "write-throws";
        var result = await fixture.Run();
        Assert(result.State == "ROLLBACK_VERIFIED" && result.RollbackVerified && !result.ConfigWriteSucceeded && !result.ServiceVerified && fixture.Factory.Text == Original,
            "uncertain description write must use exact device-native checkpoint recovery and report rollback separately");
    }
    private static async Task RestoreFailureRecovery()
    {
        using var fixture = new Fixture(); fixture.Factory.Mode = "restore-once-throws";
        var result = await fixture.Run();
        Assert(result.State == "ROLLBACK_VERIFIED" && result.RollbackVerified && fixture.Factory.Restores == 2 && fixture.Factory.Text == Original,
            "uncertain native replacement must attempt exactly one independent recovery with the same checkpoint");
    }
    private static async Task RollbackFailure()
    {
        using var fixture = new Fixture(); fixture.Factory.Mode = "restore-always-fails";
        var result = await fixture.Run();
        Assert(result.State == "ROLLBACK_FAILED" && !result.RollbackVerified && !result.ConfigReadbackVerified && fixture.Factory.Restores == 2,
            "failed replacement and failed recovery must never claim verified configuration");
    }
    private static async Task ServiceMismatch()
    {
        using var fixture = new Fixture(); fixture.Factory.Mode = "service-drift";
        var result = await fixture.Run();
        Assert(result.ConfigReadbackVerified && !result.ServiceVerified && !result.RollbackVerified && result.State == "ROLLBACK_FAILED" &&
            result.Error!.Contains("CONFIG_CHECKPOINT_SERVICE_CHECK_FAILED"), "full configuration hash equality cannot substitute for interface/LLDP service validation");
    }
    private static async Task InvalidCheckpointTables()
    {
        foreach (var mode in new[] { "unknown-list", "ambiguous-list" })
        {
            using var fixture = new Fixture(); fixture.Factory.Mode = mode;
            var result = await fixture.Run();
            Assert(result.State == "FAILED" && fixture.Factory.Descriptions == 0 && fixture.Factory.Restores == 0,
                "unknown or ambiguous checkpoint identity must fail before running configuration mutation");
        }
    }
    private static async Task MissingServiceBaseline()
    {
        using var fixture = new Fixture(); fixture.Factory.Mode = "missing-service-baseline";
        var result = await fixture.Run();
        Assert(result.State == "FAILED" && fixture.Factory.Commits == 0 && fixture.Factory.Descriptions == 0,
            "unrecognized critical service probes must block checkpoint creation and mutation");
    }
    private static async Task BaselineDrift()
    {
        using var fixture = new Fixture(); var baseline = await fixture.Capture();
        fixture.Factory.Text = Original.Replace("original", "drifted");
        var result = await fixture.Run(baseline.Id);
        Assert(result.State == "FAILED" && result.Error == "CONFIG_CHECKPOINT_BASELINE_CHANGED" && fixture.Factory.Commits == 0,
            "live baseline drift must block checkpoint creation and controlled changes");
    }
    private static async Task NoCheckpointCreated()
    {
        using var fixture = new Fixture(); fixture.Factory.Mode = "commit-no-effect";
        var result = await fixture.Run();
        Assert(result.State == "FAILED" && result.Error == "NATIVE_CHECKPOINT_NOT_CREATED" && fixture.Factory.Commits == 1 &&
            fixture.Factory.Descriptions == 0 && fixture.Factory.Restores == 0 && fixture.Factory.Text == Original,
            "a successful-looking commit response without a new device checkpoint must block all running configuration mutation");
    }
    private static async Task UnverifiedReplaceBlocked()
    {
        using var fixture = new Fixture(); fixture.Factory.Mode = "replace-unverified";
        var result = await fixture.Run();
        Assert(result.State == "FAILED" && result.Error == "CONFIG_CHECKPOINT_REPLACE_INTERACTION_UNVERIFIED" && fixture.Factory.Commits == 1 &&
            fixture.Factory.Descriptions == 0 && fixture.Factory.Restores == 0 && result.CheckpointId == "1000000001",
            "even an identified checkpoint must not permit mutation before its native replacement interaction is implemented");
    }
    private static async Task RawEchoNoCheckpointCreated()
    {
        using var fixture = new Fixture(); fixture.Factory.Mode = "raw-commit-no-effect";
        var result = await fixture.Run();
        Assert(result.State == "FAILED" && result.Error == "NATIVE_CHECKPOINT_NOT_CREATED" && fixture.Factory.Commits == 1 &&
            fixture.Factory.Descriptions == 0 && fixture.Factory.Restores == 0,
            "the actual retained echo/prompt transcript must reach the no-new-checkpoint blocker without configuration mutation");
    }
    private static async Task RawEchoCheckpointTable()
    {
        using var fixture = new Fixture(); fixture.Factory.Mode = "raw-table";
        var result = await fixture.Run();
        Assert(result.State == "SERVICE_VERIFIED" && result.CheckpointId == "1000000001" && fixture.Factory.Restores == 1,
            "a supported synthetic checkpoint table must parse with the same retained echo and verified prompt boundaries");
    }
    private static void RawEchoAndPromptForms()
    {
        const string command = SimulatorConfigurationService.CheckpointListCommand;
        foreach (var transcript in new[]
        {
            "display configuration commit list\r\nNo commit point exists. \r\n<sw1>\r\n",
            "<sw1>" + command + "\nNo commit point exists.\n<sw1>",
            "[sw1]" + command + "\nNo commit point exists.\n[sw1]",
            "<sw1>\n" + command + "\nNo commit point exists.\n<sw1>",
            "No commit point exists.\n<sw1>"
        })
            Assert(SimulatorConfigurationService.ParseCheckpointIds(transcript, "sw1").Count == 0,
                "only exact checkpoint-list echoes and verified device prompts may be removed");
    }
    private static void RejectUnexpectedTranscriptContent()
    {
        foreach (var transcript in new[]
        {
            "display other-command\nNo commit point exists.\n<sw1>",
            "No commit point exists.\n<other-device>",
            "No commit point exists.\n<Permission denied>",
            "No commit point exists.\nunknown trailing bytes\n<sw1>",
            "No commit point exists.\n<sw1>\n<sw1>",
            "display configuration commit list\ndisplay configuration commit list\nNo commit point exists.\n<sw1>",
            "Commit ID Time Stamp User\n1000000001 2026-11-07 admin\nPermission denied\n<sw1>"
        })
        {
            try { SimulatorConfigurationService.ParseCheckpointIds(transcript, "sw1"); }
            catch (ConfigRestoreException error) when (error.Code == "CONFIG_CHECKPOINT_LIST_UNRECOGNIZED") { continue; }
            throw new InvalidOperationException("Unknown transcript content must remain a hard checkpoint-list failure");
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WORKSPACE_ROOT") ?? @"D:\study\NetConsole-Next-Workspace",
            "test-data", "r70-checkpoint-unit-" + Guid.NewGuid().ToString("N"));
        public string Profile { get; } = Guid.NewGuid().ToString("N");
        public SimulatorConfigurationService Service { get; }
        public Factory Factory { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var settings = new RuntimeSettings { DataRoot = _root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            var catalog = new Catalog(settings); catalog.Initialize();
            catalog.CreateH3cDeviceProfile(new(Profile, "192.168.56.3", 22, "fixture", "FIXTURE_REFERENCE", "", true, 0, 0, TerminalProtocol: "SSH"));
            Service = new(catalog, settings, Factory);
        }
        public Task<CompleteConfigurationBackup> Capture() => Service.CaptureAsync(Profile, Actor, P2B5SiteScopeDecision.Global, default);
        public async Task<H3cCheckpointRestoreOperation> Run(string? baseline = null) =>
            await Service.VerifyNativeCheckpointRestoreAsync(new(Profile, baseline ?? (await Capture()).Id, "GigabitEthernet1/0/48", "R70-checkpoint-fixture"), Actor, P2B5SiteScopeDecision.Global, default);
        public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    private sealed class Factory : ISimulatorConfigurationSessionFactory
    {
        public string Text = Original;
        public string Mode = "normal";
        public int Opens;
        public int Commits;
        public int Descriptions;
        public int Restores;
        private string? _checkpoint;
        public Task<ISimulatorConfigurationSession> OpenAsync(H3cDeviceProfileSnapshot profile, CancellationToken token)
        { Opens++; return Task.FromResult<ISimulatorConfigurationSession>(new Session(this)); }
        private sealed class Session(Factory owner) : ISimulatorConfigurationSession
        {
            public bool SupportsNativeCheckpointRestore => owner.Mode != "replace-unverified";
            public Task<string> ReadAsync(string command, CancellationToken token)
            {
                if (command == H3cCommandPolicy.DisplayVersion) return Task.FromResult("H3C Comware Software, Version 7.1.070, Alpha 7170\nH3C S6850 uptime is 1 day\n");
                if (command == H3cCommandPolicy.DisplayCurrentConfiguration) return Task.FromResult(owner.Text);
                if (command == SimulatorConfigurationService.CheckpointListCommand)
                {
                    var output = owner.Mode == "unknown-list" ? "unrecognized output"
                        : owner.Commits == 0 || owner.Mode is "commit-no-effect" or "raw-commit-no-effect" ? "No commit point exists."
                        : "Commit ID Time Stamp User\n1000000001 2026-11-07 admin\n" + (owner.Mode == "ambiguous-list" ? "1000000002 2026-11-07 admin\n" : "");
                    return Task.FromResult(owner.Mode.StartsWith("raw-", StringComparison.Ordinal)
                        ? command + "\r\n" + output.TrimEnd() + " \r\n<fixture>\r\n" : output);
                }
                if (command == H3cCommandPolicy.DisplayInterfaceBrief)
                    return Task.FromResult(owner.Mode == "missing-service-baseline" ? "" :
                        "Brief information on interfaces in route mode:\nInterface Link Protocol IP\nVlan-interface1 " +
                        (owner.Mode == "service-drift" && owner.Restores > 0 ? "DOWN" : "UP") + " UP 192.168.56.3\n");
                if (command == H3cCommandPolicy.DisplayLldpNeighborInformationList)
                    return Task.FromResult("Local Interface Neighbor Interface Chassis ID\nGE1/0/1 neighbor port1 0011.2233.4455\n");
                throw new InvalidOperationException("Unexpected read command " + command);
            }
            public Task SetDescriptionAsync(string name, string? description, CancellationToken token)
            {
                owner.Descriptions++; owner.Text = owner.Text.Replace(" description original", " description " + description);
                if (owner.Mode == "write-throws") throw new IOException("synthetic uncertain write");
                return Task.CompletedTask;
            }
            public Task<string> CommitConfigurationCheckpointAsync(CancellationToken token)
            { owner.Commits++; owner._checkpoint = owner.Text; return Task.FromResult("Commit succeeded."); }
            public Task<string> RestoreConfigurationCheckpointAsync(string commitId, CancellationToken token)
            {
                Assert(commitId == "1000000001", "must replace the exact newly created checkpoint");
                owner.Restores++;
                if (owner.Mode == "restore-always-fails" || (owner.Mode == "restore-once-throws" && owner.Restores == 1)) throw new IOException("synthetic replacement failure");
                owner.Text = owner._checkpoint!;
                return Task.FromResult("Replace succeeded.");
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
