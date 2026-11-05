using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Text.Json;

internal static class H3cFactScopeTests
{
    public static void Run()
    {
        LateSamplesCannotRollBackCurrent();
        ExactReplayDoesNotWriteOrPromoteHistory();
        ConflictingRevisionRollsBackWholeBatch();
        EqualTimestampSourcesCannotHideContradiction();
        CapturedScopeSurvivesRestartAndFiltersBeforeLimit();
        SiteMoveCannotRelabelFactsOrHistory();
        EndpointOrDeviceRebindCannotRelabelFacts();
        LegacyFactsRemainGlobalDiagnosticsOnly();
        Console.WriteLine("H3C_FACT_SCOPE_TESTS=8/8 PASS");
    }

    private static void LateSamplesCannotRollBackCurrent()
    {
        using var f = new Fixture();
        f.Import(3000, "R/B"); f.Import(1000); f.Import(2000, "I");
        Assert(f.Catalog.QueryH3cFacts(f.Profile, history: false).All(x => x.CapturedMs == 3000), "late imports must remain historical");
        var restarted = new Catalog(f.Settings);
        Assert(restarted.QueryH3cFacts(f.Profile, history: false).All(x => x.CapturedMs == 3000), "Current must survive restart without rollback");
        Assert(restarted.QueryH3cFacts(f.Profile, history: true).Count == 9, "all late revisions must remain inspectable");
    }

    private static void ExactReplayDoesNotWriteOrPromoteHistory()
    {
        using var f = new Fixture();
        f.Import(1000); f.Import(2000);
        f.Execute(@"CREATE TABLE writes(n INTEGER); INSERT INTO writes VALUES(0);
            CREATE TRIGGER fact_write_insert AFTER INSERT ON h3c_facts BEGIN UPDATE writes SET n=n+1; END;
            CREATE TRIGGER fact_write_update AFTER UPDATE ON h3c_facts BEGIN UPDATE writes SET n=n+1; END;");
        f.Import(1000); var result = f.Import(2000);
        Assert(f.Scalar("SELECT n FROM writes") == "0" && result.CurrentCount == 3 && result.HistoricalCount == 3,
            "exact replay must perform no fact writes, and old replay must not promote history");
    }

    private static void ConflictingRevisionRollsBackWholeBatch()
    {
        using var f = new Fixture();
        f.Catalog.ImportH3cOfflineReplay(f.Profile, new Dictionary<string, string> { [H3cCommandPolicy.DisplayWlanApAll] = Fixture.Ap("R/M") }, 1000);
        var before = f.Rows();
        ExpectConflict(() => f.Import(1000, "R/B")); // device.identity is inserted before the AP conflict.
        Assert(f.Rows() == before && f.Catalog.QueryH3cFacts(f.Profile, "device.identity").Count == 0,
            "same-revision conflicts must roll back earlier facts in the same batch");
        f.Import(2000);
        before = f.Rows();
        ExpectConflict(() => f.Catalog.ImportH3cOfflineReplay(f.Profile,
            new Dictionary<string, string> { [H3cCommandPolicy.DisplayWlanApAll] = "" }, 1000));
        Assert(f.Rows() == before, "a conflicting historical revision must also remain immutable");
    }

    private static void EqualTimestampSourcesCannotHideContradiction()
    {
        using var f = new Fixture(); f.Import(1000);
        f.Catalog.ImportH3cOfflineReplay(f.Profile, Fixture.Outputs(), 1000, "another-offline-source");
        Assert(f.Catalog.QueryH3cFacts(f.Profile).Count == 3 && f.Catalog.QueryH3cFacts(f.Profile, history: true).Count == 6,
            "identical equal-time evidence can retain provenance without changing Current");
        var before = f.Rows();
        ExpectConflict(() => f.Catalog.ImportH3cOfflineReplay(f.Profile, Fixture.Outputs("R/B"), 1000, "third-source"));
        Assert(f.Rows() == before, "a different source label cannot bypass same-time contradiction checks");
    }

    private static void CapturedScopeSurvivesRestartAndFiltersBeforeLimit()
    {
        using var f = new Fixture(); f.Import(1000); f.Import(2000);
        f.Execute("UPDATE h3c_facts SET payload_json=json_remove(payload_json,'$.capturedScope') WHERE captured_ms=2000");
        var restarted = new Catalog(f.Settings);
        var scoped = restarted.QueryH3cFacts(f.Profile, "command.display.wlan.ap.all", true, 1, Fixture.Scope("local"));
        Assert(scoped.Single().CapturedMs == 1000, "captured Site predicate must run before pagination/limit");
        using var json = JsonDocument.Parse(scoped[0].PayloadJson);
        Assert(json.RootElement.GetProperty("capturedScope").GetProperty("deviceId").GetString() == f.Device,
            "stable captured device ID must persist independently of current UI state");
        Assert(restarted.QueryH3cFacts(f.Profile, history: true, scope: Fixture.Scope("other")).Count == 0 &&
            restarted.QueryH3cFacts(f.Profile, history: true, scope: P2B5SiteScopeDecision.Denied("revoked")).Count == 0,
            "other Site and revoked scopes must reveal no facts/counts");
    }

    private static void SiteMoveCannotRelabelFactsOrHistory()
    {
        using var f = new Fixture(); f.Import(1000);
        var read = new WirelessReadModelService(f.Catalog);
        var resource = read.GetSnapshot(Fixture.Scope("local")).ApResources.Single();
        Assert(read.GetRuntimeState(resource.Id, Fixture.Scope("local"), nowMs: 1500)!.Sources.Single().Current is not null,
            "authorized runtime history must work with captured provenance");
        f.Execute(@"INSERT INTO sites VALUES('other','Other fixture'); UPDATE device_inventory SET site_id='other';
            UPDATE collection_sources SET site_id='other' WHERE kind<>'SYSLOG_UDP'");
        Assert(f.Catalog.QueryH3cFacts(f.Profile, history: true, scope: Fixture.Scope("other")).Count == 0 &&
            read.GetSnapshot(Fixture.Scope("other")).ApResources.Count == 0 &&
            read.GetRuntimeState(resource.Id, Fixture.Scope("other"), nowMs: 1500) is null,
            "moving current inventory cannot grant another Site access to captured facts or physical AP history");
        var relabeled = resource with { Observations = resource.Observations.Select(x => x with { SourceSiteId = "other" }).ToArray() };
        Assert(f.Catalog.QueryWirelessRuntimeHistory(relabeled, Fixture.Scope("other"), 10).Samples.Count == 0,
            "history SQL must independently reject captured Site mismatch");
        ExpectConflict(() => f.Import(1000));
        f.Import(3000, "R/B");
        var current = read.GetSnapshot(Fixture.Scope("other")).ApResources.Single();
        var history = read.GetRuntimeState(current.Id, Fixture.Scope("other"), nowMs: 3500)!;
        Assert(history.Sources.Single().Current?.CapturedMs == 3000 && history.Sources.Single().Recent10.Count == 0,
            "a new Site capture must not attach the old Site baseline or transition");
        Assert(f.Catalog.QueryH3cFacts(f.Profile, history: true, scope: P2B5SiteScopeDecision.Global).Count == 6,
            "global diagnostics retains historical provenance without relabeling it");
    }

    private static void EndpointOrDeviceRebindCannotRelabelFacts()
    {
        using var f = new Fixture(); f.Import(1000);
        foreach (var sql in new[] { "UPDATE h3c_device_profiles SET host='192.0.2.32'", "UPDATE h3c_device_profiles SET port=2222",
            "UPDATE device_inventory SET id='replacement-device'" })
        {
            f.Execute(sql);
            Assert(f.Catalog.QueryH3cFacts(f.Profile, history: true, scope: Fixture.Scope("local")).Count == 0 &&
                f.Catalog.QueryWirelessCatalog(P2B5SiteScopeDecision.Global).Observations.Count == 0,
                "endpoint/device rebinding cannot relabel an old snapshot under current authority");
            f.Execute("UPDATE h3c_device_profiles SET host='192.0.2.31',port=22; UPDATE device_inventory SET id='" + f.Device + "'");
        }
    }

    private static void LegacyFactsRemainGlobalDiagnosticsOnly()
    {
        using var f = new Fixture(); f.Import(1000);
        f.Execute("UPDATE h3c_facts SET payload_json=json_remove(payload_json,'$.capturedScope')");
        Assert(f.Catalog.QueryH3cFacts(f.Profile, history: true, scope: Fixture.Scope("local")).Count == 0 &&
            f.Catalog.QueryH3cFacts(f.Profile, history: true, scope: P2B5SiteScopeDecision.Global).Count == 3 &&
            f.Catalog.QueryWirelessCatalog(P2B5SiteScopeDecision.Global).Observations.Count == 0,
            "legacy scope is not inferred from current inventory; diagnostic global access remains available");
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "next-fact-scope-" + Guid.NewGuid().ToString("N"));
        public readonly RuntimeSettings Settings;
        public readonly Catalog Catalog;
        public readonly string Profile = Guid.NewGuid().ToString("N");
        public readonly string Device;
        public Fixture()
        {
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings); Catalog.Initialize();
            Catalog.CreateH3cDeviceProfile(new(Profile, "192.0.2.31", 22, "fixture", "FIXTURE_REFERENCE",
                "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('='), true, 0, 0));
            Device = Catalog.QueryManagedDeviceInventory().Items.Single().Id;
            Execute("UPDATE device_inventory SET device_type='AC',model='WX'");
        }
        public static P2B5SiteScopeDecision Scope(string site) => new("AUTHORIZED", false, new HashSet<string> { site }, "fixture");
        public static string Ap(string state) => "<AC>display wlan ap all\nAP-ONE 1 " + state + " WA6320 SERIAL-001 group-a 1 day\n<AC>";
        public static Dictionary<string, string> Outputs(string state = "R/M") => new()
        {
            [H3cCommandPolicy.DisplayVersion] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "display_version.txt")),
            [H3cCommandPolicy.DisplayWlanApAll] = Ap(state),
            [H3cCommandPolicy.DisplayWlanApAllAddress] = "<AC>display wlan ap all address\nAP-ONE 192.0.2.32 4c6f-d608-0400\n<AC>"
        };
        public H3cOfflineReplayResult Import(long time, string state = "R/M") => Catalog.ImportH3cOfflineReplay(Profile, Outputs(state), time);
        public string Rows() => JsonSerializer.Serialize(Catalog.QueryH3cFacts(Profile, history: true, limit: 1000));
        private SqliteConnection Open()
        { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "catalog.db"), Pooling = false, ForeignKeys = true }.ToString()); db.Open(); return db; }
        public void Execute(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public string Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar())!; }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
    private static void ExpectConflict(Action action)
    { try { action(); throw new InvalidOperationException("expected revision conflict"); } catch (H3cFactRevisionConflictException) { } }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
