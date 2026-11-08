using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Text.Json;

internal static class ConfigurationRestoreReadinessTests
{
    private static readonly WebUserIdentity Actor = new("local-api-token", "local-api-token", "SUPERADMIN", "test");
    private const string Original = "#\n version 7.1.070, Alpha 7170\n#\n sysname fixture\n#\ninterface GigabitEthernet1/0/48\n description original\n#\nreturn\n";
    public static async Task RunAsync()
    {
        await StableReadOnly(); await HistoricalSource(); await CorruptSource(); await ModelMismatch();
        await ChangedReadback(); await ChangedBusiness(); await TransferUnavailable(); await Cancellation();
        await RestartAndOwnership(); await InterruptedHistory(); await IsolatedRows(); await AdmissionValidation();
        await MalformedHistory(); await FrozenTargetDrift(); await QueuedRevocation(); await RunningHistory();
        Console.WriteLine("CONFIG_RESTORE_READINESS_TESTS=16/16 PASS");
    }
    private static async Task StableReadOnly()
    {
        using var f = new Fixture(); var backup = await f.Capture(); f.Factory.Opens = 0;
        var item = (await f.Run(backup)).Items.Single();
        Check(item.State == "BLOCKED" && item.SourceIntegrityVerified && item.DeviceIdentityVerified && item.IndependentReadbackStable &&
            item.BusinessBaselineStable && item.SourceMatchesCurrent && f.Factory.Opens == 2 && item.FirstBackupId != item.SecondBackupId,
            "preflight must use independent complete reads and business probes");
        Check(!item.WriteAllowed && !item.Restorable && !item.WritePass && !item.ReadbackPass && !item.ConfigHashPass && !item.BusinessProbePass && !item.RollbackPass,
            "read-only preflight must never promote any actual restore acceptance flag");
        Check(item.Phases.Count == 6 && item.Phases.All(p => p.ElapsedMs >= 0) && item.Blockers.Contains("NATIVE_FILE_REPLACE_INTERACTION_UNVERIFIED") &&
            item.FirstSha256 == item.SecondSha256 && item.BusinessBaselineSha256?.Length == 64,
            "per-phase evidence and complete hashes must survive capability blocking");
        Check(!backup.Restorable && backup.SourceVerified, "a verified source must remain usable without claiming full-device restore");
    }
    private static async Task HistoricalSource()
    {
        using var f = new Fixture(); var backup = await f.Capture(); f.Factory.Text = Original.Replace("original", "newer");
        var item = (await f.Run(backup)).Items.Single();
        Check(item.State == "BLOCKED" && !item.SourceMatchesCurrent && item.IndependentReadbackStable,
            "a historical source may differ from the stable current configuration");
    }
    private static async Task CorruptSource()
    {
        using var f = new Fixture(); var backup = await f.Capture(); f.Factory.Opens = 0;
        File.AppendAllText(Path.Combine(f.Root, "backup", "config-complete", f.Profile, backup.Id + ".cfg"), "broken");
        var item = (await f.Run(backup)).Items.Single();
        Check(item.State == "FAILED" && item.Error == "CONFIG_COMPLETE_SOURCE_UNAVAILABLE" && f.Factory.Opens == 0,
            "corrupt original must fail before a device connection");
    }
    private static async Task ModelMismatch()
    {
        using var f = new Fixture(); var backup = await f.Capture(); f.Factory.Mode = "model";
        var item = (await f.Run(backup)).Items.Single();
        Check(item.State == "FAILED" && item.Error == "CONFIG_RESTORE_MODEL_OR_VERSION_MISMATCH" && !item.DeviceIdentityVerified,
            "model mismatch must block before transfer capability work");
    }
    private static async Task ChangedReadback()
    {
        using var f = new Fixture(); var backup = await f.Capture(); f.Factory.Opens = 0; f.Factory.Mode = "readback";
        var item = (await f.Run(backup)).Items.Single();
        Check(item.State == "FAILED" && !item.IndependentReadbackStable && item.Error == "CONFIG_RESTORE_PREFLIGHT_READBACK_CHANGED",
            "fresh connection drift must invalidate readiness");
    }
    private static async Task ChangedBusiness()
    {
        using var f = new Fixture(); var backup = await f.Capture(); f.Factory.Opens = 0; f.Factory.Mode = "business";
        var item = (await f.Run(backup)).Items.Single();
        Check(item.State == "FAILED" && item.IndependentReadbackStable && !item.BusinessBaselineStable && item.Error == "CONFIG_RESTORE_PREFLIGHT_BUSINESS_CHANGED",
            "matching full config must not substitute for stable link and LLDP probes");
    }
    private static async Task TransferUnavailable()
    {
        using var f = new Fixture(); var backup = await f.Capture(); f.Factory.Mode = "transfer";
        var item = (await f.Run(backup)).Items.Single();
        Check(item.State == "BLOCKED" && item.Capabilities!.NativeFileReplaceAdvertised && !item.Capabilities.SftpAvailable && item.Blockers.Contains("SFTP_SUBSYSTEM_UNAVAILABLE"),
            "advertised native replace is distinct from an available file transfer channel");
    }
    private static async Task Cancellation()
    {
        using var f = new Fixture(); var backup = await f.Capture(); f.Factory.Opens = 0;
        var item = (await f.Run(backup, new CancellationToken(true))).Items.Single();
        Check(item.State == "CANCELLED" && item.CompletedMs is not null && f.Factory.Opens == 0 && f.History().Single().State == "CANCELLED",
            "cancelled admission must persist a terminal record without connecting");
    }
    private static async Task RestartAndOwnership()
    {
        using var f = new Fixture(); var backup = await f.Capture(); var item = (await f.Run(backup)).Items.Single();
        var reloaded = new SimulatorConfigurationService(f.Catalog, f.Settings, f.Factory);
        Check(reloaded.RestorePreflightHistory(f.Profile, Actor, P2B5SiteScopeDecision.Global).Single().Id == item.Id,
            "history must survive service recreation");
        Check(reloaded.RestorePreflightHistory(f.Profile, Actor with { UserId = "other" }, P2B5SiteScopeDecision.Global).Count == 0,
            "a different actor may not read another actor's restore history");
    }
    private static async Task InterruptedHistory()
    {
        using var f = new Fixture(); var backup = await f.Capture(); var item = (await f.Run(backup)).Items.Single();
        var path = Path.Combine(f.Root, "backup", "config-complete", f.Profile, "restore-preflight", item.Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(item with { CompletedMs = null, State = "RUNNING" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Check(f.History().Single().State == "INTERRUPTED" && !f.History().Single().WritePass,
            "an interrupted persisted operation cannot appear to still run or pass");
    }
    private static async Task RunningHistory()
    {
        using var f = new Fixture(); var backup = await f.Capture();
        f.Factory.OpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Factory.ContinueOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = f.Run(backup);
        await f.Factory.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var item = f.History().Single();
            Check(item.State == "RUNNING" && item.CompletedMs is null && item.Error is null && !item.WritePass,
                "a live operation must remain running when another request reads its durable progress");
        }
        finally { f.Factory.ContinueOpen.TrySetResult(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        Check(f.History().Single().State == "BLOCKED", "terminal progress must replace a live history row");
    }
    private static async Task IsolatedRows()
    {
        using var f = new Fixture(); var backup = await f.Capture(); var second = f.AddProfile("192.168.56.2");
        var other = await f.Service.CaptureAsync(second, Actor, P2B5SiteScopeDecision.Global, default);
        var batch = await f.Service.PreflightRestoresAsync(new([new(f.Profile, "unavailable"), new(second, other.Id)]), Actor, P2B5SiteScopeDecision.Global, default);
        Check(batch.State == "PARTIAL" && batch.Items[0].State == "FAILED" && batch.Items[1].State == "BLOCKED" && batch.Items[1].IndependentReadbackStable,
            "a missing source must not skip another device's independent result");
    }
    private static async Task AdmissionValidation()
    {
        using var f = new Fixture(); var backup = await f.Capture(); f.Factory.Opens = 0;
        foreach (var request in new[] { new ConfigurationRestorePreflightRequest([]), new([new(f.Profile, backup.Id), new(f.Profile, backup.Id)]) })
        {
            try { await f.Service.PreflightRestoresAsync(request, Actor, P2B5SiteScopeDecision.Global, default); throw new InvalidOperationException("invalid request admitted"); }
            catch (ConfigRestoreException e) when (e.Code == "CONFIG_RESTORE_PREFLIGHT_INPUT_INVALID") { }
        }
        try { await f.Service.PreflightRestoresAsync(new([new(f.Profile, backup.Id), new("missing", backup.Id)]), Actor, P2B5SiteScopeDecision.Global, default); }
        catch (ConfigRestoreException) { Check(f.Factory.Opens == 0, "all targets must be authorized before any row opens"); return; }
        throw new InvalidOperationException("missing target admitted");
    }
    private static async Task MalformedHistory()
    {
        using var f = new Fixture(); var backup = await f.Capture(); var item = (await f.Run(backup)).Items.Single();
        var directory = Path.Combine(f.Root, "backup", "config-complete", f.Profile, "restore-preflight");
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{broken");
        File.WriteAllText(Path.Combine(directory, "partial.json"), JsonSerializer.Serialize(item with { Id = "partial", Phases = null! }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Check(f.History().Count == 1 && f.History().Single().Id == item.Id, "corrupt or malformed metadata must not break valid history or expose partial rows");
    }
    private static async Task FrozenTargetDrift()
    {
        using var f = new Fixture(); var backup = await f.Capture(); var second = f.AddProfile("192.168.56.2");
        var other = await f.Service.CaptureAsync(second, Actor, P2B5SiteScopeDecision.Global, default); f.Factory.Opens = 0;
        f.Factory.OnProbe = () =>
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(f.Root, "catalog.db")); db.Open();
            using var command = db.CreateCommand(); command.CommandText = "UPDATE h3c_device_profiles SET host='192.168.56.5' WHERE id=$id";
            command.Parameters.AddWithValue("$id", second); command.ExecuteNonQuery();
        };
        var batch = await f.Service.PreflightRestoresAsync(new([new(f.Profile, backup.Id), new(second, other.Id)]), Actor, P2B5SiteScopeDecision.Global, default);
        Check(batch.Items[0].State == "BLOCKED" && batch.Items[1].State == "FAILED" && f.Factory.Opens == 2,
            "a later row must not reconnect to an endpoint changed after batch admission");
    }
    private static async Task QueuedRevocation()
    {
        using var f = new Fixture(); var backup = await f.Capture(); f.Factory.Opens = 0;
        f.Factory.OpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Factory.ContinueOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Run(backup);
        await f.Factory.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = f.Run(backup);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(f.Root, "catalog.db")))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "UPDATE h3c_device_profiles SET enabled=0 WHERE id=$id";
            command.Parameters.AddWithValue("$id", f.Profile); command.ExecuteNonQuery();
        }
        f.Factory.ContinueOpen.SetResult();
        var results = await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(5));
        Check(f.Factory.Opens == 1 && results.All(batch => batch.Items.Single().State == "FAILED"),
            "revocation while queued must be checked after taking the endpoint gate and before opening another connection");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WORKSPACE_ROOT") ?? @"D:\study\NetConsole-Next-Workspace", "test-data", "r72-restore-unit-" + Guid.NewGuid().ToString("N"));
        public RuntimeSettings Settings { get; }
        public Catalog Catalog { get; }
        public Factory Factory { get; } = new();
        public SimulatorConfigurationService Service { get; }
        public string Profile { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings); Catalog.Initialize(); Profile = AddProfile("192.168.56.3"); Service = new(Catalog, Settings, Factory);
        }
        public string AddProfile(string host)
        {
            var id = Guid.NewGuid().ToString("N");
            Catalog.CreateH3cDeviceProfile(new(id, host, 22, "fixture", "FIXTURE_REFERENCE", "", true, 0, 0, TerminalProtocol: "SSH")); return id;
        }
        public Task<CompleteConfigurationBackup> Capture() => Service.CaptureAsync(Profile, Actor, P2B5SiteScopeDecision.Global, default);
        public Task<ConfigurationRestorePreflightBatch> Run(CompleteConfigurationBackup backup, CancellationToken token = default) =>
            Service.PreflightRestoresAsync(new([new(Profile, backup.Id)]), Actor, P2B5SiteScopeDecision.Global, token);
        public IReadOnlyList<ConfigurationRestorePreflight> History() => Service.RestorePreflightHistory(Profile, Actor, P2B5SiteScopeDecision.Global);
        public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Factory : ISimulatorConfigurationSessionFactory
    {
        public int Opens; public string Mode = "normal"; public string Text = Original; public Action? OnProbe;
        public TaskCompletionSource? OpenEntered, ContinueOpen;
        public async Task<ISimulatorConfigurationSession> OpenAsync(H3cDeviceProfileSnapshot profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Opens); OpenEntered?.TrySetResult();
            if (ContinueOpen is not null) await ContinueOpen.Task.WaitAsync(token);
            return new Session(this);
        }
        private sealed class Session(Factory owner) : ISimulatorConfigurationSession
        {
            public Task<string> ReadAsync(string command, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (command == H3cCommandPolicy.DisplayVersion) return Task.FromResult("H3C Comware Software, Version 7.1.070, Alpha 7170\nH3C " + (owner.Mode == "model" ? "WX5540H" : "S6850") + " uptime is 1 day\n");
                if (command == H3cCommandPolicy.DisplayCurrentConfiguration) return Task.FromResult(owner.Mode == "readback" && owner.Opens == 2 ? owner.Text.Replace("original", "drifted") : owner.Text);
                if (command == SimulatorConfigurationService.CheckpointListCommand) return Task.FromResult("No commit point exists.");
                if (command == H3cCommandPolicy.DisplayInterfaceBrief) return Task.FromResult("Brief information on interfaces in route mode:\nInterface Link Protocol IP\nVlan-interface1 " + (owner.Mode == "business" && owner.Opens == 2 ? "DOWN" : "UP") + " UP 192.168.56.3\n");
                if (command == H3cCommandPolicy.DisplayLldpNeighborInformationList) return Task.FromResult("Total number of neighbors: 0");
                throw new InvalidOperationException("Unexpected read command");
            }
            public Task<ConfigurationRestoreCapabilities> ProbeRestoreCapabilitiesAsync(CancellationToken token)
            {
                owner.OnProbe?.Invoke();
                return Task.FromResult(new ConfigurationRestoreCapabilities(true, owner.Mode != "transfer", 123, owner.Mode == "transfer" ? "SFTP_SUBSYSTEM_UNAVAILABLE" : "SFTP_CONNECTED_WRITE_UNVERIFIED"));
            }
            public Task SetDescriptionAsync(string name, string? description, CancellationToken token) => throw new InvalidOperationException("preflight attempted a write");
            public Task<string> CommitConfigurationCheckpointAsync(CancellationToken token) => throw new InvalidOperationException("preflight attempted commit");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
