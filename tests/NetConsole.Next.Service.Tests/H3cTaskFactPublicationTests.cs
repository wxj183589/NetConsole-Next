using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Text.Json;

internal static class H3cTaskFactPublicationTests
{
    public static async Task RunAsync()
    {
        await PublishesSealedCommandAndSurvivesRestart();
        await SeparateRunsNeverFabricateCoherentApIdentity();
        await FailedAndPartialRunsNeverPublish();
        await EndpointChangesStopNewCommands();
        await ChangedEndpointCannotRelabelBoundedOutput();
        await ProjectionFailureCannotInvalidateSealedRaw();
        await TamperedRawCannotRebuildFacts();
        LegacyDefinitionsCannotAcquireEndpointProof();
        await ReferencedCommandCannotInvalidateAdmittedRaw();
        await MultiCommandRawOutputsRemainIndependent();
        await MultiCommandFailureKeepsEarlierRawBounded();
        Console.WriteLine("H3C_TASK_FACT_PUBLICATION_TESTS=11/11 PASS");
    }

    private static async Task PublishesSealedCommandAndSurvivesRestart()
    {
        using var f = new Fixture();
        await using var scheduler = f.Scheduler(new Factory());
        var run = await f.Run(scheduler, H3cCommandPolicy.DisplayVersion);
        var fact = f.Catalog.QueryH3cFacts(f.Profile).Single();
        var raw = f.Catalog.RecentTaskRuns().Single(x => x.Id == run.Id);
        using var json = JsonDocument.Parse(fact.PayloadJson);
        Assert(run.State == "SUCCEEDED" && fact.CollectedRawVerified && fact.DataState == "COLLECTED_RAW_VERIFIED" &&
            fact.Status == "STALE" && fact.Freshness == "AGE_DEPENDENT" &&
            json.RootElement.GetProperty("rawProvenance").GetProperty("outputSha256").GetString() == raw.RawOutputSha256,
            "successful scoped collector must publish existing-schema facts with exact verified Raw references and explicit time limits");
        var before = f.Rows();
        Assert(f.Catalog.PublishH3cTaskFacts(run.Id).Status == "PUBLISHED" && f.Rows() == before,
            "republication must be idempotent");
        Assert(new Catalog(f.Settings).QueryH3cFacts(f.Profile).Single().CollectedRawVerified,
            "Raw-reference verification must survive catalog restart");
        f.Execute("UPDATE raw_segments SET state='INCOMPLETE' WHERE task_run_id='" + run.Id + "'");
        Assert(!f.Catalog.QueryH3cFacts(f.Profile).Single().CollectedRawVerified, "damaged Raw catalog state cannot retain verified provenance");
    }

    private static async Task SeparateRunsNeverFabricateCoherentApIdentity()
    {
        using var f = new Fixture();
        await using var scheduler = f.Scheduler(new Factory());
        await f.Run(scheduler, H3cCommandPolicy.DisplayVersion);
        await f.Run(scheduler, H3cCommandPolicy.DisplayWlanApAll);
        await f.Run(scheduler, H3cCommandPolicy.DisplayWlanApAllAddress);
        var facts = f.Catalog.QueryH3cFacts(f.Profile);
        var resource = new WirelessReadModelService(f.Catalog).GetSnapshot(P2B5SiteScopeDecision.Global).ApResources.Single();
        Assert(facts.Count == 3 && facts.All(x => x.CollectedRawVerified) && facts.Select(x => x.Source).Distinct().Count() == 3 &&
            resource.IdentityStatus == "CANDIDATE" && resource.NormalizedMacSet.Count == 0 && resource.Observations.Single().NormalizedState == "STALE",
            "independent command runs must not correlate AP names/timestamps into a falsely coherent physical identity or live state");
    }

    private static async Task FailedAndPartialRunsNeverPublish()
    {
        foreach (var partial in new[] { false, true })
        {
            using var f = new Fixture();
            await using var scheduler = f.Scheduler(new Factory { Execute = _ => partial
                ? throw new H3cPartialOutputException("partial synthetic output", "SYNTHETIC_PARTIAL", false)
                : throw new IOException("synthetic transport failure") });
            var run = await f.Run(scheduler, H3cCommandPolicy.DisplayVersion);
            Assert(run.State == "FAILED" && f.Catalog.QueryH3cFacts(f.Profile, history: true).Count == 0 &&
                f.Catalog.PublishH3cTaskFacts(run.Id).Status == "NO_COMPLETE_SUCCEEDED_RAW", "failed/partial tasks cannot publish verified facts");
        }
    }

    private static async Task EndpointChangesStopNewCommands()
    {
        using var f = new Fixture(); var calls = 0;
        await using var scheduler = f.Scheduler(new Factory
        {
            Connect = () => f.Execute("UPDATE h3c_device_profiles SET port=2222"),
            Execute = _ => { calls++; return "must not execute"; }
        });
        var run = await f.Run(scheduler, H3cCommandPolicy.DisplayVersion);
        Assert(run.State == "CANCELLED" && calls == 0 && run.Error!.Contains("TASK_ENDPOINT_REVOKED"),
            "endpoint changes after connect cannot start a command under a frozen target");
    }

    private static async Task ChangedEndpointCannotRelabelBoundedOutput()
    {
        using var f = new Fixture();
        await using var scheduler = f.Scheduler(new Factory { Execute = command =>
        {
            f.Execute("UPDATE h3c_device_profiles SET host='192.0.2.42'");
            return Factory.Output(command);
        } });
        var run = await f.Run(scheduler, H3cCommandPolicy.DisplayVersion);
        var result = f.Catalog.RecentTaskRuns().Single(x => x.Id == run.Id);
        var path = Path.Combine(f.Root, result.RawOutputPath!.Replace('/', Path.DirectorySeparatorChar));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert(run.State == "CANCELLED" && result.CaptureStatus == "PARTIAL" &&
            json.RootElement.GetProperty("device_host").GetString() == "192.0.2.41" && f.Catalog.QueryH3cFacts(f.Profile).Count == 0,
            "bounded output retains the admitted host, never the newly edited endpoint; revoked output remains partial Raw only");
    }

    private static async Task ProjectionFailureCannotInvalidateSealedRaw()
    {
        using var f = new Fixture();
        f.Execute("CREATE TRIGGER reject_projection BEFORE INSERT ON h3c_facts BEGIN SELECT RAISE(FAIL,'synthetic projection failure'); END");
        await using var scheduler = f.Scheduler(new Factory());
        var run = await f.Run(scheduler, H3cCommandPolicy.DisplayVersion);
        var raw = f.Catalog.RecentTaskRuns().Single(x => x.Id == run.Id);
        Assert(run.State == "SUCCEEDED" && raw.State == "SUCCEEDED" && raw.RawOutputPath is not null &&
            f.Catalog.QueryH3cFacts(f.Profile).Count == 0 && f.Scalar("SELECT COUNT(*) FROM audit_events WHERE action='H3C_FACT_PUBLICATION' AND outcome='REPAIR_REQUIRED'") == "1",
            "projection failure must leave the already committed Raw/task result intact and record repair status");
        f.Execute("DROP TRIGGER reject_projection");
        Assert(f.Catalog.PublishH3cTaskFacts(run.Id).Status == "PUBLISHED", "verified Raw can rebuild the failed projection explicitly without reconnecting");
    }

    private static async Task TamperedRawCannotRebuildFacts()
    {
        using var f = new Fixture();
        await using var scheduler = f.Scheduler(new Factory());
        var run = await f.Run(scheduler, H3cCommandPolicy.DisplayVersion);
        var relative = f.Catalog.RecentTaskRuns().Single(x => x.Id == run.Id).RawOutputPath!;
        var path = Path.Combine(f.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        File.AppendAllText(path, "tampered");
        f.Execute("DELETE FROM h3c_facts");
        try { f.Catalog.PublishH3cTaskFacts(run.Id); throw new Exception("tampered Raw was accepted"); }
        catch (InvalidDataException) { }
        Assert(f.Catalog.QueryH3cFacts(f.Profile).Count == 0, "Raw content/hash verification must precede all rebuilt fact writes");
    }

    private static void LegacyDefinitionsCannotAcquireEndpointProof()
    {
        using var f = new Fixture();
        var scope = f.Authorizer.CaptureDefinition(f.Profile, "local-api-token") with { Endpoint = null };
        var definition = new TaskDefinitionSnapshot("legacy", "H3C_SSH_READONLY", f.Profile, "display version", 1000, true, "NEVER", scope);
        try { f.Authorizer.CaptureExecution(definition, "local-api-token"); throw new Exception("legacy endpoint inferred"); }
        catch (TaskScopeRevokedException ex) { Assert(ex.Reason.Contains("TASK_ENDPOINT_REVOKED"), "missing endpoint evidence must fail closed"); }
    }

    private static async Task ReferencedCommandCannotInvalidateAdmittedRaw()
    {
        using var f = new Fixture();
        var rejected = false;
        await using var scheduler = f.Scheduler(new Factory { Connect = () =>
        {
            var id = f.Scalar("SELECT id FROM task_definitions LIMIT 1");
            try { f.Catalog.CreateTaskDefinition(id, "H3C_SSH_READONLY", f.Profile, H3cCommandPolicy.DisplayWlanApAll, TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException) { rejected = true; }
        } });
        var run = await f.Run(scheduler, H3cCommandPolicy.DisplayVersion);
        Assert(rejected && run.State == "SUCCEEDED" && f.Catalog.QueryH3cFacts(f.Profile).Single().Kind == "device.identity",
            "command edits cannot invalidate a queued/running Raw manifest; use a new definition for a new command");
    }

    private static async Task MultiCommandRawOutputsRemainIndependent()
    {
        using var f = new Fixture();
        var definition = Guid.NewGuid().ToString("N");
        var first = H3cCommandPolicy.DisplayVersion;
        var second = H3cCommandPolicy.DisplayWlanApAll;
        f.Catalog.CreateTaskDefinition(definition, "H3C_SSH_READONLY", f.Profile, first, TimeSpan.FromSeconds(5),
            scopeSnapshot: f.Authorizer.CaptureDefinition(f.Profile, "local-api-token"), commands: new[] { first, second });
        await using var scheduler = f.Scheduler(new Factory());
        Assert(scheduler.TryScheduleDefinition(definition, out var runId, "local-api-token"), "multi-command task admitted");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        DeviceCollectionSnapshot run;
        while (true)
        {
            run = scheduler.Get(runId)!;
            if (run.State is "SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMED_OUT") break;
            await Task.Delay(10, timeout.Token);
        }
        Assert(run.State == "SUCCEEDED" && f.Scalar($"SELECT COUNT(*) FROM task_run_command_outputs WHERE task_run_id='{runId}'") == "2",
            "a successful multi-command task must seal one command-level Raw row per command");
        var commands = f.Catalog.GetTaskRunCommands(runId);
        Assert(commands.Count == 2 && commands.All(item => item.State == "SUCCEEDED") &&
            commands[0].Command == first && commands[1].Command == second,
            "task command detail must preserve order and independent success states");
        var facts = f.Catalog.QueryH3cFacts(f.Profile, history: true);
        Assert(facts.Count >= 2 && facts.All(item => item.CollectedRawVerified),
            "complete multi-command facts must retain a verified collection-level Raw provenance");
    }

    private static async Task MultiCommandFailureKeepsEarlierRawBounded()
    {
        using var f = new Fixture();
        var definition = Guid.NewGuid().ToString("N");
        var first = H3cCommandPolicy.DisplayVersion;
        var second = H3cCommandPolicy.DisplayWlanApAll;
        f.Catalog.CreateTaskDefinition(definition, "H3C_SSH_READONLY", f.Profile, first, TimeSpan.FromSeconds(5),
            scopeSnapshot: f.Authorizer.CaptureDefinition(f.Profile, "local-api-token"), commands: new[] { first, second });
        await using var scheduler = f.Scheduler(new Factory { Execute = command =>
            command == second ? throw new IOException("synthetic second command failure") : Factory.Output(command) });
        Assert(scheduler.TryScheduleDefinition(definition, out var runId, "local-api-token"), "failing multi-command task admitted");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        DeviceCollectionSnapshot run;
        while (true)
        {
            run = scheduler.Get(runId)!;
            if (run.State is "SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMED_OUT") break;
            await Task.Delay(10, timeout.Token);
        }
        var commands = f.Catalog.GetTaskRunCommands(runId);
        Assert(run.State == "FAILED" && commands.Count == 2 && commands[0].State == "PARTIAL" && commands[1].State == "FAILED" &&
            f.Catalog.QueryH3cFacts(f.Profile, history: true).Count == 0,
            "a failed later command must retain earlier Raw as partial evidence without publishing facts");
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "next-task-facts-" + Guid.NewGuid().ToString("N"));
        public readonly string Profile = Guid.NewGuid().ToString("N");
        public readonly RuntimeSettings Settings;
        public readonly Catalog Catalog;
        public readonly TaskSiteScopeAuthorizer Authorizer;
        public Fixture()
        {
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings); Catalog.Initialize();
            Catalog.CreateH3cDeviceProfile(new(Profile, "192.0.2.41", 22, "fixture", "FIXTURE_REFERENCE",
                "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('='), true, 0, 0));
            Execute("UPDATE device_inventory SET device_type='AC'");
            Authorizer = new(Catalog, new(Settings));
        }
        public BoundedCollectionScheduler Scheduler(Factory factory) => new(factory, 1, 4, Catalog,
            resultSink: new H3cTaskRawResultSink(Catalog), scopeAuthorizer: Authorizer);
        public async Task<DeviceCollectionSnapshot> Run(BoundedCollectionScheduler scheduler, string command)
        {
            var definition = Guid.NewGuid().ToString("N");
            Catalog.CreateTaskDefinition(definition, "H3C_SSH_READONLY", Profile, command, TimeSpan.FromSeconds(5),
                scopeSnapshot: Authorizer.CaptureDefinition(Profile, "local-api-token"));
            Assert(scheduler.TryScheduleDefinition(definition, out var id, "local-api-token"), "fixture task admitted");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true) { var state = scheduler.Get(id)!; if (state.State is "SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMED_OUT") return state; await Task.Delay(10, timeout.Token); }
        }
        public string Rows() => JsonSerializer.Serialize(Catalog.QueryH3cFacts(Profile, history: true, limit: 1000));
        private SqliteConnection Open()
        { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "catalog.db"), Pooling = false, ForeignKeys = true }.ToString()); db.Open(); return db; }
        public void Execute(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public string Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar())!; }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }

    private sealed class Factory : IDeviceSessionFactory
    {
        public Action Connect { get; init; } = () => { };
        public Func<string, string> Execute { get; init; } = Output;
        public static string Output(string command) => command switch
        {
            H3cCommandPolicy.DisplayVersion => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "display_version.txt")),
            H3cCommandPolicy.DisplayWlanApAll => "<AC>display wlan ap all\nAP-ONE 1 R/M WA6320 SERIAL-001 group-a 1 day\n<AC>",
            H3cCommandPolicy.DisplayWlanApAllAddress => "<AC>display wlan ap all address\nAP-ONE 192.0.2.42 4c6f-d608-0400\n<AC>",
            _ => throw new InvalidOperationException("unexpected fixture command")
        };
        public IDeviceSession Create(string target) => new Session(this);
        private sealed class Session(Factory owner) : IDeviceSession
        {
            public Task ConnectAsync(CancellationToken token) { owner.Connect(); return Task.CompletedTask; }
            public Task<string> ExecuteAsync(string command, CancellationToken token) => Task.FromResult(owner.Execute(command));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
