using System.Collections.Concurrent;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetConsole.Next.Service.Tests;

internal static class CompleteConfigurationBatchTests
{
    private static readonly WebUserIdentity Actor = new("local-api-token", "local-api-token", "SUPERADMIN", "fixture");
    private static readonly P2B5SiteScopeDecision Scope = P2B5SiteScopeDecision.Global;
    private const string Configuration = "#\n version 7.1.070\n#\n sysname fixture\n#\nlocal-user fixture class manage\n password hash SYNTHETIC_PRIVATE\n#\nreturn\n";

    public static async Task RunAsync()
    {
        await PrivateBatchPreservesOriginals();
        await PartialFailureAndRetry();
        await RejectMixedTargetsBeforeAdmission();
        await FrozenEndpointCannotBeReplaced();
        await ScopeRevokedBeforePublication();
        await DurableScheduleUsesPrivatePath();
        await ScheduleDoesNotOverlap();
        TargetResolutionIsExplicitAndBounded();
        DomainResolutionUsesExactMembers();
        TargetResolutionDeniesForeignSites();
        await RevokedScheduleDoesNotConnect();
        Console.WriteLine("CompleteConfigurationBatch: 11/11 PASS");
    }

    private static async Task PrivateBatchPreservesOriginals()
    {
        using var f = new Fixture();
        var batch = await f.Finish(f.Start(f.First, f.Second));
        Assert(batch.Succeeded == 2 && batch.State == "SUCCEEDED", "both devices must have complete private backups");
        foreach (var profile in new[] { f.First, f.Second })
        {
            var backup = f.Complete.List(profile, Actor, Scope).Single();
            var raw = File.ReadAllText(Path.Combine(f.Root, "backup", "config-complete", profile, backup.Id + ".cfg"));
            var view = f.Complete.View(backup.Id, Actor, Scope)!;
            Assert(raw == Configuration && !view.RedactedConfiguration.Contains("SYNTHETIC_PRIVATE") && backup.RawSha256 != backup.RedactedSha256,
                "private originals and verified redacted views must remain separate");
            Assert(batch.Items.Single(item => item.ProfileId == profile).SnapshotId == backup.Id, "batch must link private snapshot");
        }
        await f.Finish(f.Start(f.First));
        Assert(f.Complete.List(f.First, Actor, Scope).Select(item => item.Version).Order().SequenceEqual(new[] { 1, 2 }),
            "private versions must increase independently");
        Assert(!Directory.Exists(Path.Combine(f.Root, "backup", "config")) && f.SanitizedFactory.Attempts == 0,
            "private batches must never reconstruct sanitized task Raw");
    }

    private static async Task PartialFailureAndRetry()
    {
        using var f = new Fixture();
        f.Factory.FailedProfile = f.Second;
        var batch = await f.Finish(f.Start(f.First, f.Second));
        Assert(batch.State == "PARTIAL" && batch.Succeeded == 1 && batch.Failed == 1 &&
            batch.Items.Single(item => item.State == "FAILED").Error == "CONFIG_DEVICE_AUTHENTICATION_FAILED", "safe per-device partial result");
        f.Factory.FailedProfile = null;
        var retry = await f.Finish(f.Start(batch.Items.Where(item => item.State == "FAILED").Select(item => item.ProfileId).ToArray()));
        Assert(retry.Succeeded == 1 && f.Complete.List(f.First, Actor, Scope).Count == 1 &&
            f.Complete.List(f.Second, Actor, Scope).Count == 1, "retry must target only failed devices");
    }

    private static Task RejectMixedTargetsBeforeAdmission()
    {
        using var f = new Fixture();
        var invalid = f.AddProfile("192.0.2.1");
        Expect("CONFIG_CHANGE_TARGET_NOT_AUTHORIZED", () => f.Start(f.First, invalid));
        Assert(f.Factory.Opens.Count == 0, "mixed invalid selection must fail before any connection");
        return Task.CompletedTask;
    }

    private static async Task FrozenEndpointCannotBeReplaced()
    {
        using var f = new Fixture();
        f.Factory.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Start(f.First);
        await Wait(() => f.Factory.Opens.Count == 1);
        var queued = f.Start(f.First);
        f.Sql("UPDATE h3c_device_profiles SET host='192.168.56.5' WHERE id='" + f.First + "'");
        f.Factory.Hold.SetResult(true);
        var result = await f.Finish(queued);
        await f.Finish(first);
        Assert(result.Failed == 1 && result.Items[0].Error == "TASK_ENDPOINT_REVOKED_ADMISSION" && f.Factory.Opens.Count == 1,
            "queued profile must not silently migrate to an edited endpoint");
    }

    private static async Task ScopeRevokedBeforePublication()
    {
        using var f = new Fixture();
        f.Factory.OnConfiguration = () => f.Sql("UPDATE device_inventory SET enabled=0 WHERE terminal_profile_id='" + f.First + "'");
        var batch = await f.Finish(f.Start(f.First), internalMetadata: true);
        Assert(batch.Failed == 1 && batch.Items[0].Error == "TASK_TARGET_REVOKED_PUBLISH" &&
            !Directory.Exists(Path.Combine(f.Root, "backup", "config-complete")), "revoked target must not publish private files");
    }

    private static async Task DurableScheduleUsesPrivatePath()
    {
        using var f = new Fixture();
        var plan = f.Schedules.Create(new([f.First, f.Second], ConfigBackupKinds.CompleteRunning, 60), Actor, Scope);
        var store = File.ReadAllText(Path.Combine(f.Root, "backup", "config-schedules.json"));
        Assert(!store.Contains("192.168") && !store.Contains("SYNTHETIC") && !store.Contains("FIXTURE_REFERENCE"),
            "schedule persistence contains profile references only");
        f.Schedules.Dispose();
        f.Schedules = f.NewSchedules();
        f.Schedules.RunDueSchedules(plan.NextRunMs!.Value);
        await Wait(() => { f.Schedules.RunDueSchedules(plan.NextRunMs.Value); return f.Schedules.List(Actor, Scope).Single().LastState == "SUCCEEDED"; });
        Assert(f.Complete.List(f.First, Actor, Scope).Count == 1 && f.Complete.List(f.Second, Actor, Scope).Count == 1 &&
            f.SanitizedFactory.Attempts == 0, "restarted private schedule must use exact private capture path");
    }

    private static async Task ScheduleDoesNotOverlap()
    {
        using var f = new Fixture();
        f.Factory.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = f.Schedules.Create(new([f.First], ConfigBackupKinds.CompleteRunning, 60), Actor, Scope);
        var due = plan.NextRunMs!.Value;
        f.Schedules.RunDueSchedules(due);
        await Wait(() => f.Factory.Opens.Count == 1);
        for (var i = 0; i < 5; i++) f.Schedules.RunDueSchedules(due + 3_600_000);
        Assert(f.Factory.Opens.Count == 1, "slow private backup must not overlap with the next period");
        f.Factory.Hold.SetResult(true);
        await Wait(() => { f.Schedules.RunDueSchedules(due); return f.Schedules.List(Actor, Scope).Single().LastState == "SUCCEEDED"; });
    }

    private static void TargetResolutionIsExplicitAndBounded()
    {
        using var f = new Fixture();
        var resolver = new CompleteConfigurationTargets(f.Catalog, new(f.Catalog), f.Backups);
        var device = f.Catalog.GetTaskScopeTarget(f.First, Scope)!;
        var bySite = resolver.Resolve(new(SiteId: device.SiteId), Actor, Scope);
        Assert(bySite.ProfileIds.Count == 2 && bySite.Items.All(item => item.SiteId == device.SiteId), "Site uses exact enabled inventory associations");
        var explicitSelection = resolver.Resolve(new([f.Second, f.Second]), Actor, Scope);
        Assert(explicitSelection.ProfileIds.SequenceEqual(new[] { f.Second }), "explicit selection is normalized once");
        Expect("CONFIG_COMPLETE_SELECTION_INVALID", () => resolver.Resolve(new([f.First], device.SiteId), Actor, Scope));
        Expect("CONFIG_BACKUP_RESOURCE_LIMIT", () => resolver.Resolve(new(SiteId: "missing-site"), Actor, Scope));
        Assert(f.Factory.Opens.Count == 0, "preview must never connect to devices");
    }

    private static void DomainResolutionUsesExactMembers()
    {
        using var f = new Fixture();
        f.Sql("UPDATE device_inventory SET device_type='AC'");
        var wireless = new WirelessReadModelService(f.Catalog);
        var device = f.Catalog.GetTaskScopeTarget(f.First, Scope)!;
        var domain = wireless.GetSnapshot(Scope).AcDomains.Single(item => item.MemberDeviceIds.Contains(device.Id));
        var resolver = new CompleteConfigurationTargets(f.Catalog, wireless, f.Backups);
        var preview = resolver.Resolve(new(DomainId: domain.Id), Actor, Scope);
        Assert(preview.ProfileIds.SequenceEqual(new[] { f.First }) && preview.Items.Single().DeviceId == device.Id,
            "Domain selects only its exact inventory members and does not include other ACs in the Site");
        Expect("CONFIG_BACKUP_TARGET_NOT_VISIBLE", () => resolver.Resolve(new(DomainId: "missing-domain"), Actor, Scope));
        Assert(f.Factory.Opens.IsEmpty, "Domain preview performs no device interaction");
    }

    private static void TargetResolutionDeniesForeignSites()
    {
        using var f = new Fixture();
        var resolver = new CompleteConfigurationTargets(f.Catalog, new(f.Catalog), f.Backups);
        var scope = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "foreign" }, "fixture", "claims");
        var site = f.Catalog.GetTaskScopeTarget(f.First, Scope)!.SiteId;
        Expect("CONFIG_BACKUP_TARGET_NOT_VISIBLE", () => resolver.Resolve(new(SiteId: site), Actor, scope));
        Expect("CONFIG_CHANGE_TARGET_NOT_VISIBLE", () => resolver.Resolve(new([f.First]), Actor, scope));
        Expect("CONFIG_BACKUP_PERMISSION_DENIED", () => resolver.Resolve(new([f.First]), Actor with { Role = "ADMIN" }, Scope));
        Assert(f.Factory.Opens.IsEmpty, "scope rejection cannot connect");
    }

    private static async Task RevokedScheduleDoesNotConnect()
    {
        using var f = new Fixture();
        var plan = f.Schedules.Create(new([f.First], ConfigBackupKinds.CompleteRunning, 60), Actor, Scope);
        f.Sql("UPDATE h3c_device_profiles SET host='192.0.2.1' WHERE id='" + f.First + "'");
        f.Schedules.RunDueSchedules(plan.NextRunMs!.Value);
        var blocked = f.Schedules.List(Actor, Scope).Single();
        Assert(blocked.LastState == "BLOCKED" && blocked.LastError == "CONFIG_CHANGE_TARGET_NOT_AUTHORIZED" && f.Factory.Opens.IsEmpty,
            "due private schedule revalidates current whitelist before admission");
        await Task.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(@"D:\study\NetConsole-Next-Workspace\test-data", "r71-complete-batch-" + Guid.NewGuid().ToString("N"));
        public Catalog Catalog { get; }
        public RuntimeSettings Settings { get; }
        public Factory Factory { get; } = new();
        public UnexpectedFactory SanitizedFactory { get; } = new();
        public SimulatorConfigurationService Complete { get; }
        public ConfigBackupService Backups { get; }
        public ConfigBackupScheduleService Schedules { get; set; }
        public string First { get; }
        public string Second { get; }
        private readonly P2B5SiteScopeStore _scopeStore;
        private readonly TaskSiteScopeAuthorizer _authorizer;
        private readonly BoundedCollectionScheduler _scheduler;
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings); Catalog.Initialize();
            First = AddProfile("192.168.56.3"); Second = AddProfile("192.168.56.2");
            _scopeStore = new(Settings); _authorizer = new(Catalog, _scopeStore);
            _scheduler = new(SanitizedFactory, 1, 4, Catalog, scopeAuthorizer: _authorizer);
            Complete = new(Catalog, Settings, Factory);
            Backups = new(Catalog, _scheduler, _authorizer, Settings, Complete);
            Schedules = NewSchedules();
        }
        public string AddProfile(string host)
        {
            var id = Guid.NewGuid().ToString("N");
            Catalog.CreateH3cDeviceProfile(new(id, host, 22, "fixture", "FIXTURE_REFERENCE", "", true, 0, 0, TerminalProtocol: "SSH"));
            return id;
        }
        public ConfigBackupScheduleService NewSchedules() => new(Catalog, _scopeStore, _authorizer, Backups, Settings,
            NullLogger<ConfigBackupScheduleService>.Instance);
        public ConfigBackupBatch Start(params string[] profiles) => Backups.Start(profiles, Actor, Scope, ConfigBackupKinds.CompleteRunning);
        public async Task<ConfigBackupBatch> Finish(ConfigBackupBatch batch, bool internalMetadata = false)
        {
            ConfigBackupBatch? latest = null;
            await Wait(() => { latest = internalMetadata ? Backups.GetScheduledBatchStatus(batch.Id, Actor.UserId) : Backups.GetBatch(batch.Id, Actor, Scope);
                return latest?.CompletedMs is not null; });
            return latest!;
        }
        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(Root, "catalog.db")); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            Factory.Hold?.TrySetResult(true);
            _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult(); Schedules.Dispose();
            SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(Root).StartsWith(@"D:\study\NetConsole-Next-Workspace\test-data\r71-complete-batch-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped its owned path.");
            Directory.Delete(Root, true);
        }
    }

    private sealed class UnexpectedFactory : IDeviceSessionFactory
    {
        public int Attempts;
        public IDeviceSession Create(string target) { Interlocked.Increment(ref Attempts); throw new InvalidOperationException("Sanitized scheduler must not run"); }
    }
    private sealed class Factory : ISimulatorConfigurationSessionFactory
    {
        public ConcurrentQueue<string> Opens { get; } = new();
        public string? FailedProfile;
        public TaskCompletionSource<bool>? Hold;
        public Action? OnConfiguration;
        public Task<ISimulatorConfigurationSession> OpenAsync(H3cDeviceProfileSnapshot profile, CancellationToken token)
        {
            Opens.Enqueue(profile.Id);
            if (profile.Id == FailedProfile) throw new H3cAuthenticationFailedException();
            return Task.FromResult<ISimulatorConfigurationSession>(new Session(this));
        }
        private sealed class Session(Factory owner) : ISimulatorConfigurationSession
        {
            public async Task<string> ReadAsync(string command, CancellationToken token)
            {
                if (owner.Hold is { } hold) await hold.Task.WaitAsync(token);
                if (command == H3cCommandPolicy.DisplayVersion) return "H3C Comware Software, Version 7.1.070\nH3C S5560 uptime is 1 day\n";
                owner.OnConfiguration?.Invoke(); return Configuration;
            }
            public Task SetDescriptionAsync(string name, string? description, CancellationToken token) => throw new InvalidOperationException("No writes allowed");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private static async Task Wait(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
    private static void Expect(string code, Action action)
    {
        try { action(); } catch (ConfigBackupException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
