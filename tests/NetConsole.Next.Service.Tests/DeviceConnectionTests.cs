using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Collections.Concurrent;
using System.Net;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

internal static class DeviceConnectionTests
{
    public static async Task RunAsync()
    {
        RejectInvalidRequests();
        VisibilityBeforeCompatibility();
        RejectUnsupportedWithoutWrites();
        RejectMissingDisabledOrInconsistentProfile();
        RejectReboundOrIncompleteExpectedInventory();
        await ExactInventoryIdentityAndCredentialBoundary();
        await ExistingPlanCancellation();
        await ExistingPlanCurrentAuthority();
        await WirelessHistoryCurrentAuthority();
        await WirelessExportRunIsolation();
        await ConcurrentRetriesExecuteOnlyOnce();
        await SuccessfulRetryCannotBeRepeated();
        await RetryAssociationFailureNeverExecutes();
        Console.WriteLine("DEVICE_CONNECTION_TESTS=13/13 PASS");
    }

    private static void RejectInvalidRequests()
    {
        using var f = new Fixture();
        foreach (var ids in new IReadOnlyList<string>?[] { null, [], [" "], [f.Device, f.Device],
                     [f.Device, " " + f.Device], Enumerable.Range(0, 65).Select(_ => Guid.NewGuid().ToString("N")).ToArray() })
            Expect("DEVICE_CONNECTION_TEST_INVALID_TARGETS", 400, () => f.Start(ids));
        Expect("DEVICE_CONNECTION_TEST_PERMISSION_DENIED", 403,
            () => f.Tests.Start(new([f.Device]), f.Actor with { Role = "ADMIN" }, P2B5SiteScopeDecision.Global));
        f.NoTasks();
    }

    private static void VisibilityBeforeCompatibility()
    {
        using var f = new Fixture();
        f.Execute("UPDATE device_inventory SET vendor='Huawei' WHERE id=$id", f.Device);
        var hidden = f.AddProfile("192.0.2.92");
        f.Execute("INSERT INTO sites(id,name) VALUES('hidden','Hidden')");
        f.Execute("UPDATE device_inventory SET site_id='hidden' WHERE id=$id", hidden.Device);
        var local = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "local" }, "fixture", "claims-1");
        foreach (var targets in new[] { new[] { f.Device, hidden.Device }, new[] { hidden.Device, f.Device },
                     new[] { f.Device, "missing-device" } })
            Expect("DEVICE_CONNECTION_TEST_TARGET_NOT_VISIBLE", 404,
                () => f.Tests.Start(new(targets), f.Actor, local));
        f.NoTasks();
    }

    private static void RejectUnsupportedWithoutWrites()
    {
        using var f = new Fixture();
        var second = f.AddProfile("192.0.2.93");
        foreach (var vendor in new[] { "Huawei", "Cisco", "ZTE", "", "unknown" })
        {
            f.Execute("UPDATE device_inventory SET vendor=$value WHERE id=$id", second.Device, vendor);
            Expect("DEVICE_CONNECTION_TEST_TARGET_UNSUPPORTED", 409, () => f.Start([f.Device, second.Device]));
        }
        foreach (var type in new[] { "FIT_AP", "fit_ap", "FiT_aP" })
        {
            f.Execute("UPDATE device_inventory SET vendor='H3C',device_type=$value WHERE id=$id", second.Device, type);
            Expect("DEVICE_CONNECTION_TEST_TARGET_UNSUPPORTED", 409, () => f.Start([f.Device, second.Device]));
        }
        f.NoTasks();
    }

    private static void RejectMissingDisabledOrInconsistentProfile()
    {
        using var f = new Fixture();
        var second = f.AddProfile("192.0.2.94");
        f.Execute("UPDATE device_inventory SET terminal_profile_id=NULL WHERE id=$id", second.Device);
        Expect("DEVICE_CONNECTION_TEST_TARGET_UNSUPPORTED", 409, () => f.Start([f.Device, second.Device]));
        f.Execute("UPDATE device_inventory SET terminal_profile_id=$value WHERE id=$id", second.Device, second.Profile);
        f.Execute("UPDATE h3c_device_profiles SET enabled=0 WHERE id=$id", second.Profile);
        Expect("DEVICE_CONNECTION_TEST_TARGET_UNSUPPORTED", 409, () => f.Start([f.Device, second.Device]));
        f.Execute("UPDATE h3c_device_profiles SET enabled=1 WHERE id=$id", second.Profile);
        f.Execute("UPDATE device_inventory SET enabled=0 WHERE id=$id", second.Device);
        Expect("DEVICE_CONNECTION_TEST_TARGET_UNSUPPORTED", 409, () => f.Start([f.Device, second.Device]));
        f.Execute("INSERT INTO sites(id,name) VALUES('different-site','Different')");
        f.Execute("UPDATE device_inventory SET enabled=1,site_id='different-site' WHERE id=$id", second.Device);
        Expect("DEVICE_CONNECTION_TEST_TARGET_UNSUPPORTED", 409, () => f.Start([f.Device, second.Device]));
        f.NoTasks();
    }

    private static void RejectReboundOrIncompleteExpectedInventory()
    {
        using var f = new Fixture();
        var second = f.AddProfile("192.0.2.96");
        var replacement = f.AddProfile("192.0.2.97");
        var request = new H3cTaskPlanCreateRequest(null, [f.Profile, second.Profile], "display version", 30_000);
        void Reject(IReadOnlyDictionary<string, string> expected)
        {
            try { _ = f.Plans.CreateAndSchedule(request, f.Actor.UserId, expected); throw new Exception("target rebind rejection expected"); }
            catch (InvalidOperationException ex)
            { Assert(ex.Message == "设备连接档案关联已变化，请刷新设备列表后重试。", "inventory changes must produce the stable conflict explanation"); }
            f.NoTasks();
        }

        Reject(new Dictionary<string, string> { [f.Profile] = f.Device });
        Reject(new Dictionary<string, string> { [f.Profile] = f.Device, ["unselected-profile"] = second.Device });
        var selected = new Dictionary<string, string> { [f.Profile] = f.Device, [second.Profile] = second.Device };
        // Rebind the second profile after the inventory selection was captured.
        // All CaptureDefinition calls succeed against the new association, so
        // only the explicit expected inventory guard can reject this batch.
        f.Execute("UPDATE device_inventory SET terminal_profile_id=NULL WHERE id=$id", second.Device);
        f.Execute("UPDATE device_inventory SET terminal_profile_id=$value WHERE id=$id", replacement.Device, second.Profile);
        Reject(selected);
    }

    private static async Task ExactInventoryIdentityAndCredentialBoundary()
    {
        using var f = new Fixture();
        var inventory = "dev-" + Guid.NewGuid().ToString("N");
        f.Execute("UPDATE device_inventory SET id=$value WHERE id=$id", f.Device, inventory);
        foreach (var guess in new[] { f.Profile, "192.0.2.91", "H3C 192.0.2.91:22" })
            Expect("DEVICE_CONNECTION_TEST_TARGET_NOT_VISIBLE", 404, () => f.Start([guess]));
        f.NoTasks();
        var second = f.AddProfile("192.0.2.95");
        var plan = f.Start([inventory, second.Device]);
        var terminal = await f.Terminal(plan.Id);
        Assert(terminal.State == "SUCCEEDED" && terminal.Items.Count == 2, "both exact inventory subjects must complete");
        Assert(terminal.Items.Select(item => item.Target).SequenceEqual(new[] { f.Profile, second.Profile }),
            "inventory association must resolve to the exact stored profiles");
        Assert(terminal.CommandPlan.SequenceEqual(new[] { "display version" }) && f.Factory.Commands.Count == 2 &&
            f.Factory.Commands.All(command => command == "display version"), "the plan must execute only the fixed read-only version command");
        foreach (var item in terminal.Items)
        {
            var definition = f.Catalog.GetTaskDefinition(item.DefinitionId)!;
            var request = JsonSerializer.Serialize(definition);
            Assert(definition.TimeoutMs == 30_000 && !request.Contains("synthetic-secret") &&
                !request.Contains("FIXTURE_REFERENCE"), "task arguments must contain no credentials or credential references");
        }
        Assert(f.Plans.GetReport(plan.Id, f.Actor.UserId).Items.All(item => item.Run?.State == "SUCCEEDED" && item.Commands.Count == 1),
            "existing task-plan reports must include every command outcome");
    }

    private static async Task ExistingPlanCancellation()
    {
        using var f = new Fixture();
        f.Factory.Hold = true;
        var plan = f.Start([f.Device]);
        await f.Factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = f.Plans.Cancel(plan.Id, f.Actor.UserId);
        var terminal = await f.Terminal(plan.Id);
        Assert(terminal.State == "CANCELLED" && terminal.Items.Single().State == "CANCELLED", "existing plan cancellation must stop the admitted run");
    }

    private static async Task ExistingPlanCurrentAuthority()
    {
        using var f = new Fixture();
        var plan = f.Start([f.Device]);
        await f.Terminal(plan.Id);
        f.Execute("UPDATE web_users SET role='ADMIN' WHERE id=$id", f.Actor.UserId);
        foreach (var action in new Action[] { () => f.Plans.GetVisible(plan.Id, f.Actor.UserId),
                     () => f.Plans.GetReport(plan.Id, f.Actor.UserId), () => f.Plans.Cancel(plan.Id, f.Actor.UserId) })
        {
            try { action(); throw new Exception("current authority denial expected"); }
            catch (TaskScopeRevokedException) { }
        }
        f.Execute("UPDATE web_users SET role='SUPERADMIN' WHERE id=$id", f.Actor.UserId);
        f.Execute("UPDATE device_inventory SET enabled=0 WHERE id=$id", f.Device);
        try { _ = f.Plans.GetVisible(plan.Id, f.Actor.UserId); throw new Exception("current target denial expected"); }
        catch (TaskScopeRevokedException) { }
    }

    private static async Task WirelessHistoryCurrentAuthority()
    {
        using var f = new Fixture();
        var id = "wireless-plan-" + Guid.NewGuid().ToString("N");
        f.Plans.CreateAndSchedule(new(id, [f.Profile], "display version", 30_000), f.Actor.UserId);
        await f.Terminal(id);
        var wireless = new WirelessTaskService(new WirelessReadModelService(f.Catalog), f.Catalog, f.Plans, f.Scopes);
        Assert(wireless.List(f.Actor.UserId).Single().Id == id, "authorized wireless history is visible");
        Assert(wireless.GetReport(id, f.Actor.UserId).Items.Single().DeviceId == f.Device, "report uses stable inventory identity");
        Assert(wireless.Export(id, f.Actor.UserId).Length > 0, "authorized export is available");
        f.Execute("UPDATE device_inventory SET enabled=0 WHERE id=$id", f.Device);
        Assert(wireless.List(f.Actor.UserId).Count == 0, "revoked plan must be absent from history");
        foreach (var action in new Action[] { () => wireless.GetReport(id, f.Actor.UserId), () => wireless.Export(id, f.Actor.UserId) })
        {
            try { action(); throw new Exception("wireless current authority denial expected"); }
            catch (TaskScopeRevokedException) { }
        }
    }

    private static async Task WirelessExportRunIsolation()
    {
        using var f = new Fixture();
        var wireless = new WirelessTaskService(new WirelessReadModelService(f.Catalog), f.Catalog, f.Plans, f.Scopes);
        var first = "wireless-plan-" + Guid.NewGuid().ToString("N");
        var second = "wireless-plan-" + Guid.NewGuid().ToString("N");
        f.Factory.Hold = true;
        var active = f.Plans.CreateAndSchedule(new(first, [f.Profile], "display version", 30_000), f.Actor.UserId);
        await f.Factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(wireless.GetReport(first, f.Actor.UserId).ObservationStatus == "NOT_PUBLISHED", "active task has no published observations");
        using (var zip = new ZipArchive(new MemoryStream(wireless.Export(first, f.Actor.UserId))))
            Assert(zip.GetEntry("raw-results/manifest.json") is not null && !zip.Entries.Any(e => e.FullName.EndsWith(".txt")), "active task exports explicit unavailable raw");
        _ = f.Plans.Cancel(first, f.Actor.UserId);
        await f.Terminal(first);
        f.Factory.Hold = false;
        f.Plans.CreateAndSchedule(new(second, [f.Profile], "display version", 30_000), f.Actor.UserId);
        var terminal = await f.Terminal(second);
        var runId = terminal.Items.Single().RunId!;
        using var archive = new ZipArchive(new MemoryStream(wireless.Export(second, f.Actor.UserId)));
        var raw = archive.GetEntry($"raw-results/{runId}/command-001.txt");
        Assert(raw is not null && archive.Entries.All(e => active.Items.Single().RunId is not { } previous || !e.FullName.Contains(previous)), "export contains only its own run");
        using var reader = new StreamReader(raw!.Open(), Encoding.UTF8);
        var text = reader.ReadToEnd();
        Assert(text.Contains("<REDACTED>") && !text.Contains("fixture-password"), "raw credentials are redacted");
    }

    private static async Task ConcurrentRetriesExecuteOnlyOnce()
    {
        using var f = new Fixture();
        f.Factory.Fail = true;
        var original = f.Start([f.Device]);
        Assert((await f.Terminal(original.Id)).State == "FAILED", "fixture original run fails");
        f.Factory.Fail = false;
        f.Factory.Hold = true;
        using var ready = new CountdownEvent(2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Request()
        {
            ready.Signal();
            await start.Task;
            try { f.Plans.Retry(original.Id, new(0), f.Actor.UserId); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var a = Task.Run(Request);
        var b = Task.Run(Request);
        Assert(ready.Wait(TimeSpan.FromSeconds(5)), "both concurrent retry requests are ready");
        start.SetResult();
        var admitted = await Task.WhenAll(a, b);
        var retry = f.Catalog.GetTaskPlanRetries(original.Id).Single();
        Assert(admitted.Count(value => value) == 1, "exactly one concurrent retry is admitted");
        f.Factory.Release.TrySetResult();
        await f.RunTerminal(retry.RetryRunId);
        Assert(f.Factory.Commands.Count == 2 && f.Catalog.GetTaskPlanRetries(original.Id).Count == 1,
            "concurrent duplicate retry cannot execute or persist a second attempt");
    }

    private static async Task SuccessfulRetryCannotBeRepeated()
    {
        using var f = new Fixture();
        f.Factory.Fail = true;
        var original = f.Start([f.Device]);
        await f.Terminal(original.Id);
        f.Factory.Fail = false;
        f.Plans.Retry(original.Id, new(0), f.Actor.UserId);
        var retry = f.Catalog.GetTaskPlanRetries(original.Id).Single();
        Assert((await f.RunTerminal(retry.RetryRunId)).State == "SUCCEEDED", "retry succeeds");
        foreach (var indexes in new IReadOnlyList<int>?[] { null, [0] })
        {
            var rejected = false;
            try { f.Plans.Retry(original.Id, new(0, indexes), f.Actor.UserId); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "both default and explicit requests reject a successful retry command");
        }
        Assert(f.Factory.Commands.Count == 2 && f.Catalog.GetTaskPlanRetries(original.Id).Count == 1,
            "successful retry preserves original history without executing it again");
    }

    private static async Task RetryAssociationFailureNeverExecutes()
    {
        using var f = new Fixture();
        f.Factory.Fail = true;
        var original = f.Start([f.Device]);
        var terminal = await f.Terminal(original.Id);
        f.Factory.Fail = false;
        f.Execute("CREATE TRIGGER reject_retry BEFORE INSERT ON task_plan_retries BEGIN SELECT RAISE(ABORT,'synthetic association failure'); END");
        var rejected = false;
        try { f.Plans.Retry(original.Id, new(0), f.Actor.UserId); }
        catch (SqliteException) { rejected = true; }
        var rejectedRun = f.Catalog.RecentTaskRuns().Single(run => run.Id != terminal.Items.Single().RunId);
        Assert(rejected && rejectedRun.State == "CANCELLED" && f.Factory.Commands.Count == 1 &&
            f.Catalog.GetTaskPlanRetries(original.Id).Count == 0 &&
            f.Catalog.GetTaskRunCommands(rejectedRun.Id).All(command => command.State == "CANCELLED" && command.RawOutputPath is null) &&
            f.Catalog.QueryH3cFacts(f.Profile, history: true).Count == 0,
            "failed association must cancel before enqueue, execute zero commands and publish zero facts");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public Catalog Catalog { get; }
        public WebUserIdentity Actor { get; }
        public Factory Factory { get; } = new();
        public H3cBatchTaskService Plans { get; }
        public P2B5SiteScopeStore Scopes { get; }
        public DeviceConnectionTestService Tests { get; }
        public string Profile { get; }
        public string Device { get; }
        private readonly BoundedCollectionScheduler _scheduler;

        public Fixture()
        {
            DirectoryInfo? FindRepository(string start)
            {
                for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                    if (File.Exists(Path.Combine(directory.FullName, "tools", "workspace_paths.py"))) return directory;
                return null;
            }
            var repository = FindRepository(Directory.GetCurrentDirectory()) ?? FindRepository(AppContext.BaseDirectory)
                ?? throw new InvalidOperationException("Run tests from the repository or its child directory.");
            Root = Path.Combine(repository.Parent!.FullName, "test-data", "device-connection-tests-" + Guid.NewGuid().ToString("N"));
            var settings = new RuntimeSettings { DataRoot = Root, WebPort = 18773, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 1024 * 1024 };
            Catalog = new(settings);
            Catalog.Initialize();
            Actor = new WebAuthStore(settings).TryLogin("superadmin", "superadmin", "127.0.0.1", out _)!.User;
            var scopes = Scopes = new P2B5SiteScopeStore(settings);
            var authorizer = new TaskSiteScopeAuthorizer(Catalog, scopes);
            _scheduler = new(Factory, 2, 8, Catalog, resultSink: new H3cTaskRawResultSink(Catalog), scopeAuthorizer: authorizer);
            Plans = new(Catalog, authorizer, scopes, _scheduler);
            Tests = new(Catalog, Plans);
            (Profile, Device) = AddProfile("192.0.2.91");
        }

        public (string Profile, string Device) AddProfile(string host)
        {
            var profile = Guid.NewGuid().ToString("N");
            Catalog.CreateH3cDeviceProfile(new(profile, host, 22, "fixture", "FIXTURE_REFERENCE", string.Empty,
                true, 0, 0, Password: "synthetic-secret"));
            return (profile, Catalog.GetTaskScopeTarget(profile, P2B5SiteScopeDecision.Global)!.Id);
        }

        public TaskPlanSnapshot Start(IReadOnlyList<string>? ids) => Tests.Start(new(ids), Actor, P2B5SiteScopeDecision.Global);
        public async Task<TaskPlanSnapshot> Terminal(string id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var plan = Plans.GetVisible(id, Actor.UserId);
                if (plan.State is not ("QUEUED" or "RUNNING")) return plan;
                await Task.Delay(10, timeout.Token);
            }
        }
        public async Task<TaskRunSnapshot> RunTerminal(string id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var run = Catalog.GetTaskRun(id)!;
                if (run.State is not ("QUEUED" or "RUNNING" or "CONNECTING")) return run;
                await Task.Delay(10, timeout.Token);
            }
        }
        public void Execute(string sql, string id = "", string value = "")
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
        public void NoTasks()
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT (SELECT COUNT(*) FROM task_definitions)+(SELECT COUNT(*) FROM task_plans)+(SELECT COUNT(*) FROM task_runs)";
            Assert(Convert.ToInt64(command.ExecuteScalar()) == 0 && Factory.Commands.IsEmpty, "rejected batches must create no definitions, plans, runs or commands");
        }
        private SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "catalog.db"), Pooling = false }.ToString());
            db.Open(); return db;
        }
        public void Dispose()
        {
            _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }

    private sealed class Factory : IDeviceSessionFactory
    {
        public bool Hold;
        public bool Fail;
        public ConcurrentQueue<string> Commands { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDeviceSession Create(string target) => new Session(this);
        private sealed class Session(Factory owner) : IDeviceSession
        {
            public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
            public async Task<string> ExecuteAsync(string command, CancellationToken token)
            {
                owner.Commands.Enqueue(command);
                owner.Entered.TrySetResult();
                if (owner.Fail) throw new IOException("synthetic collection failure");
                if (owner.Hold) await owner.Release.Task.WaitAsync(token);
                return "H3C Comware Software, Version 7.1.070, Release 6635\npassword fixture-password\n";
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private static void Expect(string code, int status, Action action)
    {
        try { action(); throw new Exception("request rejection expected"); }
        catch (DeviceConnectionTestException ex) { Assert(ex.Code == code && ex.StatusCode == status, "unexpected batch rejection: " + ex.Code); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
