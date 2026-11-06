using NetConsole.Next.Service;
using Microsoft.Data.Sqlite;

internal static class WirelessTests
{
    public static void Run()
    {
        NetConsole.Next.Service.Tests.WirelessGroupTests.Run();
        ProjectsSamePhysicalApAcrossDualAcWithoutDuplicateIdentity();
        PreservesSerialMacConflictWithoutAutoMerge();
        MergesPartialExactEvidenceWithoutUsingNames();
        DoesNotUseApNameAsIdentity();
        KeepsSameNamedGroupsAcScoped();
        RejectsMalformedMacEvidence();
        RejectsMismatchedSourceSite();
        CollectionFailureIsNotOffline();
        RepeatedObservationIsIdempotent();
        KeepsIdentityWhenNameMissing();
        RejectsAmbiguousTopologyEvidence();
        CatalogPaginationAndProtectedReads();
        MissingEvidenceIsNotContradiction();
        SameRevisionStateConflictIsPreserved();
        ReportsMultiSourceMasterWithoutTopologyInference();
        Console.WriteLine("WIRELESS_PROJECTION_TESTS=PASS");
    }

    private static void ProjectsSamePhysicalApAcrossDualAcWithoutDuplicateIdentity()
    {
        var acs = new[]
        {
            Ac("AC1", "192.168.56.2", "site-a"),
            Ac("AC2", "192.168.56.5", "site-a")
        };
        var observations = new[]
        {
            Observation("AC1", "SITE-AP-001", "4c6f-d608-0400", "R/M", "RUNNING_MASTER", 100),
            Observation("AC2", "SITE-AP-001", "4C:6F:D6:08:04:00", "R/B", "RUNNING_BACKUP", 101)
        };
        var topology = new[]
        {
            new WirelessTopologyEvidence("domain-ac1-ac2", "ONE_PLUS_ONE", "CONFIRMED",
                new[] { "AC1", "AC2" }, new[] { "fixture:ha-members", "fixture:ha-role" }, true)
        };
        var result = new WirelessProjectionEngine().Project(acs, observations, topology, new("FULL", "fixture-claims"));
        Assert(result.AcDomains.Count == 1, "dual AC evidence should produce one logical domain");
        Assert(result.AcDomains[0].DomainKind == "ONE_PLUS_ONE" && result.AcDomains[0].TopologyStatus == "CONFIRMED",
            "explicit HA evidence should confirm ONE_PLUS_ONE");
        Assert(result.AcDomains[0].Members.Count == 2, "logical domain should retain both AC members");
        Assert(result.ApResources.Count == 1, "same serial and complete MAC must produce one physical AP resource");
        Assert(result.ApResources[0].Observations.Count == 2, "physical AP must retain two source observations");
        Assert(result.ApResources[0].Observations.Select(item => item.SourceAcDeviceId).Order().SequenceEqual(new[] { "AC1", "AC2" }),
            "source AC observations must remain independent");
        Assert(result.ApResources[0].Observations.Any(item => item.NormalizedState == "RUNNING_MASTER") &&
               result.ApResources[0].Observations.Any(item => item.NormalizedState == "RUNNING_BACKUP"),
            "R/M and R/B observations must remain distinct");
    }

    private static void PreservesSerialMacConflictWithoutAutoMerge()
    {
        var result = new WirelessProjectionEngine().Project(
            new[] { Ac("AC1", "192.168.56.2", "site-a"), Ac("AC2", "192.168.56.5", "site-a") },
            new[]
            {
                Observation("AC1", "AP-A", "4c6f-d608-0400", "R/M", "RUNNING_MASTER", 100),
                Observation("AC2", "AP-A", "4c6f-de4b-0500", "R/B", "RUNNING_BACKUP", 101)
            },
            Array.Empty<WirelessTopologyEvidence>(), new("FULL", "fixture-claims"));
        Assert(result.ApResources.Count == 2, "same serial with different MAC must not auto-merge");
        Assert(result.ApResources.All(item => item.IdentityStatus == "CONFLICT" && item.ConflictReason is not null),
            "serial/MAC conflict must be explicit");
    }

    private static void MergesPartialExactEvidenceWithoutUsingNames()
    {
        var serialOnly = Observation("AC1", "AP-A", null, "R/M", "RUNNING_MASTER", 100, serial: "SN-PARTIAL");
        var complete = Observation("AC2", "different-name", "4c6f-d608-0400", "R/B", "RUNNING_BACKUP", 101, serial: "SN-PARTIAL");
        var serialResult = new WirelessProjectionEngine().Project(
            new[] { Ac("AC1", "192.168.56.2", "site-a"), Ac("AC2", "192.168.56.5", "site-a") },
            new[] { serialOnly, complete }, Array.Empty<WirelessTopologyEvidence>(), new("FULL", "fixture-claims"));
        Assert(serialResult.ApResources.Count == 2 && serialResult.ApResources.Any(item => item.IdentityStatus == "CANDIDATE") &&
               serialResult.ApResources.Any(item => item.IdentityStatus == "CONFIRMED"),
            "a serial-only observation must remain a candidate until complete MAC evidence is available");

        var macOnly = Observation("AC1", "AP-A", "4c6f-d608-0400", "R/M", "RUNNING_MASTER", 100, serial: null);
        var completeForMac = Observation("AC2", "different-name", "4c6f-d608-0400", "R/B", "RUNNING_BACKUP", 101, serial: "SN-MAC-PARTIAL");
        var macResult = new WirelessProjectionEngine().Project(
            new[] { Ac("AC1", "192.168.56.2", "site-a"), Ac("AC2", "192.168.56.5", "site-a") },
            new[] { macOnly, completeForMac }, Array.Empty<WirelessTopologyEvidence>(), new("FULL", "fixture-claims"));
        Assert(macResult.ApResources.Count == 1 && macResult.ApResources.Single().IdentityStatus == "CANDIDATE" &&
               macResult.ApResources.Single().Observations.Count == 2,
            "a MAC-only observation must join one unique exact MAC identity without treating a missing serial as a conflict");
    }

    private static void DoesNotUseApNameAsIdentity()
    {
        var result = new WirelessProjectionEngine().Project(
            new[] { Ac("AC1", "192.168.56.2", "site-a"), Ac("AC2", "192.168.56.5", "site-a") },
            new[]
            {
                Observation("AC1", "same-name", null, "R", "UNKNOWN", 100, serial: null),
                Observation("AC2", "same-name", null, "R", "UNKNOWN", 101, serial: null)
            },
            Array.Empty<WirelessTopologyEvidence>(), new("FULL", "fixture-claims"));
        Assert(result.ApResources.Count == 2, "AP name must not be used as a physical identity");
        Assert(result.ApResources.All(item => item.IdentityStatus == "UNKNOWN"), "missing identity evidence must remain UNKNOWN");
    }

    private static void KeepsSameNamedGroupsAcScoped()
    {
        var result = new WirelessProjectionEngine().Project(
            new[] { Ac("AC1", "192.168.56.2", "site-a"), Ac("AC2", "192.168.56.5", "site-a") },
            new[]
            {
                Observation("AC1", "AP-A", "4c6f-d608-0400", "R/M", "RUNNING_MASTER", 100, "same-group"),
                Observation("AC2", "AP-B", "4c6f-de4b-0500", "R/B", "RUNNING_BACKUP", 101, "same-group")
            },
            Array.Empty<WirelessTopologyEvidence>(), new("FULL", "fixture-claims"));
        Assert(result.ApGroups.Count == 2, "same group name on different ACs must stay AC-scoped");
        Assert(result.ApGroups.Select(item => item.SourceAcDeviceId).Order().SequenceEqual(new[] { "AC1", "AC2" }),
            "group source AC must be preserved");
    }

    private static void RejectsMalformedMacEvidence()
    {
        foreach (var value in new[] { "4c6fd6080400junk", "4c:6f:d6:08:04:00!", "4c6f-d608-040", "4c:6fd6:080400", "4c6f d608 0400" })
            Assert(WirelessProjectionEngine.NormalizeMac(value) == "", "malformed MAC must never become exact identity evidence");
        foreach (var value in new[] { "4c6fd6080400", "4c6f-d608-0400", "4c:6f:d6:08:04:00", "4c6f.d608.0400", "4c-6f-d6-08-04-00" })
            Assert(WirelessProjectionEngine.NormalizeMac(value) == "4C:6F:D6:08:04:00", "complete supported MAC representations should normalize equally");
    }

    private static void RejectsMismatchedSourceSite()
    {
        var observation = Observation("AC1", "AP", "4c6fd6080400", "R/M", "RUNNING_MASTER", 100) with { SourceSiteId = "site-b" };
        var result = new WirelessProjectionEngine().Project(new[] { Ac("AC1", "192.168.56.2", "site-a") },
            new[] { observation }, Array.Empty<WirelessTopologyEvidence>(), new("PARTIAL_SCOPE", "claims"));
        Assert(result.ApResources.Count == 0 && result.ApGroups.Count == 0, "source Site must agree with inventory authority");
    }

    private static void CollectionFailureIsNotOffline()
    {
        foreach (var status in new[] { "DISCONNECTED", "FAILED", "CLOSED" })
        {
            var ac = Ac("AC1", "192.168.56.2", "site-a") with { ConnectionStatus = status };
            var result = new WirelessProjectionEngine().Project(new[] { ac }, Array.Empty<WirelessObservationSeed>(),
                Array.Empty<WirelessTopologyEvidence>(), new("FULL", "claims"));
            Assert(result.AcDomains[0].Members[0].RuntimeState == "STALE", "transport failure cannot prove AC offline");
        }
    }

    private static void RepeatedObservationIsIdempotent()
    {
        var observation = Observation("AC1", "AP", "4c6fd6080400", "R/M", "RUNNING_MASTER", 100);
        var result = new WirelessProjectionEngine().Project(new[] { Ac("AC1", "192.168.56.2", "site-a") },
            new[] { observation, observation with { NormalizedMacSet = new[] { "4c:6f:d6:08:04:00" } } },
            Array.Empty<WirelessTopologyEvidence>(), new("FULL", "claims"));
        Assert(result.ApResources.Single().Observations.Count == 1 && result.ApGroups.Single().MemberResourceIds.Count == 1,
            "duplicate delivery must not duplicate observations or crash group construction");
    }

    private static void KeepsIdentityWhenNameMissing()
    {
        var result = new WirelessProjectionEngine().Project(new[] { Ac("AC1", "192.168.56.2", "site-a") },
            new[] { Observation("AC1", "", "4c6fd6080400", "R/M", "RUNNING_MASTER", 100) },
            Array.Empty<WirelessTopologyEvidence>(), new("FULL", "claims"));
        Assert(result.ApResources.Single().IdentityStatus == "CONFIRMED", "display name must not be required for physical identity");
    }

    private static void RejectsAmbiguousTopologyEvidence()
    {
        var acs = new[] { Ac("AC1", "192.168.56.2", "site-a"), Ac("AC2", "192.168.56.5", "site-a") };
        var first = new WirelessTopologyEvidence("domain-a", "ONE_PLUS_ONE", "CONFIRMED", new[] { "AC1", "AC2" }, new[] { "fixture:ha" }, true);
        var engine = new WirelessProjectionEngine();
        var scope = new WirelessScopeView("FULL", "claims");
        var conflicting = engine.Project(acs, Array.Empty<WirelessObservationSeed>(),
            new[] { first, first with { DomainKind = "CLUSTER" } }, scope);
        Assert(conflicting.AcDomains.Single().TopologyStatus == "CONFLICT", "contradictory same-domain evidence must fail closed");
        var overlapping = engine.Project(acs, Array.Empty<WirelessObservationSeed>(),
            new[] { first, first with { DomainId = "domain-b" } }, scope);
        Assert(overlapping.AcDomains.Count == 2 && overlapping.AcDomains.All(item => item.TopologyStatus == "CONFLICT"),
            "overlapping domain membership must never pick an arbitrary domain");
        var unsupported = engine.Project(acs, Array.Empty<WirelessObservationSeed>(),
            new[] { first with { SourceEvidenceRefs = Array.Empty<string>() } }, scope);
        Assert(unsupported.AcDomains.Single().TopologyStatus == "EVIDENCE_PENDING", "confirmed flag without evidence refs is insufficient");
    }

    private static void CatalogPaginationAndProtectedReads()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-wireless-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = 0, SyslogBind = System.Net.IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            for (var i = 1; i <= 101; i++)
                catalog.CreateDeviceInventory(new(null, "AC-" + i, "192.0.2." + i, "H3C", "WX", "AC", "local", null, "fixture", true, null), "fixture");
            var global = P2B5SiteScopeDecision.Global;
            Assert(catalog.QueryWirelessCatalog(global).Acs.Count == 101, "wireless inventory must include ACs beyond page one");
            var pin = "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=');
            var profileId = Guid.NewGuid().ToString("N");
            catalog.CreateH3cDeviceProfile(new(profileId, "192.0.2.200", 22, "reader", "FIXTURE_REFERENCE", pin, true, 0, 0));
            var taskScope = new TaskSiteScopeAuthorizer(catalog, new P2B5SiteScopeStore(settings)).CaptureDefinition(profileId, "local-api-token");
            catalog.CreateTaskDefinition("scope-task", "H3C_SSH_READONLY", profileId, "display version", TimeSpan.FromSeconds(1), true, scopeSnapshot: taskScope);
            catalog.CreateTaskRun("scope-task");
            catalog.CreateTaskDefinition("legacy-scope-task", "H3C_SSH_READONLY", profileId, "display version", TimeSpan.FromSeconds(1), true);
            catalog.CreateTaskRun("legacy-scope-task");
            var session = new TerminalSessionSnapshot("scope-session", profileId, "fixture", "SSH", "RESTRICTED_READONLY",
                "192.0.2.200", 22, null, null, "CONNECTING", 1, null, 1, null, null, null, null, null, 0, null, false);
            catalog.CreateTerminalSession(session);
            var linked = catalog.QueryManagedDeviceInventory(search: "192.0.2.200").Items.Single();
            catalog.UpdateDeviceInventory(linked.Id, new(linked.Id, "Fixture AC", "192.0.2.200", "H3C", "WX", "AC", "local", null, "fixture", true, profileId), "fixture");
            var outputs = new Dictionary<string, string>
            {
                [H3cCommandPolicy.DisplayVersion] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "display_version.txt")),
                [H3cCommandPolicy.DisplayWlanApAll] = "<AC>display wlan ap all\nAP-ONE 1 R/M WA6320 SERIAL-001 group-a 1 day\n<AC>",
                [H3cCommandPolicy.DisplayWlanApAllAddress] = "<AC>display wlan ap all address\nAP-ONE 192.0.2.201 4c6f-d608-0400\n<AC>"
            };
            catalog.ImportH3cOfflineReplay(profileId, outputs, 1000);
            var collected = catalog.QueryWirelessCatalog(global);
            Assert(collected.Observations.Single().NormalizedMacSet.Single() == "4c6f-d608-0400", "address command facts must reach wireless projection");
            Assert(collected.Observations.Single().NormalizedState == "STALE" && collected.Warnings!.Contains("OFFLINE_FIXTURE_EVIDENCE"),
                "offline replay must not masquerade as live AP status");
            catalog.ImportH3cOfflineReplay(profileId, new Dictionary<string, string>
            {
                [H3cCommandPolicy.DisplayWlanApAll] = "<AC>display wlan ap all\n% Unrecognized command found at '^' position.\n<AC>"
            }, 2000);
            var preserved = catalog.QueryWirelessCatalog(global);
            Assert(preserved.Observations.Single().SerialEvidence == "SERIAL-001" && preserved.Observations.Single().CapturedMs == 1000 &&
                preserved.Warnings!.Contains("AP_COLLECTION_FAILED_PREVIOUS_EVIDENCE_PRESERVED"), "failed collection must preserve the previous valid AP evidence");
            var runtimeService = new WirelessReadModelService(catalog);
            var physicalId = runtimeService.GetSnapshot(global).ApResources.Single().Id;
            var persistedSnapshot = runtimeService.GetSnapshot(global);
            var manifestPage = catalog.QueryWirelessCollectionManifests(global, "", null, 1, 10);
            Assert(manifestPage.Total == 1 && manifestPage.Items.Single().ObservationCount == persistedSnapshot.ApResources.Sum(item => item.Observations.Count),
                "persisted wireless collection manifests must be queryable with stable counts");
            var manifestDetail = catalog.GetWirelessCollectionManifest(manifestPage.Items.Single().Id, global);
            Assert(manifestDetail is not null && manifestDetail.Items.Count == manifestPage.Items.Single().ObservationCount,
                "collection manifest detail must retain per-observation provenance");
            var hiddenManifest = catalog.GetWirelessCollectionManifest(manifestPage.Items.Single().Id,
                new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "other-site" }, "fixture"));
            Assert(hiddenManifest is null, "collection manifest detail must hide when no current Site claim can see its items");
            using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "catalog.db")}"))
            {
                db.Open();
                using var projectionQuery = db.CreateCommand();
                projectionQuery.CommandText = "SELECT (SELECT COUNT(*) FROM wireless_ac_domains),(SELECT COUNT(*) FROM wireless_ap_resources),(SELECT COUNT(*) FROM wireless_ap_observations),(SELECT COUNT(*) FROM wireless_ap_groups),(SELECT COUNT(*) FROM wireless_collection_manifests),(SELECT COUNT(*) FROM wireless_collection_manifest_items)";
                using var row = projectionQuery.ExecuteReader();
                Assert(row.Read() && row.GetInt64(0) == persistedSnapshot.AcDomains.Count && row.GetInt64(1) == persistedSnapshot.ApResources.Count &&
                    row.GetInt64(2) == persistedSnapshot.ApResources.Sum(item => item.Observations.Count) && row.GetInt64(3) == persistedSnapshot.ApGroups.Count &&
                    row.GetInt64(4) == 1 && row.GetInt64(5) == persistedSnapshot.ApResources.Sum(item => item.Observations.Count),
                    "global wireless projection must persist one stable row per domain/resource/observation/group and one collection manifest");
            }
            var restartedCatalog = new Catalog(settings);
            restartedCatalog.Initialize();
            var restartedSnapshot = new WirelessReadModelService(restartedCatalog).GetSnapshot(global);
            Assert(restartedSnapshot.ApResources.Single().Id == physicalId && restartedSnapshot.ApResources.Single().Observations.Count == 1,
                "wireless projection IDs and observation links must survive service restart");
            var failedRuntime = runtimeService.GetRuntimeState(physicalId, global, nowMs: 2500)!;
            Assert(failedRuntime.NormalizedState == "STALE" && failedRuntime.Sources.Single().LatestOutcome == "UNSUPPORTED" &&
                failedRuntime.Sources.Single().Current?.NormalizedState == "RUNNING_MASTER", "runtime API projection must preserve valid state after a failed command fact");
            outputs[H3cCommandPolicy.DisplayWlanApAll] = outputs[H3cCommandPolicy.DisplayWlanApAll].Replace("R/M", "R/B");
            catalog.ImportH3cOfflineReplay(profileId, outputs, 3000);
            catalog.ImportH3cOfflineReplay(profileId, outputs, 4000);
            outputs[H3cCommandPolicy.DisplayWlanApAll] = outputs[H3cCommandPolicy.DisplayWlanApAll].Replace("R/B", "I");
            catalog.ImportH3cOfflineReplay(profileId, outputs, 3500); // A late import must not reorder effective history.
            var reconstructed = runtimeService.GetRuntimeState(physicalId, global, nowMs: 5000)!;
            var sourceHistory = reconstructed.Sources.Single();
            Assert(sourceHistory.Current?.CapturedMs == 4000 && sourceHistory.Current.NormalizedState == "RUNNING_BACKUP" &&
                sourceHistory.PreviousEffective?.NormalizedState == "OFFLINE" && sourceHistory.Recent10.Count == 3,
                "existing SQLite facts must reconstruct chronological changes and suppress unchanged samples without adding schema");
            Assert(runtimeService.GetRuntimeState(physicalId, global, nowMs: 5000, policy: new(300_000, 2))!.Sources.Single().HistoryStatus == "PARTIAL_SCAN_LIMIT",
                "catalog scan cap must be observable instead of silently truncating effective history");
            Assert(runtimeService.GetRuntimeState(physicalId, P2B5SiteScopeDecision.Denied("revoked"), nowMs: 5000) is null,
                "runtime lookup after claims revocation must not reveal history");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { catalog.QueryWirelessRuntimeHistory(runtimeService.GetSnapshot(global).ApResources.Single(), global, 10, cancelled.Token); throw new InvalidOperationException("cancelled read succeeded"); }
                catch (OperationCanceledException) { }
            }
            var query = new TerminalSessionQuery(null, null, null, null, null, null, null, null, 1, 20, "started", true);
            var visible = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "local" }, "fixture");
            var hidden = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "other-site" }, "fixture");
            Assert(catalog.QueryTerminalSessions(query, visible).Total == 1 && catalog.TerminalSessionIsInScope(session.Id, visible),
                "authorized terminal must remain readable");
            Assert(catalog.QueryTerminalSessions(query, hidden).Total == 0 && !catalog.TerminalSessionIsInScope(session.Id, hidden),
                "terminal list/count/detail must reject another Site");
            Assert(catalog.RecentTaskRuns(scope: visible).Count == 1 && catalog.RecentTaskRuns(scope: hidden).Count == 0,
                "generic task results must filter current claims and proven creation scope before the limit");
            Assert(catalog.RecentTaskRuns(scope: global).Count == 2, "global operator retains access to unscoped legacy records without exposing them to Site-scoped users");
            var revoked = P2B5SiteScopeDecision.Denied("revoked");
            Assert(!catalog.TerminalSessionIsInScope(session.Id, revoked) && catalog.QueryTerminalSessions(query, revoked).Total == 0 &&
                catalog.RecentTaskRuns(scope: revoked).Count == 0, "revocation must hide terminal and task evidence");
            Assert(catalog.QueryTerminalSessions(query, global).Total == 1, "global administrator must retain existing access");
            var fixture = new WirelessFixtureDocument(
                new[] { Ac("AC1", "192.168.56.2", "site-a"), Ac("AC2", "192.168.56.5", "site-b") },
                new[] { Observation("AC1", "AP", "4c6fd6080400", "R/M", "RUNNING_MASTER", 100),
                    Observation("AC2", "AP", "4c6fd6080400", "R/B", "RUNNING_BACKUP", 101) with { SourceSiteId = "site-b" } },
                Array.Empty<WirelessTopologyEvidence>());
            var path = Path.Combine(root, "scoped-wireless.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(fixture));
            var previousFixture = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FIXTURE_PATH");
            try
            {
                Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FIXTURE_PATH", path);
                var read = new WirelessReadModelService(catalog);
                var oneSite = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "site-a" }, "fixture");
                var partial = read.GetSnapshot(oneSite);
                Assert(partial.Scope.Status == "PARTIAL_SCOPE" && partial.ApResources.Single().Observations.Single().SourceAcDeviceId == "AC1" &&
                    partial.AcDomains.SelectMany(item => item.Members).All(item => item.SiteId == "site-a") && partial.ApGroups.Count == 1,
                    "partial Site visibility must project only visible observations, members, groups and counts");
                var scopedRuntime = read.GetRuntimeState(partial.ApResources.Single().Id, oneSite, nowMs: 1000)!;
                Assert(scopedRuntime.Sources.Single().SourceAcDeviceId == "AC1" && scopedRuntime.Scope.Status == "PARTIAL_SCOPE" &&
                    read.GetRuntimeState(partial.ApResources.Single().Id, oneSite, nowMs: 1000, sourceAcId: "AC2") is null,
                    "runtime history filters cannot expand current authorized observation scope");
                var bothSites = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "site-a", "site-b" }, "fixture");
                Assert(read.GetSnapshot(bothSites).ApResources.Single().Observations.Count == 2, "two authorized Sites must retain both visible sources");
                Assert(read.GetSnapshot(global).ApResources.Single().Observations.Count == 2, "global scope must retain independent multi-AC observations");
                Assert(read.GetSnapshot(revoked).ApResources.Count == 0 && read.GetSnapshot(revoked).AcDomains.Count == 0,
                    "revoked fixture scope must reveal zero resources");
            }
            finally { Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FIXTURE_PATH", previousFixture); }

        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static void MissingEvidenceIsNotContradiction()
    {
        var result = new WirelessProjectionEngine().Project(
            new[] { Ac("AC1", "192.168.56.2", "site-a"), Ac("AC2", "192.168.56.5", "site-a") },
            new[] { Observation("AC1", "AP", "4c6fd6080400", "R/M", "RUNNING_MASTER", 100),
                Observation("AC2", "AP", null, "R/B", "RUNNING_BACKUP", 101) },
            Array.Empty<WirelessTopologyEvidence>(), new("FULL", "claims"));
        Assert(result.ApResources.Count == 2 && result.ApResources.Any(item => item.IdentityStatus == "CANDIDATE") &&
            result.ApResources.Any(item => item.IdentityStatus == "CONFIRMED"), "missing MAC must stay candidate without creating a false contradiction or merging");
    }

    private static void SameRevisionStateConflictIsPreserved()
    {
        var observation = Observation("AC1", "AP", "4c6fd6080400", "R/M", "RUNNING_MASTER", 100);
        var result = new WirelessProjectionEngine().Project(new[] { Ac("AC1", "192.168.56.2", "site-a") },
            new[] { observation, observation with { RawState = "R/B", NormalizedState = "RUNNING_BACKUP" } },
            Array.Empty<WirelessTopologyEvidence>(), new("FULL", "claims"));
        var resource = result.ApResources.Single();
        Assert(result.AcDomains.Single().Members.Single().RuntimeState == "CONFLICT", "domain member summary must not hide conflicting observations");
        Assert(resource.Observations.Count == 2 && resource.Observations.All(item => item.NormalizedState == "CONFLICT") &&
            resource.Observations.Select(item => item.Id).Distinct().Count() == 2, "contradictory same-revision state must retain both raw sources with unique conflict IDs");
        Assert(result.ApGroups.Single().MemberResourceIds.Single() == resource.Id, "state conflict must preserve AP group membership");
    }

    private static void ReportsMultiSourceMasterWithoutTopologyInference()
    {
        var result = new WirelessProjectionEngine().Project(
            new[] { Ac("AC1", "192.168.56.2", "site-a"), Ac("AC2", "192.168.56.5", "site-a") },
            new[] { Observation("AC1", "AP", "4c6fd6080400", "R/M", "RUNNING_MASTER", 100),
                Observation("AC2", "AP", "4c6fd6080400", "R/M", "RUNNING_MASTER", 101) },
            Array.Empty<WirelessTopologyEvidence>(), new("FULL", "claims"));
        Assert(result.ApResources.Single().StateConflictReason == "MULTI_SOURCE_MASTER" &&
            result.ApResources.Single().Observations.Count == 2 && result.AcDomains.All(item => item.TopologyStatus == "EVIDENCE_PENDING"),
            "multiple masters must expose the conflict reason without inferring HA topology or discarding sources");
    }

    private static WirelessAcDeviceSeed Ac(string id, string ip, string site) =>
        new(id, id, site, ip, "H3C", "WX", "CONNECTED", id + "-profile", "V7", true);

    private static WirelessObservationSeed Observation(string acId, string apName, string? mac, string rawState,
        string normalizedState, long captured, string? group = "default-group", string? serial = "SN-AP-0001") =>
        new(acId, acId, "site-a", "WX", "V7", apName, serial,
            mac is null ? Array.Empty<string>() : new[] { mac }, "WA6320-HCL", group, rawState, normalizedState,
            normalizedState == "UNKNOWN" ? "LOW" : "HIGH", captured, "fact:fixture", "fixture.ap.v1", "revision:" + captured);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
