using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Text.Json;

internal static class PingLossEvidenceTests
{
    private static int checks;
    public static async Task RunAsync()
    {
        using var f = new Fixture();
        var first = await f.Seal(H3cCommandPolicy.DisplayVersion,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "display_version.txt")));
        var before = f.Catalog.QueryH3cFacts(f.Profile, history: true).Single();
        var cutoff = long.Parse(f.Scalar($"SELECT ended_ms FROM task_runs WHERE id='{first}'"));
        var run = f.Run(cutoff);
        var initial = f.Project(run)!;
        Check(initial.State == "HISTORICAL_CONTEXT" && initial.Facts.Single().Id == before.Id, "nearest prior sealed Raw is found");
        Check(initial.ConnectedApState == "UNKNOWN" && initial.ConnectedApReason == "NO_AUTHORITATIVE_CLIENT_AP_ASSOCIATION", "device facts never infer an AP");
        var evidence = initial.Facts.Single();
        Check(evidence.CapturedMs <= cutoff && evidence.CollectionEndedMs <= cutoff && evidence.AgeMs == cutoff - before.CapturedMs &&
            evidence.RawReferences.Single().RunId == first && evidence.Source == "H3C_TASK_RAW:" + first,
            "capture, completion, age and immutable raw references are preserved");
        var json = JsonSerializer.Serialize(initial);
        Check(!json.Contains("payloadJson") && !json.Contains("output\":"), "response does not expose full transcripts or payloads");

        await Task.Delay(5);
        var later = await f.Seal(H3cCommandPolicy.DisplayVersion, "H3C Comware Software, Version 7.1.070, Release 9999\nH3C WA6320");
        var future = f.Catalog.QueryH3cFacts(f.Profile, "device.identity").Single();
        Check(future.Id != before.Id && !f.Catalog.QueryH3cFacts(f.Profile, history: true).Single(x => x.Id == before.Id).IsCurrent,
            "fixture advances Current after the selected loss sample");
        // Synthetic extra identities share a real sealed manifest solely to exercise SQL cutoff/limit order.
        f.Execute($@"WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<25)
            INSERT INTO h3c_facts(id,profile_id,kind,identity_key,status,source,captured_ms,is_current,payload_json)
            SELECT 'future-'||n,profile_id,'interface','future-'||n,status,source,captured_ms,0,payload_json
            FROM h3c_facts,seq WHERE id='{future.Id}'");
        Check(f.Project(run)!.Facts.Single().Id == before.Id, "future Current and more than the result limit of future facts cannot hide prior evidence");

        f.Execute($"UPDATE task_runs SET ended_ms={cutoff + 1} WHERE id='{first}'");
        Check(f.Project(run)!.State == "NO_PRIOR_VERIFIED_EVIDENCE", "an observation whose task only completed after the sample is excluded");
        f.Execute($"UPDATE task_runs SET ended_ms={cutoff} WHERE id='{first}'");
        f.Execute($"UPDATE raw_segments SET state='INCOMPLETE' WHERE task_run_id='{first}'");
        Check(f.Project(run)!.Facts.Count == 0, "revoked Raw manifest cannot masquerade as reliable prior evidence");
        f.Execute($"UPDATE raw_segments SET state='FINALIZED' WHERE task_run_id='{first}'");
        f.Catalog.ImportH3cOfflineReplay(f.Profile, new Dictionary<string, string> { [H3cCommandPolicy.DisplayVersion] = "H3C Comware Software, Version 7.1.070, Release 8888\nH3C WA6320" }, cutoff, "offline-nearby");
        Check(f.Project(run)!.Facts.Single().Id == before.Id, "offline replay and nearby unverified evidence are excluded");

        var other = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "other" }, "other Site");
        Check(f.Project(run, other) is null && f.Catalog.QueryPingLossFacts(f.Profile, f.Device, "local", cutoff, other, 21).Count == 0,
            "projection and SQL both enforce exact authorized Site");
        Check(f.Catalog.QueryPingLossFacts(f.Profile, "another-device", "local", cutoff, P2B5SiteScopeDecision.Global, 21).Count == 0,
            "another device cannot reuse the same profile endpoint");
        f.Execute("INSERT INTO sites(id,name) VALUES('other','Other'); UPDATE device_inventory SET site_id='other' WHERE id='" + f.Device + "'");
        Check(f.Project(run)!.State == "TARGET_BINDING_UNPROVEN", "Site edits cannot relabel historical evidence");
        f.Execute("UPDATE device_inventory SET site_id='local' WHERE id='" + f.Device + "'");
        f.Execute($"UPDATE device_inventory SET updated_ms={run.CreatedMs + 1} WHERE id='{f.Device}'");
        Check(f.Project(run)!.State == "TARGET_BINDING_UNPROVEN", "inventory revision after admission fails closed without an original endpoint snapshot");
        f.Execute($"UPDATE device_inventory SET updated_ms={run.CreatedMs} WHERE id='{f.Device}'");
        f.Execute($"UPDATE h3c_device_profiles SET updated_ms={run.CreatedMs + 1} WHERE id='{f.Profile}'");
        Check(f.Project(run)!.State == "TARGET_BINDING_UNPROVEN", "profile revision after admission cannot be rebound to a historical probe");
        f.Execute($"UPDATE h3c_device_profiles SET updated_ms={run.CreatedMs} WHERE id='{f.Profile}'");
        f.Execute("UPDATE h3c_device_profiles SET host='unprovable.example' WHERE id='" + f.Profile + "'");
        Check(f.Project(run)!.State == "TARGET_BINDING_UNPROVEN", "DNS is never resolved anew to infer past target identity");
        f.Execute("UPDATE h3c_device_profiles SET host='192.0.2.71' WHERE id='" + f.Profile + "'");
        var tcp = run.Items[0] with { QuickPing = run.Items[0].QuickPing! with { Protocol = "TCP", Port = 2222 } };
        Check(f.Project(run with { Items = [tcp] })!.State == "TARGET_BINDING_UNPROVEN", "TCP port identity must match the captured target");

        var item = run.Items[0];
        var ping = item.QuickPing!;
        foreach (var sample in new[] { ping.Samples[0] with { Status = "REPLIED", LatencyMs = 0 },
            ping.Samples[0] with { Status = "CANCELLED" }, ping.Samples[0] with { CapturedMs = run.CreatedMs - 1 },
            ping.Samples[0] with { CapturedMs = cutoff + 1 }, ping.Samples[0] with { LatencyMs = 1 } })
            Check(f.Project(run with { Items = [item with { QuickPing = ping with { Samples = [sample] } }] }) is null,
                "only actual bounded lost probes can request context: " + sample.Status + "/" + sample.CapturedMs);
        Check(f.Project(run, sequence: 2) is null, "missing or paused unsent sequence is not loss");
        Check(f.Project(run with { Items = [item with { QuickPing = ping with { Samples = [ping.Samples[0], ping.Samples[0]] } }] }) is null,
            "ambiguous duplicate sample identity fails closed");
        Check(f.Project(run with { Items = [item with { Kind = "QUICK_PING" }] }) is null, "endpoint is scoped to Long Ping");
        Check(f.Project(run with { Items = [item with { StartedMs = null }] }) is null, "missing probe start boundary is unknown");

        f.Execute($@"WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<25)
            INSERT INTO h3c_facts(id,profile_id,kind,identity_key,status,source,captured_ms,is_current,payload_json)
            SELECT 'tied-'||n,profile_id,'interface','port-'||n,status,source,captured_ms,0,payload_json
            FROM h3c_facts,seq WHERE id='{before.Id}'");
        var tied = f.Project(run)!;
        Check(tied.Facts.Count == 20 && tied.HasMore && tied.Facts.All(x => x.CapturedMs == before.CapturedMs),
            "tied observations have deterministic bounded output and disclose truncation");
        var staleAt = cutoff + 86_400_000;
        var stale = f.Project(f.Run(staleAt), now: staleAt)!;
        Check(stale.Facts.Any(x => x.AgeMs >= 86_399_000), "old observations retain their full age without claiming freshness");
        var tasks = f.Scalar("SELECT COUNT(*) FROM task_runs");
        _ = f.Project(run);
        Check(f.Scalar("SELECT COUNT(*) FROM task_runs") == tasks, "context read creates no task or device command");
        var history = Path.Combine(f.Root, "diagnostics");
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, run.Id + ".json"), JsonSerializer.Serialize(run));
        using var diagnostics = new NetworkDiagnosticsService(f.Catalog);
        Check(PingLossEvidence.Get(f.Catalog, diagnostics, run.Id, "loss", 1, "local-api-token", P2B5SiteScopeDecision.Global)
            is { State: "HISTORICAL_CONTEXT", Sequence: 1 }, "authorized saved diagnostic history resolves the exact prior observation context");
        Check(PingLossEvidence.Get(f.Catalog, diagnostics, run.Id, "loss", 1, "local-api-token", other) is null,
            "saved diagnostic evidence remains hidden outside its exact authorized Site");
        try { PingLossEvidence.Get(f.Catalog, diagnostics, "missing", "loss", 1, "revoked-actor", P2B5SiteScopeDecision.Global); throw new Exception("inactive actor accepted"); }
        catch (DeviceSiteScopeException) { Check(true, "history reader authorization is checked before lookup"); }
        Console.WriteLine($"PING_LOSS_EVIDENCE_TESTS={checks}/{checks} PASS");
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WORKSPACE_ROOT") ??
            (OperatingSystem.IsWindows() ? @"D:\study\NetConsole-Next-Workspace" : Path.Combine(Path.GetTempPath(), "netconsole-next-workspace")),
            "test-data", "ping-loss-evidence-tests", Guid.NewGuid().ToString("N"));
        public readonly string Profile = Guid.NewGuid().ToString("N");
        public readonly Catalog Catalog;
        public readonly RuntimeSettings Settings;
        public readonly string Device;
        public readonly long Created;
        public Fixture()
        {
            Settings = new() { DataRoot = Root, SyslogBind = IPAddress.Loopback, SyslogPort = 0, WebPort = 18771, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings); Catalog.Initialize();
            Catalog.CreateH3cDeviceProfile(new(Profile, "192.0.2.71", 22, "fixture", "FIXTURE_REFERENCE",
                "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('='), true, 0, 0));
            Device = Catalog.QueryManagedDeviceInventory().Items.Single().Id;
            Created = Catalog.GetDeviceInventoryDetail(Device)!.UpdatedMs;
        }
        public async Task<string> Seal(string command, string output)
        {
            var id = Guid.NewGuid().ToString("N");
            var authorizer = new TaskSiteScopeAuthorizer(Catalog, new(Settings));
            Catalog.CreateTaskDefinition(id, "H3C_SSH_READONLY", Profile, command, TimeSpan.FromSeconds(5),
                scopeSnapshot: authorizer.CaptureDefinition(Profile, "local-api-token"));
            var definition = Catalog.GetTaskDefinition(id)!;
            var run = Catalog.CreateTaskRun(definition);
            Catalog.TransitionTaskRun(run, "CONNECTING"); Catalog.TransitionTaskRun(run, "RUNNING");
            var raw = await new H3cTaskRawResultSink(Catalog).PersistCommandAsync(run, definition, command, output, "COMPLETE", 0);
            Catalog.CompleteTaskRunWithRawOutput(run, raw, "SUCCEEDED", null);
            Check(Catalog.PublishH3cTaskFacts(run).Status == "PUBLISHED", "fixture publishes real sealed Raw");
            return run;
        }
        public NetworkDiagnosticRunSnapshot Run(long sampleAt)
        {
            var ping = new QuickPingSnapshot("ICMP", "192.0.2.71", null, 10, 1, 0, 1, 100, null, null, null,
                [new(1, sampleAt, "TIMED_OUT", null)]);
            var item = new NetworkDiagnosticItemSnapshot("loss", Device, "Fixture", "local", "LONG_PING", "FAILED", "LOST", null,
                Created, sampleAt, null, 1, ["LONG_PING_ICMP"], QuickPing: ping);
            return new("diagnostic", "local-api-token", "FAILED", "local", 1000, 1, 1, 1, 0, 1, 0, 0, 0, 0, 100,
                Created, Created, sampleAt, null, [item]);
        }
        public PingLossEvidenceSnapshot? Project(NetworkDiagnosticRunSnapshot run, P2B5SiteScopeDecision? scope = null, int sequence = 1, long? now = null) =>
            PingLossEvidence.Project(Catalog, run, "loss", sequence, scope ?? P2B5SiteScopeDecision.Global, now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        private SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "catalog.db"), Pooling = false }.ToString()); db.Open(); return db; }
        public void Execute(string sql) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        public string Scalar(string sql) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar())!; }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
}
