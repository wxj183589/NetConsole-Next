using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Reflection;

internal static class ConfigRestoreTests
{
    public static async Task RunAsync()
    {
        using var fixture = new Fixture();
        var actor = new WebUserIdentity("local-api-token", "local-api-token", "SUPERADMIN", "restore-session");
        var reader = new WebUserIdentity("restore-reader", "restore-reader", "OPERATOR", "reader-session");
        var first = await fixture.Backup(actor);
        var source = fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global,
            ConfigBackupKinds.Running).Single(item => item.Id == first.Items.Single().SnapshotId);

        ExpectCode(() => fixture.Restore.Preview(
            new(source.Id, fixture.Profile), reader, P2B5SiteScopeDecision.Global),
            "CONFIG_RESTORE_PERMISSION_DENIED");
        ExpectCode(() => fixture.Restore.Preview(
            new(source.Id, fixture.Profile), actor, P2B5SiteScopeDecision.Denied("no Site claim")),
            "CONFIG_RESTORE_SCOPE_DENIED");

        var otherProfile = Guid.NewGuid().ToString("N");
        fixture.Catalog.CreateH3cDeviceProfile(new(otherProfile, "192.0.2.62", 22, "fixture",
            "FIXTURE_REFERENCE", string.Empty, true, 0, 0));
        ExpectCode(() => fixture.Restore.Preview(
            new(source.Id, otherProfile), actor, P2B5SiteScopeDecision.Global),
            "CONFIG_RESTORE_TARGET_MISMATCH");

        var startup = await fixture.Backup(actor, ConfigBackupKinds.StartupInfo);
        var startupSnapshot = fixture.Backups.Get(startup.Items.Single().SnapshotId!, P2B5SiteScopeDecision.Global)!;
        ExpectCode(() => fixture.Restore.Preview(
            new(startupSnapshot.Id, fixture.Profile), actor, P2B5SiteScopeDecision.Global),
            "CONFIG_RESTORE_KIND_UNSUPPORTED");

        var preview = fixture.Restore.Preview(new(source.Id, fixture.Profile), actor,
            P2B5SiteScopeDecision.Global);
        Assert(preview.Compatibility == "PROFILE_EXACT_H3C" && preview.ExpiresMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() &&
            preview.AddedLines >= 0 && preview.RemovedLines >= 0 && preview.ChangedLines >= 0,
            "restore preview must return an exact-profile compatibility result and bounded diff summary");
        var dryRun = await fixture.Restore.ExecuteAsync(
            new(preview.Id, preview.ConfirmationToken, Apply: false), actor,
            P2B5SiteScopeDecision.Global, CancellationToken.None);
        Assert(dryRun.State == "DRY_RUN" && fixture.Writer.Calls == 0,
            "dry-run must complete without invoking the device writer");

        var applyPreview = fixture.Restore.Preview(new(source.Id, fixture.Profile), actor,
            P2B5SiteScopeDecision.Global);
        var applied = await fixture.Restore.ExecuteAsync(
            new(applyPreview.Id, applyPreview.ConfirmationToken, Apply: true), actor,
            P2B5SiteScopeDecision.Global, CancellationToken.None);
        Assert(applied.State == "APPLIED" && fixture.Writer.Calls == 1 &&
            fixture.Writer.Configuration is not null &&
            !fixture.Writer.Configuration.Contains("super-secret", StringComparison.Ordinal),
            "an enabled writer must receive only the sanitized snapshot configuration");

        var invalidPreview = fixture.Restore.Preview(new(source.Id, fixture.Profile), actor,
            P2B5SiteScopeDecision.Global);
        await ExpectCodeAsync(() => fixture.Restore.ExecuteAsync(
            new(invalidPreview.Id, invalidPreview.ConfirmationToken + "x", Apply: false), actor,
            P2B5SiteScopeDecision.Global, CancellationToken.None),
            "CONFIG_RESTORE_CONFIRMATION_INVALID");

        var expiredPreview = fixture.Restore.Preview(new(source.Id, fixture.Profile), actor,
            P2B5SiteScopeDecision.Global);
        ExpirePlan(fixture.Restore, expiredPreview.Id);
        await ExpectCodeAsync(() => fixture.Restore.ExecuteAsync(
            new(expiredPreview.Id, expiredPreview.ConfirmationToken, Apply: false), actor,
            P2B5SiteScopeDecision.Global, CancellationToken.None),
            "CONFIG_RESTORE_PREVIEW_EXPIRED");

        fixture.Factory.Output = "<H3C>sysname changed-after-preview\ninterface GigabitEthernet1/0/1\n description changed\n</H3C>\n";
        var changedPreview = fixture.Restore.Preview(new(source.Id, fixture.Profile), actor,
            P2B5SiteScopeDecision.Global);
        await fixture.Backup(actor);
        await ExpectCodeAsync(() => fixture.Restore.ExecuteAsync(
            new(changedPreview.Id, changedPreview.ConfirmationToken, Apply: false), actor,
            P2B5SiteScopeDecision.Global, CancellationToken.None),
            "CONFIG_RESTORE_CURRENT_CHANGED");

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "catalog.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT detail_json FROM audit_events WHERE action LIKE 'CONFIG_RESTORE%'";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var detail = rows.GetString(0);
                Assert(!detail.Contains("super-secret", StringComparison.Ordinal) &&
                    !detail.Contains(preview.ConfirmationToken, StringComparison.Ordinal),
                    "restore audit events must not contain configuration secrets or confirmation tokens");
            }
        }

        using var disabled = new Fixture(new DisabledConfigRestoreWriter());
        var disabledFirst = await disabled.Backup(actor);
        var disabledSnapshot = disabled.Backups.Get(disabledFirst.Items.Single().SnapshotId!, P2B5SiteScopeDecision.Global)!;
        var disabledPreview = disabled.Restore.Preview(new(disabledSnapshot.Id, disabled.Profile), actor,
            P2B5SiteScopeDecision.Global);
        await ExpectCodeAsync(() => disabled.Restore.ExecuteAsync(
            new(disabledPreview.Id, disabledPreview.ConfirmationToken, Apply: true), actor,
            P2B5SiteScopeDecision.Global, CancellationToken.None),
            "CONFIG_RESTORE_WRITE_ADAPTER_UNAVAILABLE");

        Console.WriteLine("CONFIG_RESTORE_TESTS=11/11 PASS");
    }

    private static void ExpirePlan(ConfigRestoreService service, string id)
    {
        var plans = (System.Collections.IDictionary)typeof(ConfigRestoreService)
            .GetField("_plans", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        var plan = plans[id]!;
        plan.GetType().GetField("<ExpiresMs>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(plan, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
    }

    private static void ExpectCode(Action action, string code)
    {
        try { action(); }
        catch (ConfigRestoreException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException($"expected ConfigRestoreException {code}");
    }

    private static async Task ExpectCodeAsync(Func<Task> action, string code)
    {
        try { await action(); }
        catch (ConfigRestoreException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException($"expected ConfigRestoreException {code}");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string Profile { get; } = Guid.NewGuid().ToString("N");
        public RuntimeSettings Settings { get; }
        public Catalog Catalog { get; }
        public Factory Factory { get; } = new();
        public ConfigBackupService Backups { get; }
        public RecordingWriter Writer { get; }
        public ConfigRestoreService Restore { get; }
        private readonly BoundedCollectionScheduler _scheduler;

        public Fixture(IConfigRestoreWriter? writer = null)
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(repository.FullName, "tools", "workspace_paths.py")))
                repository = repository.Parent ?? throw new InvalidOperationException("Repository root unavailable.");
            Root = Path.Combine(repository.Parent!.FullName, "test-data", "next-config-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Settings = new() { DataRoot = Root, WebPort = 18772, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings);
            Catalog.Initialize();
            Catalog.CreateH3cDeviceProfile(new(Profile, "192.0.2.61", 22, "fixture", "FIXTURE_REFERENCE",
                string.Empty, true, 0, 0));
            var scopes = new P2B5SiteScopeStore(Settings);
            var authorizer = new TaskSiteScopeAuthorizer(Catalog, scopes);
            _scheduler = new BoundedCollectionScheduler(Factory, 2, 8, Catalog,
                resultSink: new H3cTaskRawResultSink(Catalog), scopeAuthorizer: authorizer);
            Backups = new ConfigBackupService(Catalog, _scheduler, authorizer, Settings);
            Writer = writer as RecordingWriter ?? new RecordingWriter();
            Restore = new ConfigRestoreService(Catalog, Backups, writer ?? Writer);
        }

        public async Task<ConfigBackupBatch> Backup(WebUserIdentity actor, string? kind = null)
        {
            var batch = Backups.Start(new[] { Profile }, actor, P2B5SiteScopeDecision.Global, kind);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var current = Backups.GetBatch(batch.Id, actor, P2B5SiteScopeDecision.Global)!
                    ?? throw new InvalidOperationException("backup batch disappeared");
                if (current.State is "SUCCEEDED" or "PARTIAL") return current;
                await Task.Delay(20, timeout.Token);
            }
        }

        public void Dispose()
        {
            _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class RecordingWriter : IConfigRestoreWriter
    {
        public int Calls { get; private set; }
        public string? Configuration { get; private set; }

        public Task<ConfigRestoreWriteResult> ApplyAsync(H3cDeviceProfileSnapshot target, string configuration,
            CancellationToken cancellationToken)
        {
            Calls++;
            Configuration = configuration;
            return Task.FromResult(new ConfigRestoreWriteResult("APPLIED", "fixture writer"));
        }
    }

    private sealed class Factory : IDeviceSessionFactory
    {
        public string Output { get; set; } = "<H3C>sysname initial\nlocal-user admin password hash super-secret\ninterface GigabitEthernet1/0/1\n description initial\n</H3C>\n";
        public IDeviceSession Create(string target) => new Session(this);

        private sealed class Session(Factory owner) : IDeviceSession
        {
            public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken) =>
                Task.FromResult(owner.Output);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
