using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Security.Cryptography;
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
        await VerifiedWirelessCollectionPublishesEffectiveObservation();
        await MissingAddressPreservesOnlyVerifiedSameRunEvidence();
        await MultiCommandRawCannotRelabelDeviceHost();
        await SuccessfulMultiCommandRunRequiresCompleteManifestSet();
        await MultiCommandFailureKeepsEarlierRawBounded();
        await VlanTablePublishesWholeRevisionsWithRawAndScope();
        await CompleteLldpTablesReplaceOnlyScopedCollectedCurrent();
        await SameIdentityLldpAuthoritiesAndScopesRemainIndependent();
        await LldpLossRecoveryAndRecurrencePersist();
        LldpHistoryRevisionsCannotCrowdOutIdentity();
        Console.WriteLine("H3C_TASK_FACT_PUBLICATION_TESTS=20/20 PASS");
    }

    private static void LldpHistoryRevisionsCannotCrowdOutIdentity()
    {
        using var f = new Fixture();
        f.Catalog.ImportH3cOfflineReplay(f.Profile, new Dictionary<string, string>
            { [H3cCommandPolicy.DisplayLldpNeighborInformationList] = "Local Interface Chassis ID Port ID System Name\nGE1/0/1 0011-2233-4455 GE2/0/1 SAME" }, 1000, "offline-baseline");
        var original = f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor").Single();
        // Fixture-only historical rows model one noisy identity without creating
        // 1100 device sessions. They deliberately do not acquire Raw proof.
        f.Execute(@"WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<1100)
            INSERT INTO h3c_facts(id,profile_id,kind,identity_key,status,source,captured_ms,is_current,payload_json)
            SELECT 'busy-'||n,profile_id,kind,'busy-neighbor',status,source,1000+n,0,payload_json
            FROM h3c_facts,seq WHERE id='" + original.Id + "'");
        Assert(f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor", history: true, limit: 1000).All(x => x.Id != original.Id),
            "fixture reproduces a busy identity crowding out the old neighbor in a revision-limited page");
        var latest = f.Catalog.QueryLatestLldpNeighborHistory(f.Profile, P2B5SiteScopeDecision.Global);
        Assert(latest.Count == 2 && latest.Any(x => x.Id == original.Id) && latest.Single(x => x.IdentityKey == "busy-neighbor").CapturedMs == 2100,
            "fault baseline limit applies after exact authority/scope/identity deduplication");
        var other = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "other" }, "other-claims");
        Assert(f.Catalog.QueryLatestLldpNeighborHistory(f.Profile, other).Count == 0,
            "deduplicated fault history retains the original captured-scope authorization predicate");
    }

    private static async Task LldpLossRecoveryAndRecurrencePersist()
    {
        using var f = new Fixture();
        const string header = "Local Interface Chassis ID Port ID System Name";
        const string populated = header + "\nGE1/0/1 0011-2233-4455 GE2/0/1 SAME";
        var output = populated;
        var factory = new Factory { Execute = _ => output };
        await using var scheduler = f.Scheduler(factory);
        var scopes = new P2B5SiteScopeStore(f.Settings);
        var wireless = new WirelessReadModelService(f.Catalog);
        using var diagnostics = new NetworkDiagnosticsService(f.Catalog);
        var health = new OperationalHealthService(f.Catalog, wireless, diagnostics);
        var recovery = new OperationalRecoveryService(f.Catalog, f.Authorizer, scopes,
            new(f.Catalog, factory), new(f.Catalog, f.Authorizer, scopes, scheduler));
        FaultCenterService Center() => new(f.Catalog, health, recovery, wireless, scopes, f.Authorizer,
            f.Settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<FaultCenterService>.Instance);
        using var center = Center();
        var actor = f.Catalog.QueryFaultMonitorActor()!;
        const string command = H3cCommandPolicy.DisplayLldpNeighborInformationList;
        await f.Run(scheduler, command);
        output = header; await Task.Delay(20); var lostRun = await f.Run(scheduler, command);
        center.Sync(actor, P2B5SiteScopeDecision.Global, 1);
        var incident = center.Query(actor, P2B5SiteScopeDecision.Global, kind: "LLDP_NEIGHBOR_LOST").Items.Single();
        Assert(incident.State == "OPEN" && incident.SourceAuthority == "H3C_TASK_RAW" && incident.CapturedScopeKey != "UNSCOPED" &&
            f.Catalog.QueryH3cFacts(f.Profile, "lldp.table").Single().Source.EndsWith(lostRun.Id),
            "sealed empty table enters the normal persistent scoped fault center");
        center.Sync(actor, P2B5SiteScopeDecision.Global, 1);
        Assert(center.Query(actor, P2B5SiteScopeDecision.Global, kind: "LLDP_NEIGHBOR_LOST").Items.Single().Occurrences == 1,
            "repeated read of the same table is deduplicated");
        output = populated; await Task.Delay(20); await f.Run(scheduler, command);
        Assert(center.Verify(incident.Id, actor, P2B5SiteScopeDecision.Global).State == "RECOVERY_VERIFIED",
            "new complete same-neighbor Raw validates recovery through the normal service entry");
        using var restarted = Center();
        Assert(restarted.Query(actor, P2B5SiteScopeDecision.Global, kind: "LLDP_NEIGHBOR_LOST").Items.Single().State == "RESOLVED",
            "LLDP recovery persists after service reconstruction");
        output = header; await Task.Delay(20); await f.Run(scheduler, command);
        restarted.Sync(actor, P2B5SiteScopeDecision.Global, 1);
        var recurring = restarted.Query(actor, P2B5SiteScopeDecision.Global, kind: "LLDP_NEIGHBOR_LOST").Items.Single();
        Assert(recurring.Id == incident.Id && recurring.State == "REOPENED" && recurring.Occurrences == 2,
            "later exact loss reopens the retained same-partition incident");
    }

    private static async Task SameIdentityLldpAuthoritiesAndScopesRemainIndependent()
    {
        using var f = new Fixture();
        const string header = "Local Interface Chassis ID Port ID System Name";
        var output = header + "\nGE1/0/1 0011-2233-4455 GE2/0/1 SAME";
        const string command = H3cCommandPolicy.DisplayLldpNeighborInformationList;
        f.Catalog.ImportH3cOfflineReplay(f.Profile, new Dictionary<string, string> { [command] = output }, 1000, "offline-A");
        f.Catalog.ImportH3cOfflineReplay(f.Profile, new Dictionary<string, string> { [command] = output }, 1000, "offline-B");
        await using var scheduler = f.Scheduler(new Factory { Execute = _ => output });
        var first = await f.Run(scheduler, command);
        f.Execute("INSERT INTO sites VALUES('other','Other'); UPDATE device_inventory SET site_id='other'; " +
            "UPDATE collection_sources SET site_id='other' WHERE kind<>'SYSLOG_UDP'");
        await Task.Delay(20); var other = await f.Run(scheduler, command);
        var current = f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor");
        Assert(current.Count == 4 && current.Select(x => x.IdentityKey).Distinct().Count() == 1 &&
            current.Select(x => x.SourceAuthority).Distinct().Count() == 3 &&
            current.Select(x => x.CapturedScopeKey).Distinct().Count() == 2,
            "same-identity offline A/B and product observations at two Sites must independently retain Current");
        output = header; await Task.Delay(20); await f.Run(scheduler, command);
        current = f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor");
        Assert(current.Count == 3 && current.All(x => !x.Source.EndsWith(other.Id)) &&
            current.Any(x => x.Source.EndsWith(first.Id)),
            "complete empty table at Site B must retire only that collector/scope, preserving identical Site A and offline identities");
        f.Catalog.PublishH3cTaskFacts(other.Id);
        Assert(f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor").Count == 3,
            "older same-Site replay cannot resurrect a retired identical relationship");
        f.Execute("UPDATE device_inventory SET site_id='local'; UPDATE collection_sources SET site_id='local' WHERE kind<>'SYSLOG_UDP'");
        output = header + "\nGE1/0/1 0011-2233-4455 GE2/0/1 SAME";
        await Task.Delay(20); var returned = await f.Run(scheduler, command);
        current = f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor");
        Assert(current.Count == 3 && current.Count(x => x.SourceAuthority == "H3C_TASK_RAW") == 1 &&
            current.Single(x => x.SourceAuthority == "H3C_TASK_RAW").Source.EndsWith(returned.Id) &&
            f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor", history: true).Count == 5,
            "new collector run replaces same-scope Current without overwriting independent same-identity source/history");
        var relations = f.Catalog.QueryH3cLldpRelations(f.Profile, false, 100, P2B5SiteScopeDecision.Global);
        Assert(relations.Count == 3 && relations.Select(x => x.RelationKey).Distinct().Count() == 3 &&
            relations.All(x => x.ChangeKind == "FIRST_SEEN"),
            "topology Current keys and histories retain independent authority boundaries");
    }

    private static async Task CompleteLldpTablesReplaceOnlyScopedCollectedCurrent()
    {
        using var f = new Fixture();
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "r71_simulator_sw_lldp_list.txt"));
        const string command = H3cCommandPolicy.DisplayLldpNeighborInformationList;
        await using var scheduler = f.Scheduler(new Factory { Execute = cmd => cmd == command ? output : Factory.Output(cmd) });
        // This offline row has a distinct identity. The table-retirement
        // operation must leave it untouched; exact same-identity partitions are
        // covered separately by SameIdentityLldpAuthoritiesAndScopesRemainIndependent.
        f.Catalog.ImportH3cOfflineReplay(f.Profile, new Dictionary<string, string>
            { [command] = "Local Interface Neighbor Sysname Neighbor Interface Management Address\nGE9/0/9 OFFLINE GE1/0/9 192.0.2.9" }, 1000);
        var first = await f.Run(scheduler, command);
        Assert(first.State == "SUCCEEDED" && f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor").Count == 4 &&
            f.Catalog.QueryH3cFacts(f.Profile, "lldp.table").Single().CollectedRawVerified,
            "real-shape complete LLDP task must publish three correct scoped neighbors and a verified table revision");
        var firstHistoryCount = f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor", history: true).Count;
        // Use a distinct identity for the other-scope row. This tests the
        // retirement filter, not same-identity isolation across scopes.
        f.Execute("INSERT INTO h3c_facts SELECT 'other-scope',profile_id,kind,'other-scope',status,source,captured_ms,1," +
            "json_set(payload_json,'$.capturedScope.siteId','other-site') FROM h3c_facts WHERE kind='lldp.neighbor' AND source LIKE 'H3C_TASK_RAW:%' LIMIT 1");
        await f.Run(scheduler, H3cCommandPolicy.DisplayVersion);
        var identity = f.Catalog.QueryH3cFacts(f.Profile, "device.identity").Single();
        foreach (var incomplete in new[] { "Local Interface Chassis ID Port ID System Name\nGE1/0/1 truncated",
            "unrecognized output", "Local Interface Chassis ID Port ID System Name\n-- More --" })
        {
            output = incomplete;
            await f.Run(scheduler, command);
            Assert(f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor").Count == 5 &&
                f.Catalog.QueryH3cFacts(f.Profile, "lldp.table").Count == 1,
                "partial/unrecognized/paged tables cannot retire Current or advance the complete table revision");
        }
        output = "Local Interface Chassis ID Port ID System Name\nGE1/0/3 1cc0-ab25-0600 GigabitEthernet1/0/0 ac2";
        await Task.Delay(20);
        var second = await f.Run(scheduler, command);
        var current = f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor");
        Assert(second.State == "SUCCEEDED" && current.Count == 3 &&
            current.Count(x => x.Source.StartsWith("H3C_TASK_RAW:") && x.Id != "other-scope") == 1 &&
            current.Any(x => x.Id == "other-scope") && current.Any(x => x.Source == Catalog.H3cOfflineFixtureSource),
            "table retirement must preserve these distinct-identity rows from another scope/source");
        Assert(f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor", history: true).Count == firstHistoryCount + 2,
            "set replacement must retain every historical observation without removals");
        var before = f.Rows();
        f.Catalog.PublishH3cTaskFacts(first.Id);
        Assert(f.Rows() == before, "late replay must not resurrect missing old neighbors or replace the newer table");
        f.Catalog.PublishH3cTaskFacts(second.Id);
        Assert(f.Rows() == before, "same-revision republication must preserve Current and history exactly");
        // Keep valid Raw whose first projection is delayed until after a newer
        // empty table. Its unseen identity must not reappear in Current.
        f.Execute("CREATE TRIGGER defer_lldp_projection BEFORE INSERT ON h3c_facts BEGIN SELECT RAISE(FAIL,'deferred synthetic projection'); END");
        output = "Local Interface Chassis ID Port ID System Name\nGE7/0/7 0011-2233-4455 GE1/0/7 LATE";
        var late = await f.Run(scheduler, command);
        f.Execute("DROP TRIGGER defer_lldp_projection");
        output = "Local Interface Chassis ID Port ID System Name";
        await Task.Delay(20);
        await f.Run(scheduler, command);
        current = f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor");
        Assert(current.Count == 2 && current.All(x => x.Id == "other-scope" || x.Source == Catalog.H3cOfflineFixtureSource) &&
            f.Catalog.QueryH3cFacts(f.Profile, "device.identity").Single().Id == identity.Id &&
            f.Catalog.QueryH3cFacts(f.Profile, "lldp.table").Single().CollectedRawVerified,
            "empty-table retirement preserves the distinct-identity other-scope/source rows, other kind and Raw history");
        before = f.Rows();
        f.Catalog.PublishH3cTaskFacts(second.Id);
        Assert(f.Rows() == before, "replaying a nonempty older table cannot overwrite complete empty Current");
        Assert(late.State == "SUCCEEDED" && f.Catalog.PublishH3cTaskFacts(late.Id).Status == "PUBLISHED" &&
            f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor").Count == 2 &&
            f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor", history: true).Single(x => x.Source.EndsWith(late.Id)).IsCurrent == false,
            "a delayed first projection must retain historical Raw evidence without resurrecting an omitted identity");
        f.Catalog.ImportH3cOfflineReplay(f.Profile, new Dictionary<string, string>
            { [command] = "Local Interface Neighbor Sysname Neighbor Interface Management Address\nGE8/0/8 OFFLINE-LATER GE1/0/8 192.0.2.8" }, 1001);
        Assert(f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor").Count == 3 &&
            f.Catalog.QueryH3cFacts(f.Profile, "lldp.neighbor").Any(x => x.IdentityKey.StartsWith("GE8/0/8|")),
            "the collected-table ordering guard must not suppress this distinct-identity offline row");
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
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var recovery = f.Catalog.RecoverRawSegments();
            Assert(recovery.FailedFiles == 0 &&
                f.Scalar($"SELECT COUNT(*) FROM task_run_command_outputs WHERE task_run_id='{runId}'") == "2" &&
                f.Scalar($"SELECT COUNT(*) FROM task_run_raw_outputs WHERE task_run_id='{runId}'") == "0" &&
                f.Catalog.QueryH3cFacts(f.Profile, history: true).All(item => item.CollectedRawVerified),
                "restart recovery must retain command-level manifests without treating their segments as orphan single-command results");
        }
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

    private static async Task VerifiedWirelessCollectionPublishesEffectiveObservation()
    {
        using var f = new Fixture();
        var definition = Guid.NewGuid().ToString("N");
        var commands = new[] { H3cCommandPolicy.DisplayVersion, H3cCommandPolicy.DisplayWlanApAll, H3cCommandPolicy.DisplayWlanApAllAddress };
        f.Catalog.CreateTaskDefinition(definition, "H3C_SSH_READONLY", f.Profile, commands[0], TimeSpan.FromSeconds(5),
            scopeSnapshot: f.Authorizer.CaptureDefinition(f.Profile, "local-api-token"), commands: commands);
        await using var scheduler = f.Scheduler(new Factory());
        Assert(scheduler.TryScheduleDefinition(definition, out var runId, "local-api-token"), "wireless task admitted");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (scheduler.Get(runId)!.State is not ("SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMED_OUT"))
            await Task.Delay(10, timeout.Token);
        var service = new WirelessReadModelService(f.Catalog);
        var resource = service.GetSnapshot(P2B5SiteScopeDecision.Global).ApResources.Single();
        var observation = resource.Observations.Single();
        var runtime = service.GetRuntimeState(resource.Id, P2B5SiteScopeDecision.Global)!;
        Assert(resource.IdentityStatus == "CONFIRMED" && observation.NormalizedState == "RUNNING_MASTER" &&
            observation.NormalizedMacSet.Count == 2 && runtime.Current?.NormalizedState == "RUNNING_MASTER",
            "coherent verified multi-command Raw must publish identity, observation and effective runtime state");
        f.Execute("UPDATE task_run_command_outputs SET sha256='REPLACED' WHERE task_run_id='" + runId + "' AND command_index=0");
        Assert(service.GetSnapshot(P2B5SiteScopeDecision.Global).ApResources.Single().Observations.Single().NormalizedState == "STALE" &&
            service.GetRuntimeState(resource.Id, P2B5SiteScopeDecision.Global)!.Current?.NormalizedState != "RUNNING_MASTER",
            "replaced command metadata must revoke verified provenance even when the segment count is unchanged");
        f.Execute("UPDATE raw_segments SET state='INCOMPLETE' WHERE task_run_id='" + runId + "'");
        var revoked = service.GetSnapshot(P2B5SiteScopeDecision.Global).ApResources.Single();
        Assert(revoked.Observations.Single().NormalizedState == "STALE", "damaged Raw must not retain effective live state");
    }

    private static async Task MissingAddressPreservesOnlyVerifiedSameRunEvidence()
    {
        using var f = new Fixture();
        var commands = new[] { H3cCommandPolicy.DisplayVersion, H3cCommandPolicy.DisplayWlanApAll, H3cCommandPolicy.DisplayWlanApAllAddress };
        var definition = Guid.NewGuid().ToString("N");
        f.Catalog.CreateTaskDefinition(definition, "H3C_SSH_READONLY", f.Profile, commands[0], TimeSpan.FromSeconds(5),
            scopeSnapshot: f.Authorizer.CaptureDefinition(f.Profile, "local-api-token"), commands: commands);
        await using var scheduler = f.Scheduler(new Factory { Execute = command => command == H3cCommandPolicy.DisplayWlanApAll
            ? "<AC>display wlan ap all\nAP-ONE 1 R/M WA6320 SERIAL-001 group-a 1 day\n<AC>" : Factory.Output(command) });
        Assert(scheduler.TryScheduleDefinition(definition, out var firstRun, "local-api-token"), "complete wireless task admitted");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (scheduler.Get(firstRun)!.State is not ("SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMED_OUT"))
            await Task.Delay(10, timeout.Token);
        Assert(scheduler.Get(firstRun)!.State == "SUCCEEDED", "complete wireless task succeeded");
        var service = new WirelessReadModelService(f.Catalog);
        var original = service.GetSnapshot(P2B5SiteScopeDecision.Global).ApResources.Single();
        await Task.Delay(30, timeout.Token);
        var later = await f.Run(scheduler, H3cCommandPolicy.DisplayWlanApAll);
        Assert(later.State == "SUCCEEDED", "incomplete wireless task succeeded");
        var snapshot = service.GetSnapshot(P2B5SiteScopeDecision.Global);
        var preserved = snapshot.ApResources.Single();
        var runtime = service.GetRuntimeState(original.Id, P2B5SiteScopeDecision.Global)!;
        Assert(preserved.Id == original.Id && preserved.IdentityStatus == "CONFIRMED" &&
            preserved.Observations.Single().SourceRevision == original.Observations.Single().SourceRevision &&
            snapshot.Warnings.Contains("AP_ADDRESS_INCOMPLETE_PREVIOUS_EVIDENCE_PRESERVED") &&
            runtime.Sources.Single().LatestOutcome == "INCOMPLETE" && runtime.Current?.NormalizedState == "RUNNING_MASTER",
            "an AP-only run preserves the previous complete verified pair and reports a source-wide incomplete attempt");
        await Task.Delay(30, timeout.Token);
        await f.Run(scheduler, H3cCommandPolicy.DisplayWlanApAllAddress);
        Assert(service.GetSnapshot(P2B5SiteScopeDecision.Global).ApResources.Single().Observations.Single().SourceRevision == original.Observations.Single().SourceRevision,
            "an address from another run must not be paired with the later AP-only run");
        f.Execute("UPDATE raw_segments SET state='INCOMPLETE' WHERE task_run_id='" + firstRun + "'");
        Assert(service.GetSnapshot(P2B5SiteScopeDecision.Global).ApResources.All(item => item.IdentityStatus != "CONFIRMED"),
            "revoked historical Raw cannot preserve a confirmed identity");
    }

    private static async Task MultiCommandRawCannotRelabelDeviceHost()
    {
        using var f = new Fixture();
        var definition = Guid.NewGuid().ToString("N");
        var first = H3cCommandPolicy.DisplayVersion;
        var second = H3cCommandPolicy.DisplayWlanApAll;
        f.Catalog.CreateTaskDefinition(definition, "H3C_SSH_READONLY", f.Profile, first, TimeSpan.FromSeconds(5),
            scopeSnapshot: f.Authorizer.CaptureDefinition(f.Profile, "local-api-token"), commands: new[] { first, second });
        await using var scheduler = f.Scheduler(new Factory());
        Assert(scheduler.TryScheduleDefinition(definition, out var runId, "local-api-token"), "multi-command relabel task admitted");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var run = scheduler.Get(runId)!;
            if (run.State is "SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMED_OUT") break;
            await Task.Delay(10, timeout.Token);
        }
        var relative = f.Scalar($"SELECT relative_path FROM task_run_command_outputs WHERE task_run_id='{runId}' AND command_index=0");
        var factsBeforeTamper = f.Catalog.QueryH3cFacts(f.Profile, history: true).Count;
        var path = Path.Combine(f.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        var raw = File.ReadAllText(path);
        Assert(raw.Contains("\"device_host\":\"192.0.2.41\"", StringComparison.Ordinal),
            "fixture Raw should contain the captured device host before tampering");
        File.WriteAllText(path, raw.Replace("\"device_host\":\"192.0.2.41\"", "\"device_host\":\"192.0.2.99\"", StringComparison.Ordinal),
            new System.Text.UTF8Encoding(false));
        var segmentHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        f.Execute($"UPDATE raw_segments SET sha256='{segmentHash}' WHERE relative_path='{relative}'; UPDATE task_run_command_outputs SET raw_segment_sha256='{segmentHash}' WHERE task_run_id='{runId}' AND command_index=0");
        var publication = f.Catalog.PublishH3cTaskFacts(runId);
        var relabelFacts = f.Catalog.QueryH3cFacts(f.Profile, history: true);
        Assert(publication.Status != "PUBLISHED" && relabelFacts.Count == factsBeforeTamper,
            $"multi-command Raw host relabel must fail closed without publishing facts (status={publication.Status}, facts={relabelFacts.Count})");
    }

    private static async Task SuccessfulMultiCommandRunRequiresCompleteManifestSet()
    {
        using var f = new Fixture();
        var definition = Guid.NewGuid().ToString("N");
        var first = H3cCommandPolicy.DisplayVersion;
        var second = H3cCommandPolicy.DisplayWlanApAll;
        f.Catalog.CreateTaskDefinition(definition, "H3C_SSH_READONLY", f.Profile, first, TimeSpan.FromSeconds(5),
            scopeSnapshot: f.Authorizer.CaptureDefinition(f.Profile, "local-api-token"), commands: new[] { first, second });
        var runId = f.Catalog.CreateTaskRun(definition);
        f.Catalog.TransitionTaskRun(runId, "CONNECTING");
        f.Catalog.TransitionTaskRun(runId, "RUNNING");
        var task = f.Catalog.GetTaskDefinition(definition)!;
        var manifest = await new H3cTaskRawResultSink(f.Catalog).PersistCommandAsync(runId, task, first,
            Factory.Output(first), "COMPLETE", 0);
        try
        {
            f.Catalog.CompleteTaskRunWithRawOutputs(runId, new[] { manifest }, "SUCCEEDED", null);
            throw new InvalidOperationException("a successful multi-command run accepted an incomplete Raw manifest set");
        }
        catch (InvalidDataException) { }
        Assert(f.Catalog.GetTaskRun(runId)!.State == "RUNNING" &&
            f.Scalar($"SELECT COUNT(*) FROM task_run_command_outputs WHERE task_run_id='{runId}'") == "0",
            "incomplete success must leave the run open and avoid committing a partial command set");
    }

    private static async Task VlanTablePublishesWholeRevisionsWithRawAndScope()
    {
        using var f = new Fixture();
        var output = H3cVlanTests.Transcript(H3cVlanTests.Block(1) + "\n" + H3cVlanTests.Block(30));
        await using var scheduler = f.Scheduler(new Factory { Execute = _ => output });
        var first = await f.Run(scheduler, H3cCommandPolicy.DisplayVlanAll);
        var fact = f.Catalog.QueryH3cFacts(f.Profile, "vlan.table").Single();
        using (var payload = JsonDocument.Parse(fact.PayloadJson))
            Assert(first.State == "SUCCEEDED" && fact.CollectedRawVerified && payload.RootElement.GetProperty("vlans").GetArrayLength() == 2 &&
                payload.RootElement.GetProperty("rawProvenance").GetProperty("runId").GetString() == first.Id,
                "a VLAN table must bind to exactly its sealed, scoped Raw task");
        var before = f.Rows();
        Assert(f.Catalog.PublishH3cTaskFacts(first.Id).Status == "PUBLISHED" && f.Rows() == before,
            "VLAN Raw republication is idempotent");

        output = H3cVlanTests.Transcript(H3cVlanTests.Block(1));
        await f.Run(scheduler, H3cCommandPolicy.DisplayVlanAll);
        fact = f.Catalog.QueryH3cFacts(f.Profile, "vlan.table").Single();
        using (var payload = JsonDocument.Parse(fact.PayloadJson))
            Assert(payload.RootElement.GetProperty("vlans").GetArrayLength() == 1 &&
                f.Catalog.QueryH3cFacts(f.Profile, "vlan.table", history: true).Count == 2,
                "removed VLANs disappear from the whole current table while prior tables remain immutable history");

        output = H3cVlanTests.Transcript(H3cVlanTests.Block(1) + "\n---- More ----");
        await f.Run(scheduler, H3cCommandPolicy.DisplayVlanAll);
        fact = new Catalog(f.Settings).QueryH3cFacts(f.Profile, "vlan.table").Single();
        using (var payload = JsonDocument.Parse(fact.PayloadJson))
            Assert(fact.Status == "PARTIAL" && fact.DataState == "PARTIAL" && fact.CollectedRawVerified &&
                payload.RootElement.GetProperty("vlans").GetArrayLength() == 0,
                "complete Raw with incomplete parsing stays partial after restart and never fabricates current membership");
        var other = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "other" }, "other-claims");
        Assert(f.Catalog.QueryH3cFacts(f.Profile, "vlan.table", history: true, scope: other).Count == 0 &&
            f.Catalog.QueryH3cFacts(f.Profile, "vlan.table", history: true).Count == 3,
            "VLAN revisions use captured Site scope including partial state");
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
            H3cCommandPolicy.DisplayWlanApAllAddress => "<AC>display wlan ap all address\nAP-ONE 192.0.2.42 4c6f-d608-0400\nAP-ONE 192.0.2.43 4c6f-d608-0401\n<AC>",
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
