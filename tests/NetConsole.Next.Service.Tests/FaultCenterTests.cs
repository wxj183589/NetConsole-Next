using System.Text.Json;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetConsole.Next.Service;

internal static class FaultCenterTests
{
    private const long Now = 2_000_000;
    public static async Task RunAsync()
    {
        AggregatesAndDeduplicates();
        ReopensOnlyNewFaultEvidence();
        RecoveryRequiresExactFreshVerifiedResource();
        RecoveryAndAggregationKeepSourceScope();
        LldpRecoveryRequiresExactCompleteNewTable();
        ApRecoveryRequiresLiveRoleAndAllSources();
        HistoryIsBounded();
        PersistenceSurvivesRestart();
        CorruptionIsFailClosedAndPreserved();
        InterruptedAdmissionIsNotReplayed();
        FailedCommitDoesNotAdvanceAuthority();
        ScopedLifecycleAndPolicyConsent();
        ProcessingAndReopenedPersistWithRoleGuards();
        SummaryUsesFilteredScopeBeforePagination();
        await BackgroundRecoveryIsFairAndDoesNotWriteBlockedHistory();
        await UnrelatedTaskSuccessCannotResolveCollectionFailure();
        await DistinctCollectionFailuresRemainIndependent();
        RecollectionRetriesOnlyKnownTransientFailures();
        CollectionRecoveryRequiresSameCommandPlan();
        await FutureDisabledAndExhaustedPoliciesDoNotConnect();
        await AuthenticationStopsAutomaticPolicy();
        await ChangedEndpointBlocksAutomaticPolicy();
        await RecollectionRevocationStopsPolicy();
        Console.WriteLine("FAULT_CENTER_TESTS=23/23 PASS");
    }
    private static void LldpRecoveryRequiresExactCompleteNewTable()
    {
        var item = FaultLifecycle.Observe(null, Event() with { Kind = "LLDP_NEIGHBOR_LOST", ResourceKey = "GE1|GE2|AA" }, "profile-a", "actor", Now);
        var table = new H3cFactSnapshot("restored", "profile-a", "lldp.table", "table", "STALE", "H3C_SSH_READONLY", Now + 1, true,
            "{\"status\":\"PARSED\",\"neighbors\":[{\"localInterface\":\"GE1\",\"neighborInterface\":\"GE2\",\"chassisId\":\"AA\"}]}")
            { CollectedRawVerified = true, CapturedScopeKey = "scope-a" };
        Assert(FaultLifecycle.FreshRecoveryFact(item, [table], Now + 2)?.Id == "restored", "exact returned neighbor in fresh complete table verifies recovery");
        foreach (var rejected in new[] { table with { CapturedMs = Now }, table with { CapturedScopeKey = "scope-b" },
            table with { Source = "OTHER" }, table with { ProfileId = "other" }, table with { CollectedRawVerified = false },
            table with { PayloadJson = "{\"status\":\"PARSED\",\"neighbors\":[]}" }, table with { Status = "PARTIAL" } })
            Assert(FaultLifecycle.FreshRecoveryFact(item, [rejected], Now + 2) is null, "wrong identity, scope, authority, age or incomplete table cannot resolve loss");
        var resolved = item with { State = "RESOLVED", History = FaultLifecycle.Append(item, new(Now + 2, "RECOVERY_VERIFIED", "actor", table.Id, "restored")) };
        var recurring = FaultLifecycle.Observe(resolved, Event("table-again", Now + 3) with { Kind = "LLDP_NEIGHBOR_LOST", ResourceKey = item.ResourceKey }, "profile-a", "actor", Now + 4);
        Assert(recurring.State == "REOPENED" && recurring.Occurrences == 2, "later exact neighbor loss reopens the same incident");
    }
    private static void CollectionRecoveryRequiresSameCommandPlan()
    {
        var scope = new TaskScopeSnapshot(1, "actor", "device-a", "profile-a", "site-a", Now, "GLOBAL", new("192.0.2.10", 22));
        var failed = new TaskDefinitionSnapshot("failed", "H3C_READONLY", "profile-a", "display lldp neighbor-information list",
            30000, true, "NONE", scope, ["display lldp neighbor-information list", "display lldp neighbor-information verbose"]);
        var retry = failed with { Id = "retry", ScopeSnapshot = scope with { CreatedMs = Now + 1 } };
        Assert(FaultLifecycle.SameCollection(failed, retry), "new definition of the same frozen collection is valid");
        Assert(!FaultLifecycle.SameCollection(failed, retry with { Commands = ["display version"] }),
            "version success cannot resolve a failed LLDP collection");
        Assert(FaultLifecycle.CollectionResource(failed) == FaultLifecycle.CollectionResource(retry) &&
            FaultLifecycle.CollectionResource(failed) != FaultLifecycle.CollectionResource(retry with { Commands = ["display version"] }) &&
            FaultLifecycle.CollectionResource(failed) != FaultLifecycle.CollectionResource(retry with { ScopeSnapshot = scope with { SiteId = "other" } }) &&
            FaultLifecycle.CollectionResource(failed) != FaultLifecycle.CollectionResource(retry with { ScopeSnapshot = scope with { Endpoint = new("192.0.2.11", 22) } }),
            "stable resource ignores admission ID/time while preserving the complete plan and frozen target");
        Assert(!FaultLifecycle.SameCollection(failed, retry with { Commands = failed.CommandPlan.Reverse().ToArray() }),
            "command order is part of the collection plan");
        Assert(!FaultLifecycle.SameCollection(failed, retry with { TaskType = "OTHER" }) &&
            !FaultLifecycle.SameCollection(failed, retry with { ScopeSnapshot = scope with { SiteId = "other-site" } }) &&
            !FaultLifecycle.SameCollection(failed, retry with { ScopeSnapshot = scope with { Endpoint = new("192.0.2.11", 22) } }) &&
            !FaultLifecycle.SameCollection(null, retry), "missing or changed collection identity cannot verify recovery");
    }
    private static OperationalFaultEvent Event(string evidence = "fact-a", long at = Now, string resource = "GE1/0/1") =>
        new(evidence, "device-a", "site-a", "INTERFACE_DOWN", "WARNING", "OBSERVED", at, "FACT", evidence, "DOWN", resource,
            "H3C_SSH_READONLY", "scope-a");
    private static FaultIncident Incident() => FaultLifecycle.Observe(null, Event(), "profile-a", "actor", Now);
    private static void AggregatesAndDeduplicates()
    {
        var first = Incident();
        var same = FaultLifecycle.Observe(first, Event(), "profile-a", "actor", Now + 1);
        Assert(ReferenceEquals(first, same), "refreshing the same evidence cannot create history or occurrences");
        var next = FaultLifecycle.Observe(first, Event("fact-b", Now + 2), "profile-a", "actor", Now + 3);
        Assert(first.Id == next.Id && next.Occurrences == 2 && next.History.Count == 2, "evidence ids must aggregate by stable resource");
        Assert(FaultLifecycle.Fingerprint(Event(resource: "GE1/0/2"), "profile-a") != first.Id &&
            FaultLifecycle.Fingerprint(Event() with { SiteId = "site-b" }, "profile-a") != first.Id &&
            FaultLifecycle.Fingerprint(Event(), "profile-b") != first.Id, "resource, Site and profile identities must remain separate");
    }
    private static void ReopensOnlyNewFaultEvidence()
    {
        var first = Incident();
        var resolved = first with { State = "RESOLVED", History = FaultLifecycle.Append(first,
            new(Now + 100, "RECOVERY_VERIFIED", "actor", "up", "verified")) };
        Assert(ReferenceEquals(resolved, FaultLifecycle.Observe(resolved, Event("old", Now + 50), "profile-a", "actor", Now + 200)),
            "late history from before verified recovery cannot reopen a fault");
        var reopened = FaultLifecycle.Observe(resolved, Event("new", Now + 200), "profile-a", "actor", Now + 201);
        Assert(reopened.State == "REOPENED" && reopened.History.Last().Action == "REOPENED", "new resource failure must reopen resolved incident");
    }
    private static void RecoveryRequiresExactFreshVerifiedResource()
    {
        var item = Incident();
        H3cFactSnapshot Fact(string id, long at, string key = "GE1/0/1", bool verified = true, string link = "UP", string status = "STALE") =>
            new(id, "profile-a", "interface", key, status, "H3C_SSH_READONLY", at, true, JsonSerializer.Serialize(new { linkStatus = link })) { CollectedRawVerified = verified, CapturedScopeKey = "scope-a" };
        Assert(FaultLifecycle.FreshRecoveryFact(item, [Fact("wrong", Now + 1, "GE1/0/2"), Fact("fixture", Now + 1, verified: false),
            Fact("future", Now + 200), Fact("old", Now), Fact("partial", Now + 1, status: "PARTIAL"), Fact("down", Now + 1, link: "DOWN")], Now + 100) is null,
            "wrong interface, fixture, future, old, partial and unchanged faults cannot prove recovery");
        Assert(FaultLifecycle.FreshRecoveryFact(item, [Fact("up", Now + 1)], Now + 100)?.Id == "up", "Raw verified newer same interface UP is accepted even with legacy STALE label");
        Assert(FaultLifecycle.FreshRecoveryFact(item, [Fact("stale-up", Now + 1)], Now + 400000) is null, "expired observations cannot prove current recovery");
    }
    private static void RecoveryAndAggregationKeepSourceScope()
    {
        var item = Incident();
        var fact = new H3cFactSnapshot("up", "profile-a", "interface", "GE1/0/1", "STALE", "H3C_SSH_READONLY", Now + 1,
            true, "{\"linkStatus\":\"UP\"}") { CollectedRawVerified = true, CapturedScopeKey = "scope-a" };
        foreach (var wrong in new[] { fact with { Source = "OTHER_AUTHORITY" }, fact with { CapturedScopeKey = "scope-b" },
            fact with { CapturedScopeKey = "UNSCOPED" }, fact with { ProfileId = "other-profile" }, fact with { IsCurrent = false } })
            Assert(FaultLifecycle.FreshRecoveryFact(item, [wrong], Now + 2) is null, "other source/scope/profile or historical evidence cannot resolve incident");
        Assert(FaultLifecycle.FreshRecoveryFact(item with { SourceAuthority = null, CapturedScopeKey = null }, [fact], Now + 2) is null,
            "legacy incidents with unknown source/scope require new scoped observations");
        Assert(FaultLifecycle.Fingerprint(Event() with { SourceAuthority = "OTHER_AUTHORITY" }, "profile-a") != item.Id &&
            FaultLifecycle.Fingerprint(Event() with { CapturedScopeKey = "scope-b" }, "profile-a") != item.Id,
            "same interface failures from independent source or frozen scope cannot aggregate");
        Assert(item.SourceAuthority == fact.SourceAuthority && item.CapturedScopeKey == fact.CapturedScopeKey,
            "durable incident retains exact source and captured scope provenance");
    }
    private static void HistoryIsBounded()
    {
        var item = Incident();
        for (var n = 1; n < 80; n++) item = FaultLifecycle.Observe(item, Event("fact-" + n, Now + n), "profile-a", "actor", Now + n);
        Assert(item.Occurrences == 80 && item.History.Count == 50 && item.History.Last().EvidenceId == "fact-79", "retain latest 50 effective transitions, not refresh samples");
    }
    private static void ApRecoveryRequiresLiveRoleAndAllSources()
    {
        var item = Incident() with { Kind = "AP_OFFLINE", ResourceKey = "ap-a" };
        var current = new WirelessEffectiveState("RUNNING_MASTER", "R/M", Now + 1, "new", "raw", "parser", "LIVE_VERIFIED");
        var source = new WirelessSourceRuntimeState("device-a", "site-a", "RUNNING_MASTER", current, null, [], "SUCCESS", Now + 1, Now + 300001, "RECENT10_COMPLETE");
        var view = new WirelessRuntimeStateView("ap-a", "RUNNING_MASTER", [source], new("FULL", "fixture"), Now + 2, "new", 300000, []);
        Assert(FaultLifecycle.FreshApRecoveryEvidence(item, view, Now + 2) == "new", "new live master observation is a qualifying AP recovery");

        var backup = new WirelessEffectiveState("RUNNING_BACKUP", "R/B", Now + 1, "backup", "raw-backup", "parser", "LIVE_VERIFIED");
        var backupSource = source with { NormalizedState = "RUNNING_BACKUP", Current = backup };
        var backupView = view with { NormalizedState = "RUNNING_BACKUP", Sources = [backupSource], SourceRevision = "backup" };
        Assert(FaultLifecycle.FreshApRecoveryEvidence(item, backupView, Now + 2) == "backup",
            "a fresh verified R/B observation proves the AP is running on the backup AC");
        Assert(FaultLifecycle.FreshApRecoveryEvidence(item with { Kind = "AP_CONFLICT" }, backupView, Now + 2) is null,
            "backup-only evidence does not resolve a conflicting multi-source AP state");

        foreach (var rejected in new[] { view with { NormalizedState = "OFFLINE" }, view with { ResourceId = "other-ap" },
            view with { Sources = [source with { Current = current with { VerificationLevel = "OFFLINE_FIXTURE" } }] },
            view with { Sources = [source with { LatestOutcome = "PARTIAL" }] },
            view with { Sources = [source, source with { SourceAcDeviceId = "other", Current = current with { CapturedMs = Now - 1 } }] } })
            Assert(FaultLifecycle.FreshApRecoveryEvidence(item, rejected, Now + 2) is null,
                "offline, other AP, fixture, incomplete or old sources cannot confirm recovery");
    }
    private static void PersistenceSurvivesRestart()
    {
        using var f = new Fixture();
        var store = new FaultCenterStore(f.Root);
        store.Commit(new(1, [Incident()], []));
        var restored = new FaultCenterStore(f.Root);
        Assert(!restored.Unavailable && restored.Current.Incidents.Single().EvidenceId == "fact-a" && restored.Current.Policies.Count == 0,
            "incident and history must survive restart with automation disabled by default");
    }
    private static void CorruptionIsFailClosedAndPreserved()
    {
        using var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.Path)!);
        foreach (var corrupt in new[] { "{ broken", "{\"version\":1,\"incidents\":[null],\"policies\":[]}",
            "{\"version\":1,\"incidents\":[],\"policies\":[null]}" })
        {
            File.WriteAllText(f.Path, corrupt);
            var store = new FaultCenterStore(f.Root);
            Assert(store.Unavailable && File.ReadAllText(f.Path) == corrupt, "damaged optional store must be preserved and disabled");
            try { store.Commit(new(1, [], [])); throw new Exception("accepted corrupt store"); }
            catch (InvalidOperationException ex) { Assert(ex.Message == "FAULT_STORE_UNAVAILABLE", "closed storage error"); }
        }
    }
    private static void InterruptedAdmissionIsNotReplayed()
    {
        using var f = new Fixture();
        var store = new FaultCenterStore(f.Root);
        var target = new TaskScopeSnapshot(1, "actor", "device-a", "profile-a", "site-a", Now, "GLOBAL", new("192.0.2.10", 22));
        store.Commit(new(1, [Incident()], [new("device-a", Incident().Id, target, true, 5, 3, 1, Now + 300000, "RUNNING", null, null, null)]));
        var restored = new FaultCenterStore(f.Root).Current.Policies.Single();
        Assert(!restored.Enabled && restored.NextRunMs is null && restored.State == "INTERRUPTED" && restored.Attempts == 1,
            "restart must preserve consumed attempt and require review instead of duplicating network admission");
    }
    private static void FailedCommitDoesNotAdvanceAuthority()
    {
        using var f = new Fixture();
        var store = new FaultCenterStore(f.Root); store.Commit(new(1, [Incident()], []));
        File.Delete(f.Path); Directory.CreateDirectory(f.Path);
        try { store.Commit(new(1, [], [])); throw new Exception("write succeeded unexpectedly"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Assert(store.Current.Incidents.Count == 1, "failed durable commit must leave the previous in-memory authority intact");
    }
    private static void ScopedLifecycleAndPolicyConsent()
    {
        using var f = new BusinessFixture();
        var item = f.Item;
        using var service = f.Service();
        var hidden = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "not-the-site" }, "fixture");
        Assert(service.Query(f.Actor, hidden).Total == 0, "history must respect current Site scope");
        try { service.Acknowledge(item.Id, f.Actor, hidden); throw new Exception("hidden mutation accepted"); }
        catch (KeyNotFoundException) { }
        Assert(service.Acknowledge(item.Id, f.Actor, P2B5SiteScopeDecision.Global).State == "ACKNOWLEDGED", "acknowledgement is durable without resolving");
        Assert(service.Verify(item.Id, f.Actor, P2B5SiteScopeDecision.Global).State == "RECOVERY_NOT_VERIFIED", "manual acknowledgement and absent faults cannot prove recovery");
        var policy = service.SetPolicy(item.DeviceId, new(item.Id, item.ProfileId, true), f.Actor, P2B5SiteScopeDecision.Global);
        Assert(policy.NextRunMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 290000 && policy.Attempts == 0 && f.Factory.Calls == 0,
            "explicit automation waits a complete interval without connecting at creation");
        foreach (var budget in new[] { 0, 4 })
        {
            try { service.SetPolicy(item.DeviceId, new(item.Id, item.ProfileId, true, 5, budget), f.Actor, P2B5SiteScopeDecision.Global); throw new Exception("invalid budget accepted"); }
            catch (ArgumentException) { }
        }
        f.Execute("UPDATE web_users SET role='ADMIN' WHERE id=$id", f.Actor.UserId);
        try { service.Acknowledge(item.Id, f.Actor, P2B5SiteScopeDecision.Global); throw new Exception("revoked role accepted"); }
        catch (TaskScopeRevokedException) { }
    }
    private static void ProcessingAndReopenedPersistWithRoleGuards()
    {
        using var f = new BusinessFixture();
        var reopened = f.Item with { State = "REOPENED" };
        f.SeedIncidents([reopened]);
        using (var service = f.Service())
        {
            var hidden = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "hidden" }, "test");
            try { service.StartProcessing(reopened.Id, f.Actor, hidden); throw new Exception("hidden processing accepted"); }
            catch (KeyNotFoundException) { }
            Assert(service.Acknowledge(reopened.Id, f.Actor, P2B5SiteScopeDecision.Global).State == "ACKNOWLEDGED", "reopened incidents can be acknowledged");
            var processing = service.StartProcessing(reopened.Id, f.Actor, P2B5SiteScopeDecision.Global);
            Assert(processing.State == "PROCESSING" && processing.History.Last().Action == "PROCESSING", "processing is a durable lifecycle transition");
            Assert(service.StartProcessing(reopened.Id, f.Actor, P2B5SiteScopeDecision.Global).History.Count == processing.History.Count,
                "repeated process action cannot add refresh history");
        }
        using var restarted = f.Service();
        Assert(restarted.Query(f.Actor, P2B5SiteScopeDecision.Global).Items.Single().State == "PROCESSING", "processing survives Service reconstruction");
        f.Execute("UPDATE web_users SET role='ADMIN' WHERE id=$id", f.Actor.UserId);
        try { restarted.StartProcessing(reopened.Id, f.Actor, P2B5SiteScopeDecision.Global); throw new Exception("reader processing accepted"); }
        catch (TaskScopeRevokedException) { }
        Assert(f.Factory.Calls == 0, "lifecycle transitions never connect to a device");
    }
    private static void SummaryUsesFilteredScopeBeforePagination()
    {
        using var f = new BusinessFixture();
        var items = Enumerable.Range(0, 31).Select(n => f.Item with { Id = "fault-" + n, State = n < 28 ? "PROCESSING" : "REOPENED" }).ToArray();
        f.SeedIncidents(items.Append(f.Item with { Id = "hidden", SiteId = "other-site" }).ToArray());
        using var service = f.Service();
        var scope = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { f.Item.SiteId }, "test");
        var page = service.Query(f.Actor, scope, state: "PROCESSING");
        Assert(page.Total == 28 && page.Items.Count == 25 && page.Summary is { Processing: 28, Reopened: 0, AffectedDevices: 1 },
            "summary uses all filtered authorized incidents before page slicing");
        var empty = service.Query(f.Actor, scope, deviceId: "unknown");
        Assert(empty.Total == 0 && empty.Summary is { Open: 0, Processing: 0, Reopened: 0, AffectedDevices: 0 }, "empty filtered scope has zero summary");
    }
    private static async Task BackgroundRecoveryIsFairAndDoesNotWriteBlockedHistory()
    {
        using var f = new BusinessFixture();
        var blocked = Enumerable.Range(0, 30).Select(n => f.Item with { Id = "interface-" + n, ResourceKey = "GE1/0/" + (n + 1) }).ToArray();
        var collection = f.Item with { Id = "zz-collection", Kind = "COLLECTION_FAILURE", EvidenceType = "TASK_RUN", EvidenceId = f.FailedCollection() };
        f.SeedIncidents(blocked.Append(collection).ToArray());
        await f.CompleteCollection();
        using var service = f.Service();
        service.VerifyObservedDevices(f.Actor, P2B5SiteScopeDecision.Global, [f.Item.DeviceId]);
        var first = service.Query(f.Actor, P2B5SiteScopeDecision.Global);
        Assert(first.Summary!.Resolved == 0, "a bounded first sweep does not inspect more than 25 resources");
        service.VerifyObservedDevices(f.Actor, P2B5SiteScopeDecision.Global, [f.Item.DeviceId]);
        var second = service.Query(f.Actor, P2B5SiteScopeDecision.Global, kind: "COLLECTION_FAILURE").Items.Single();
        Assert(second.State == "RESOLVED" && second.History.Last().Action == "RECOVERY_VERIFIED", "later resources cannot starve behind blocked interfaces");
        var interfaces = service.Query(f.Actor, P2B5SiteScopeDecision.Global, kind: "INTERFACE_DOWN");
        Assert(interfaces.Summary!.Open == 30 && interfaces.Items.All(i => i.History.Count == 1),
            "collection success cannot recover interface faults or append repetitive blocked history");
        Assert(f.Factory.Calls == 0, "automatic verification only inspects local evidence");
    }
    private static async Task DistinctCollectionFailuresRemainIndependent()
    {
        using var f = new BusinessFixture();
        _ = f.FailedCollection(H3cCommandPolicy.DisplayVersion);
        _ = f.FailedCollection(H3cCommandPolicy.DisplayLldpNeighborInformationList);
        var legacy = f.Item with { Id = "legacy-device-collection", Kind = "COLLECTION_FAILURE", ResourceKey = f.Item.DeviceId, EvidenceType = "TASK_RUN", EvidenceId = "old-unavailable-run" };
        f.SeedIncidents([legacy]);
        using var service = f.Service();
        service.Sync(f.Actor, P2B5SiteScopeDecision.Global, 1);
        var initial = service.Query(f.Actor, P2B5SiteScopeDecision.Global, kind: "COLLECTION_FAILURE").Items;
        Assert(initial.Count == 3 && initial.Select(x => x.ResourceKey).Distinct().Count() == 3 &&
            JsonSerializer.Serialize(initial.Single(x => x.Id == legacy.Id)) == JsonSerializer.Serialize(legacy), "normal sync splits complete plans and preserves old device-aggregated history unchanged");
        await Task.Delay(20);
        await f.CompleteCollection();
        var outcomes = initial.Where(x => x.Id != legacy.Id).Select(x => service.Verify(x.Id, f.Actor, P2B5SiteScopeDecision.Global)).ToArray();
        Assert(outcomes.Count(x => x.State == "RECOVERY_VERIFIED") == 1 && outcomes.Count(x => x.State == "RECOVERY_NOT_VERIFIED") == 1,
            "version-only successful Raw resolves one collection plan while the other remains open");
        Assert(JsonSerializer.Serialize(service.Query(f.Actor, P2B5SiteScopeDecision.Global, kind: "COLLECTION_FAILURE").Items.Single(x => x.Id == legacy.Id)) == JsonSerializer.Serialize(legacy),
            "new plan verification cannot silently resolve or merge old ambiguous device incidents");
        service.Sync(f.Actor, P2B5SiteScopeDecision.Global, 1);
        Assert(service.Query(f.Actor, P2B5SiteScopeDecision.Global, kind: "COLLECTION_FAILURE").Items.Count == 3,
            "re-reading the same failed runs cannot create more plan incidents");
    }
    private static async Task UnrelatedTaskSuccessCannotResolveCollectionFailure()
    {
        using var f = new BusinessFixture();
        var collection = f.Item with { Kind = "COLLECTION_FAILURE", EvidenceType = "TASK_RUN",
            EvidenceId = f.FailedCollection("display lldp neighbor-information list") };
        f.SeedIncidents([collection]);
        await f.CompleteCollection();
        using var service = f.Service();
        var result = service.Verify(collection.Id, f.Actor, P2B5SiteScopeDecision.Global);
        Assert(result.State == "RECOVERY_NOT_VERIFIED" && result.Incident.State == "OPEN" &&
            result.Incident.History.All(h => h.Action != "RECOVERY_VERIFIED"),
            "successful version task cannot resolve the same device's failed LLDP collection through the service entry point");
    }
    private static void RecollectionRetriesOnlyKnownTransientFailures()
    {
        TaskPlanSnapshot Plan(string error, string state = "FAILED") => new("plan", "H3C_SSH_READONLY", state,
            "actor", [H3cCommandPolicy.DisplayVersion], "GLOBAL", Now, Now,
            [new(0, "profile", "definition", "run", "FAILED", error)]);
        foreach (var error in new[] { nameof(H3cConnectionTimeoutException), nameof(H3cCommandTimeoutException), nameof(H3cConnectionLostException) })
            Assert(FaultLifecycle.CanRetryRecollection(Plan(error)), "only explicitly known transient collection failures permit another policy round");
        foreach (var error in new[] { nameof(H3cAuthenticationFailedException), nameof(H3cCredentialUnavailableException), "CANCELLED", "TASK_SCOPE_DENIED", "IOException", "" })
            Assert(!FaultLifecycle.CanRetryRecollection(Plan(error)), "authentication, cancellation, scope and unknown failure must require review");
        Assert(!FaultLifecycle.CanRetryRecollection(null) && !FaultLifecycle.CanRetryRecollection(Plan(nameof(H3cConnectionLostException), "CANCELLED")), "missing and cancelled plans cannot retry");
    }
    private static async Task FutureDisabledAndExhaustedPoliciesDoNotConnect()
    {
        foreach (var mode in new[] { "future", "disabled", "exhausted" })
        {
            using var f = new BusinessFixture();
            var policy = f.Policy with { Enabled = mode != "disabled", Attempts = mode == "exhausted" ? 3 : 0,
                NextRunMs = mode == "disabled" ? null : mode == "future" ? long.MaxValue : 1 };
            f.Seed(policy); using var service = f.Service();
            await service.AdvanceAsync(policy, CancellationToken.None);
            Assert(f.Factory.Calls == 0, "future, disabled and exhausted policies cannot connect");
            if (mode == "exhausted") Assert(!service.Query(f.Actor, P2B5SiteScopeDecision.Global).Policies.Single().Enabled, "exhausted budget must disable future admissions");
        }
    }
    private static async Task AuthenticationStopsAutomaticPolicy()
    {
        using var f = new BusinessFixture(); f.Seed(f.Policy); using var service = f.Service();
        await service.AdvanceAsync(f.Policy, CancellationToken.None);
        var after = service.Query(f.Actor, P2B5SiteScopeDecision.Global).Policies.Single();
        Assert(f.Factory.Calls == 1 && !after.Enabled && after.State == "STOPPED" && after.Attempts == 1 && after.LastError == "AUTHENTICATION_FAILED",
            "a failed authentication must stop all future policy rounds and retain failure evidence");
        Assert(service.Query(f.Actor, P2B5SiteScopeDecision.Global).Items.Single().State == "OPEN", "login failure is not offline or business recovery");
    }
    private static async Task ChangedEndpointBlocksAutomaticPolicy()
    {
        using var f = new BusinessFixture(); f.Seed(f.Policy);
        f.Execute("UPDATE h3c_device_profiles SET host='192.0.2.99' WHERE id=$id", f.Item.ProfileId);
        using var service = f.Service(); await service.AdvanceAsync(f.Policy, CancellationToken.None);
        var after = service.Query(f.Actor, P2B5SiteScopeDecision.Global).Policies.Single();
        Assert(f.Factory.Calls == 0 && !after.Enabled && after.State == "BLOCKED" && after.LastError!.Contains("ENDPOINT_REVOKED", StringComparison.Ordinal),
            "an endpoint change after consent must block before any connection attempt");
    }
    private static async Task RecollectionRevocationStopsPolicy()
    {
        foreach (var mode in new[] { "actor", "endpoint" })
        {
            using var f = new BusinessFixture();
            var policy = f.Policy with { State = "AWAITING_RECOLLECTION", Attempts = 1 };
            f.Seed(policy);
            if (mode == "actor") f.Execute("UPDATE web_users SET disabled=1 WHERE id=$id", f.Actor.UserId);
            else f.Execute("UPDATE h3c_device_profiles SET host='192.0.2.99' WHERE id=$id", f.Item.ProfileId);
            using var service = f.Service(); await service.AdvanceAsync(policy, CancellationToken.None);
            var after = f.StoredPolicy();
            Assert(f.Factory.Calls == 0 && !after.Enabled && after.State == "BLOCKED" && after.NextRunMs is null && after.LastError is not null,
                "revoked owner or endpoint during recollection must persist BLOCKED without another connection");
        }
    }
    private sealed class BusinessFixture : IDisposable
    {
        private readonly Fixture _files = new();
        private readonly RuntimeSettings _settings;
        private readonly Catalog _catalog;
        private readonly P2B5SiteScopeStore _scopes;
        private readonly TaskSiteScopeAuthorizer _authorizer;
        private readonly BoundedCollectionScheduler _scheduler;
        private readonly NetworkDiagnosticsService _diagnostics;
        public WebUserIdentity Actor { get; }
        public FaultIncident Item { get; }
        public FaultPolicy Policy { get; }
        public RejectingFactory Factory { get; } = new();
        public BusinessFixture()
        {
            _settings = new() { DataRoot = _files.Root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 1048576 };
            _catalog = new(_settings); _catalog.Initialize();
            Actor = _catalog.QueryFaultMonitorActor()!;
            var profile = Guid.NewGuid().ToString("N");
            _catalog.CreateH3cDeviceProfile(new(profile, "192.0.2.10", 22, "fixture", "FAULT_TEST", "", true, 0, 0, TerminalProtocol: "SSH", Password: "fixture-only"));
            _scopes = new(_settings); _authorizer = new(_catalog, _scopes);
            var target = _authorizer.CaptureDefinition(profile, Actor.UserId);
            Item = FaultLifecycle.Observe(null, Event() with { DeviceId = target.DeviceId, SiteId = target.SiteId }, profile, Actor.UserId, Now);
            Policy = new(Item.DeviceId, Item.Id, target, true, 5, 3, 0, 1, "SCHEDULED", null, null, null);
            _scheduler = new(Factory, 1, 4, _catalog, scopeAuthorizer: _authorizer);
            _diagnostics = new(_catalog); Seed(null);
        }
        public void Seed(FaultPolicy? policy) => new FaultCenterStore(_files.Root).Commit(new(1, [Item], policy is null ? [] : [policy]));
        public void SeedIncidents(IReadOnlyList<FaultIncident> incidents) => new FaultCenterStore(_files.Root).Commit(new(1, incidents, []));
        public string FailedCollection(string command = H3cCommandPolicy.DisplayVersion)
        {
            var definition = "failed-collection-" + Guid.NewGuid().ToString("N");
            _catalog.CreateTaskDefinition(definition, "H3C_SSH_READONLY", Item.ProfileId, command,
                TimeSpan.FromSeconds(30), scopeSnapshot: Policy.Target);
            var run = _catalog.CreateTaskRun(definition);
            _catalog.TransitionTaskRun(run, "CONNECTING");
            _catalog.TransitionTaskRun(run, "FAILED", "CONNECTION_TIMEOUT");
            return run;
        }
        public async Task CompleteCollection()
        {
            var definition = "verified-collection";
            _catalog.CreateTaskDefinition(definition, "H3C_SSH_READONLY", Item.ProfileId, H3cCommandPolicy.DisplayVersion,
                TimeSpan.FromSeconds(30), scopeSnapshot: Policy.Target);
            var run = _catalog.CreateTaskRun(definition);
            // Sealed fixture Raw exercises the normal completion contract; it is
            // lifecycle evidence selection, not real device interoperability.
            _catalog.TransitionTaskRun(run, "CONNECTING");
            _catalog.TransitionTaskRun(run, "RUNNING");
            var manifest = await new H3cTaskRawResultSink(_catalog).PersistCommandAsync(run,
                _catalog.GetTaskDefinition(definition)!, H3cCommandPolicy.DisplayVersion,
                "H3C Comware Software, Version 7.1.070, Release 3506\r\nH3C S5130 uptime is 1 day", "COMPLETE", 0);
            _catalog.CompleteTaskRunWithRawOutput(run, manifest, "SUCCEEDED", null);
        }
        public FaultPolicy StoredPolicy() => new FaultCenterStore(_files.Root).Current.Policies.Single();
        public FaultCenterService Service()
        {
            var wireless = new WirelessReadModelService(_catalog);
            var health = new OperationalHealthService(_catalog, wireless, _diagnostics);
            var recovery = new OperationalRecoveryService(_catalog, _authorizer, _scopes, new(_catalog, Factory), new(_catalog, _authorizer, _scopes, _scheduler));
            return new(_catalog, health, recovery, wireless, _scopes, _authorizer, _settings, NullLogger<FaultCenterService>.Instance);
        }
        public void Execute(string sql, string id)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(_files.Root, "catalog.db"), Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
        }
        public void Dispose() { _diagnostics.Dispose(); _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult(); SqliteConnection.ClearAllPools(); _files.Dispose(); }
    }
    private sealed class RejectingFactory : IDeviceSessionFactory
    {
        public int Calls;
        public IDeviceSession Create(string target) { Calls++; throw new H3cAuthenticationFailedException(); }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string Path => System.IO.Path.Combine(Root, "operations", "fault-center.json");
        public Fixture()
        {
            var repository = new DirectoryInfo(Environment.CurrentDirectory);
            while (!File.Exists(System.IO.Path.Combine(repository.FullName, "tools", "workspace_paths.py")))
                repository = repository.Parent ?? throw new InvalidOperationException("Repository root unavailable.");
            Root = System.IO.Path.Combine(repository.Parent!.FullName, "test-data", "fault-center-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public void Dispose()
        {
            var absolute = System.IO.Path.GetFullPath(Root);
            var parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, "..")) + System.IO.Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(absolute).StartsWith("fault-center-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe fixture cleanup.");
            Directory.Delete(absolute, true);
        }
    }
    private static void Assert(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
}
