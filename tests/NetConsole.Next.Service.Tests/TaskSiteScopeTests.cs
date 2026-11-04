using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Text.Json;

internal static class TaskSiteScopeTests
{
    public static async Task RunAsync()
    {
        FreezesPersistentTargetWithoutStoringSecrets();
        RechecksClaimsAndActorWithoutExpandingOldTargets();
        LegacyRunsDoNotAcquireRetroactiveScope();
        await RevokedQueuedTargetNeverConnects();
        await RevokedDuringConnectNeverExecutesCommand();
        await RevokedBoundedCommandSealsPartialRaw(false);
        await RevokedBoundedCommandSealsPartialRaw(true);
        await AuthorizedScopedTaskKeepsExistingCollectionBehavior();
        await QueuedCancelAndRevocationKeepBothReasons();
        await RevokedDuringDisposeSealsOrClassifiesResult(false);
        await RevokedDuringDisposeSealsOrClassifiesResult(true);
        Console.WriteLine("TASK_SITE_SCOPE_TESTS=11/11 PASS");
    }

    private static void FreezesPersistentTargetWithoutStoringSecrets()
    {
        using var f = new Fixture();
        var snapshot = f.Authorizer.CaptureDefinition(f.ProfileId, f.SuperId);
        f.Catalog.CreateTaskDefinition("frozen", "H3C_SSH_READONLY", f.ProfileId, "display version", TimeSpan.FromSeconds(2), scopeSnapshot: snapshot);
        var admitted = f.Catalog.GetTaskDefinition("frozen")!;
        var run = f.Catalog.CreateTaskRun(admitted, f.SuperId);
        f.Catalog.CreateTaskDefinition("frozen", "H3C_SSH_READONLY", f.ProfileId, "display version", TimeSpan.FromSeconds(8),
            scopeSnapshot: snapshot with { CreatedMs = snapshot.CreatedMs + 1, ClaimsRevision = "changed" });
        Assert(f.Catalog.GetTaskDefinition("frozen")!.ScopeSnapshot == snapshot, "referenced scope must remain immutable even when metadata/timeout is edited");
        var other = f.CreateProfile("192.0.2.22");
        var expanded = f.Authorizer.CaptureDefinition(other, f.SuperId);
        ExpectInvalid(() => f.Catalog.CreateTaskDefinition("frozen", "H3C_SSH_READONLY", other, "display version", TimeSpan.FromSeconds(2), scopeSnapshot: expanded));
        ExpectInvalid(() => f.Catalog.CreateTaskRun(admitted)); // Changed timeout cannot race queue admission.
        f.Catalog.TransitionTaskRun(run, "CONNECTING", admittedTimeoutMs: admitted.TimeoutMs);
        var stored = f.Catalog.RecentTaskRuns().Single();
        Assert(stored.DeadlineMs - stored.StartedMs == 2000, "persisted deadline must use the admitted timeout, not mutable definition metadata");
        var request = f.Scalar("SELECT request_json FROM task_definitions WHERE id='frozen'");
        Assert(!request.Contains("password", StringComparison.OrdinalIgnoreCase) && !request.Contains("CREDENTIAL", StringComparison.Ordinal) &&
            !request.Contains("token", StringComparison.OrdinalIgnoreCase) && request.Contains(snapshot.DeviceId), "scope JSON stores stable subject IDs without credentials/session tokens");
    }

    private static void RechecksClaimsAndActorWithoutExpandingOldTargets()
    {
        using var f = new Fixture();
        f.Grant("local");
        var definition = f.Definition("claims", f.AdminId);
        // Exercise the Site predicate independently. Public collection admission
        // keeps its pre-existing SUPERADMIN role requirement.
        var execution = new TaskExecutionScope(definition.ScopeSnapshot!, f.AdminId, definition.Id, "ADMIN");
        ExpectScopeDenied(() => f.Authorizer.CaptureExecution(definition, f.AdminId));
        var originalRevision = f.Scopes.Resolve(f.Catalog.GetActiveTaskActor(f.AdminId)!).ClaimsRevision;
        f.Grant("local", 2);
        Assert(f.Scopes.Resolve(f.Catalog.GetActiveTaskActor(f.AdminId)!).ClaimsRevision != originalRevision,
            "authority revision must change even if its effective Site set stays the same");
        f.Authorizer.Require(execution, f.ProfileId, "REVISION_ONLY_CHANGED");
        Assert(f.Scalar("SELECT COUNT(*) FROM audit_events WHERE action='TASK_SCOPE_REVISION_CHECK'") != "0", "a revision-only change must remain auditable without enlarging targets");
        f.Grant("other-site");
        f.Authorizer.Require(execution, f.ProfileId, "REVISION_CHANGED");
        var other = f.CreateProfile("192.0.2.23");
        ExpectScopeDenied(() => f.Authorizer.Require(execution, other, "NEW_TARGET"));
        f.Execute("UPDATE web_user_site_memberships SET claim_state='REVOKED' WHERE user_id=$id AND site_reference='local'", f.AdminId);
        ExpectScopeDenied(() => f.Authorizer.Require(execution, f.ProfileId, "CLAIMS_REVOKED"));
        ExpectScopeDenied(() => f.Authorizer.CaptureDefinition(f.ProfileId, f.AdminId, "ADMIN"));
        var global = f.Authorizer.CaptureExecution(f.Definition("global", f.SuperId), f.SuperId);
        f.Execute("UPDATE web_users SET disabled=1 WHERE id=$id", f.SuperId);
        ExpectScopeDenied(() => f.Authorizer.Require(global, f.ProfileId, "ACTOR_DISABLED"));
    }

    private static void LegacyRunsDoNotAcquireRetroactiveScope()
    {
        using var f = new Fixture();
        f.Catalog.CreateTaskDefinition("legacy", "H3C_SSH_READONLY", f.ProfileId, "display version", TimeSpan.FromSeconds(2));
        var legacy = f.Catalog.GetTaskDefinition("legacy")!;
        f.Catalog.CreateTaskRun(legacy);
        ExpectScopeDenied(() => f.Authorizer.CaptureExecution(legacy, f.SuperId));
        var snapshot = f.Authorizer.CaptureDefinition(f.ProfileId, f.SuperId);
        ExpectInvalid(() => f.Catalog.CreateTaskDefinition("legacy", "H3C_SSH_READONLY", f.ProfileId, "display version", TimeSpan.FromSeconds(2), scopeSnapshot: snapshot));
        f.Grant("local");
        var scope = f.Scopes.Resolve(f.Catalog.GetActiveTaskActor(f.AdminId)!);
        Assert(f.Catalog.RecentTaskRuns(scope: scope).Count == 0 && f.Catalog.RecentTaskRuns(scope: P2B5SiteScopeDecision.Global).Count == 1,
            "legacy history remains visible to global operator without inventing a Site creation proof");
    }

    private static async Task RevokedQueuedTargetNeverConnects()
    {
        using var f = new Fixture();
        f.Definition("queued", f.SuperId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SessionFactory { OnExecute = async (target, token) =>
        {
            if (target == "blocker") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return "fixture";
        } };
        await using var scheduler = f.Scheduler(factory);
        Assert(scheduler.TrySchedule(new("blocker", "fixture", TimeSpan.FromSeconds(5)), out var blocker), "blocker admitted");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(scheduler.TryScheduleDefinition("queued", out var run, f.SuperId), "scoped target queued");
        f.Execute("UPDATE web_users SET role='ADMIN' WHERE id=$id", f.SuperId);
        release.TrySetResult();
        await Terminal(scheduler, blocker);
        var result = await Terminal(scheduler, run);
        Assert(result.State == "CANCELLED" && result.Error!.Contains("BEFORE_CONNECT") && factory.TargetConnects == 0,
            "role/claims revocation while queued must stop before opening a device session");
    }

    private static async Task RevokedDuringConnectNeverExecutesCommand()
    {
        using var f = new Fixture();
        f.Definition("connect", f.SuperId);
        var factory = new SessionFactory { OnConnect = (_, _) => { f.Execute("UPDATE web_users SET disabled=1 WHERE id=$id", f.SuperId); return Task.CompletedTask; } };
        await using var scheduler = f.Scheduler(factory);
        Assert(scheduler.TryScheduleDefinition("connect", out var run, f.SuperId), "scoped connection admitted");
        var result = await Terminal(scheduler, run);
        Assert(result.State == "CANCELLED" && result.Error!.Contains("REVOKED_AFTER_START") && result.Error.Contains("BEFORE_COMMAND") &&
            factory.TargetConnects == 1 && factory.TargetCommands == 0 && factory.Disposals == 1,
            "accepted bounded connect may finish, but revoked command must never start and transport must be disposed");
    }

    private static async Task RevokedBoundedCommandSealsPartialRaw(bool cancelAlso)
    {
        using var f = new Fixture();
        f.Definition("command", f.SuperId);
        BoundedCollectionScheduler? scheduler = null;
        string run = "";
        var factory = new SessionFactory { OnExecute = (_, _) =>
        {
            f.Execute("UPDATE web_users SET disabled=1 WHERE id=$id", f.SuperId);
            if (cancelAlso) Assert(scheduler!.Cancel(run), "simultaneous cancel accepted");
            return Task.FromResult("bounded synthetic response");
        } };
        await using (scheduler = f.Scheduler(factory))
        {
            Assert(scheduler.TryScheduleDefinition("command", out run, f.SuperId), "bounded command admitted");
            var result = await Terminal(scheduler, run);
            var persisted = f.Catalog.RecentTaskRuns().Single();
            Assert(result.State == "CANCELLED" && result.Error!.Contains("REVOKED_AFTER_START") &&
                persisted.CaptureStatus == "PARTIAL" && persisted.ParseStatus == "PARTIAL" && persisted.RawOutputSha256?.Length == 64 && factory.TargetCommands == 1,
                "atomic command output must be durably sealed as partial, never success/offline or a future retry");
            if (cancelAlso) Assert(result.Error!.Contains("CANCEL_REQUESTED"), "cancel and revoke reasons must both survive Raw commit");
            Assert(f.Scalar("SELECT COUNT(*) FROM audit_events WHERE action='TASK_SCOPE_REVOKED'") != "0", "revocation must leave a safe audit reference");
        }
    }

    private static async Task AuthorizedScopedTaskKeepsExistingCollectionBehavior()
    {
        using var f = new Fixture();
        f.Definition("success", f.SuperId);
        var factory = new SessionFactory();
        await using var scheduler = f.Scheduler(factory);
        Assert(scheduler.TryScheduleDefinition("success", out var run, f.SuperId), "authorized scoped task admitted");
        var result = await Terminal(scheduler, run);
        Assert(result.State == "SUCCEEDED" && f.Catalog.RecentTaskRuns().Single().CaptureStatus == "COMPLETE" && factory.TargetCommands == 1,
            "scope enforcement must preserve ordinary authorized capture and durable Raw output");
    }

    private static async Task QueuedCancelAndRevocationKeepBothReasons()
    {
        using var f = new Fixture();
        f.Definition("queued-cancel", f.SuperId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SessionFactory { OnExecute = async (target, token) =>
        { if (target == "blocker") { entered.TrySetResult(); await release.Task.WaitAsync(token); } return "fixture"; } };
        await using var scheduler = f.Scheduler(factory);
        Assert(scheduler.TrySchedule(new("blocker", "fixture", TimeSpan.FromSeconds(5)), out var blocker), "queue blocker admitted");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(scheduler.TryScheduleDefinition("queued-cancel", out var run, f.SuperId), "scoped target queued");
        f.Execute("UPDATE web_users SET disabled=1 WHERE id=$id", f.SuperId);
        Assert(scheduler.Cancel(run), "operator cancellation remains possible after authorization is revoked");
        release.TrySetResult();
        await Terminal(scheduler, blocker);
        var result = await Terminal(scheduler, run);
        Assert(result.State == "CANCELLED" && result.Error!.Contains("REVOKED") && result.Error.Contains("CANCEL_REQUESTED") && factory.TargetConnects == 0,
            "queued cancellation and revocation must both survive deterministically without opening a transport");
    }

    private static async Task RevokedDuringDisposeSealsOrClassifiesResult(bool priorFailure)
    {
        using var f = new Fixture();
        f.Definition("result-boundary", f.SuperId);
        var factory = new SessionFactory
        {
            OnExecute = (_, _) => priorFailure ? throw new IOException("synthetic transport failure") : Task.FromResult("bounded fixture"),
            OnDispose = () => f.Execute("UPDATE web_users SET disabled=1 WHERE id=$id", f.SuperId)
        };
        await using var scheduler = f.Scheduler(factory);
        Assert(scheduler.TryScheduleDefinition("result-boundary", out var run, f.SuperId), "result boundary task admitted");
        var result = await Terminal(scheduler, run);
        Assert(result.State == "CANCELLED" && result.Error!.Contains("BEFORE_RESULT_COMMIT") && result.Error.Contains("REVOKED_AFTER_START"),
            "revocation between command completion and commit must be rechecked");
        if (priorFailure) Assert(result.Error!.Contains("PRIOR_OUTCOME=IOException"), "prior transport failure and revocation must both remain traceable");
        else Assert(f.Catalog.RecentTaskRuns().Single().CaptureStatus == "PARTIAL", "completed bounded output must be sealed without publishing a successful task");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "netconsole-next-task-scope-" + Guid.NewGuid().ToString("N"));
        public Catalog Catalog { get; }
        public P2B5SiteScopeStore Scopes { get; }
        public TaskSiteScopeAuthorizer Authorizer { get; }
        public string ProfileId { get; }
        public string AdminId { get; }
        public string SuperId { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var settings = new RuntimeSettings { DataRoot = Root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(settings); Catalog.Initialize();
            var v13Flag = P2B2MigrationContext.ExplicitMigrationEnvironmentVariable;
            var scopeFlag = P2B5SiteScopeMigrationContext.ExplicitMigrationEnvironmentVariable;
            var oldV13 = Environment.GetEnvironmentVariable(v13Flag);
            var oldScope = Environment.GetEnvironmentVariable(scopeFlag);
            try
            {
                Environment.SetEnvironmentVariable(v13Flag, "1");
                Environment.SetEnvironmentVariable(scopeFlag, "1");
                using var db = Open();
                P2B2SchemaV13Migration.Apply(db, P2B2MigrationContext.ForIsolatedTest(Path.Combine(Root, "catalog.db")));
                P2B5SiteScopeMigration.Apply(db, P2B5SiteScopeMigrationContext.ForIsolatedTest(Path.Combine(Root, "catalog.db")));
            }
            finally { Environment.SetEnvironmentVariable(v13Flag, oldV13); Environment.SetEnvironmentVariable(scopeFlag, oldScope); }
            AdminId = Scalar("SELECT id FROM web_users WHERE username='admin'");
            SuperId = Scalar("SELECT id FROM web_users WHERE username='superadmin'");
            Scopes = new(settings); Authorizer = new(Catalog, Scopes);
            ProfileId = CreateProfile("192.0.2.21");
        }

        public string CreateProfile(string host)
        {
            var id = Guid.NewGuid().ToString("N");
            Catalog.CreateH3cDeviceProfile(new(id, host, 22, "fixture-reader", "FIXTURE_REFERENCE",
                "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('='), true, 0, 0));
            return id;
        }
        public TaskDefinitionSnapshot Definition(string id, string actor)
        {
            var snapshot = Authorizer.CaptureDefinition(ProfileId, actor, actor == AdminId ? "ADMIN" : "SUPERADMIN");
            Catalog.CreateTaskDefinition(id, "H3C_SSH_READONLY", ProfileId, "display version", TimeSpan.FromSeconds(5), scopeSnapshot: snapshot);
            return Catalog.GetTaskDefinition(id)!;
        }
        public BoundedCollectionScheduler Scheduler(SessionFactory factory) =>
            new(factory, 1, 4, Catalog, resultSink: new H3cTaskRawResultSink(Catalog), scopeAuthorizer: Authorizer);
        public void Grant(string site, int revision = 1)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = @"INSERT INTO web_user_site_memberships(id,user_id,site_reference,claim_state,source_kind,source_reference,revision,lifecycle,created_ms,updated_ms)
                VALUES($id,$user,$site,'ACTIVE','DIRECT',$site,$revision,'ACTIVE',1,1)";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$user", AdminId);
            command.Parameters.AddWithValue("$site", site); command.Parameters.AddWithValue("$revision", revision); command.ExecuteNonQuery();
        }
        public void Execute(string sql, string user)
        { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$id", user); command.ExecuteNonQuery(); }
        public string Scalar(string sql)
        { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar())!; }
        private SqliteConnection Open()
        { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "catalog.db"), Pooling = false, ForeignKeys = true }.ToString()); db.Open(); return db; }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }

    private sealed class SessionFactory : IDeviceSessionFactory
    {
        public Func<string, CancellationToken, Task> OnConnect { get; init; } = (_, _) => Task.CompletedTask;
        public Func<string, CancellationToken, Task<string>> OnExecute { get; init; } = (_, _) => Task.FromResult("synthetic Comware response");
        public Action OnDispose { get; init; } = () => { };
        public int TargetConnects, TargetCommands, Disposals;
        public IDeviceSession Create(string target) => new Session(this, target);
        private sealed class Session(SessionFactory owner, string target) : IDeviceSession
        {
            public Task ConnectAsync(CancellationToken token)
            { if (target != "blocker") Interlocked.Increment(ref owner.TargetConnects); return owner.OnConnect(target, token); }
            public Task<string> ExecuteAsync(string command, CancellationToken token)
            { if (target != "blocker") Interlocked.Increment(ref owner.TargetCommands); return owner.OnExecute(target, token); }
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposals); owner.OnDispose(); return ValueTask.CompletedTask; }
        }
    }

    private static async Task<DeviceCollectionSnapshot> Terminal(BoundedCollectionScheduler scheduler, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var state = scheduler.Get(id)!;
            if (state.State is "SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMED_OUT") return state;
            await Task.Delay(10, timeout.Token);
        }
    }
    private static void ExpectScopeDenied(Action action)
    { try { action(); throw new Exception("scope denial expected"); } catch (TaskScopeRevokedException) { } }
    private static void ExpectInvalid(Action action)
    { try { action(); throw new Exception("invalid scope mutation expected"); } catch (InvalidOperationException) { } }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
