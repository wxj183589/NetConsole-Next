using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class WirelessGroupTests
{
    private static readonly WirelessScopeView Full = new("FULL", "fixture");
    private static readonly WirelessAcDeviceSeed[] Acs = { Ac("AC1"), Ac("AC2"), Ac("AC3") };
    private static readonly WirelessTopologyEvidence[] Topology =
        { new("domain-1", "ONE_PLUS_ONE", "CONFIRMED", new[] { "AC1", "AC2" }, new[] { "fixture:ha-members" }, true) };

    public static void Run()
    {
        NameOnlyGroupsRemainCandidates();
        ExplicitKeysStayAcScopedWithoutSharedEvidence();
        ExplicitDomainEvidenceAndEmptyGroups();
        MissingAndInvalidEvidenceFailsClosed();
        RenameAndMembershipRevisionsAreIndependent();
        ContradictoryNamesAndKeysRemainSeparate();
        DomainMemberChangeDoesNotPromoteGroups();
        GroupMembersRetainOnlyActualObservationProvenance();
        SourceAndSiteFilteringPrecedesProjection();
        LegacyProjectionMigrationIsAtomicAndIdempotent();
        Console.WriteLine("WIRELESS_GROUP_CONTRACT_TESTS=PASS");
        CandidateCacheCompactsOnlyObservedSources();
        DelayedSnapshotsCannotRestoreEvictedCandidates();
        EmptyGroupEvidenceRetainsPerSourceWatermarks();
        CandidateCacheCompactionRollsBackAtomically();
        Console.WriteLine("WIRELESS_GROUP_CACHE_TESTS=4/4 PASS");
    }

    private static void NameOnlyGroupsRemainCandidates()
    {
        var snapshot = Project(new[] { Ap("AC1", "one", 100), Ap("AC2", "one", 100) });
        Assert(snapshot.ApGroups.Count == 2, "SC-36: same name in one Domain remains two source groups");
        foreach (var group in snapshot.ApGroups)
        {
            Assert(group.GroupScope == "AC" && group.GroupIdentityStatus == "CANDIDATE" &&
                group.GroupIdentityKey is null && group.SourceGroupKey is null, "AP listing text must never become a stable vendor group key");
            Assert(group.SourceObservationIds.Count == 1 && snapshot.ApResources.SelectMany(item => item.Observations)
                .Any(item => item.Id == group.SourceObservationIds[0]), "group provenance must reference actual AP observations");
        }
    }

    private static void ExplicitKeysStayAcScopedWithoutSharedEvidence()
    {
        var baseline = Project(new[] { Ap("AC1", "one", 100), Ap("AC2", "one", 100) });
        var groups = Reproject(baseline, new[] { Evidence(baseline, "AC1", "key-a"), Evidence(baseline, "AC2", "key-a") }).ApGroups;
        Assert(groups.Count == 2 && groups.All(item => item.GroupScope == "AC" && item.GroupIdentityStatus == "CONFIRMED"),
            "SC-36: shared Domain and shared display text do not establish Domain ownership");
        Assert(groups.Select(item => item.GroupIdentityKey).Order().SequenceEqual(new[] { "AC|AC1|key-a", "AC|AC2|key-a" }),
            "group stable identity must include explicit source AC and vendor key");
    }

    private static void ExplicitDomainEvidenceAndEmptyGroups()
    {
        var baseline = Project(new[] { Ap("AC1", "one", 100), Ap("AC2", "one", 100) });
        var evidence = new[] { Shared(Evidence(baseline, "AC1", "shared-key")), Shared(Evidence(baseline, "AC2", "shared-key")) };
        var group = Reproject(baseline, evidence).ApGroups.Single();
        Assert(group.GroupScope == "DOMAIN" && group.GroupIdentityKey == "DOMAIN|domain-1|shared-key" &&
            group.GroupIdentityStatus == "CONFIRMED" && group.SourceAcDeviceIds.SequenceEqual(new[] { "AC1", "AC2" }),
            "SC-37: explicit shared evidence and confirmed matching Domain establish one Domain group");
        Assert(group.MemberResourceIds.Count == 1 && group.SourceObservationIds.Count == 2 && group.SourceEvidenceRefs.Contains("fixture:domain-shared"),
            "Domain group retains both actual source relations and explicit shared provenance");
        var emptyEvidence = evidence.Select(item => item with { SourceObservationIds = Array.Empty<string>() }).ToArray();
        var empty = Project(Array.Empty<WirelessObservationSeed>(), emptyEvidence).ApGroups.Single();
        var oneSourceEmpty = Project(Array.Empty<WirelessObservationSeed>(), emptyEvidence.Take(1).ToArray()).ApGroups.Single();
        Assert(empty.MemberResourceIds.Count == 0 && empty.SourceObservationIds.Count == 0 && empty.GroupIdentityStatus == "CONFIRMED",
            "explicitly observed empty groups do not require fabricated AP members");
        Assert(empty.Id == oneSourceEmpty.Id && empty.MembershipRevision != oneSourceEmpty.MembershipRevision,
            "empty Domain membership revision includes observed source relations");
    }

    private static void MissingAndInvalidEvidenceFailsClosed()
    {
        var baseline = Project(new[] { Ap("AC1", "one", 100) });
        var keyless = Evidence(baseline, "AC1", null);
        var candidate = Reproject(baseline, new[] { keyless }).ApGroups.Single();
        Assert(candidate.GroupIdentityKey is null && candidate.GroupIdentityStatus == "CANDIDATE", "SC-38: missing source key cannot confirm a group");
        foreach (var invalid in new[]
        {
            null!,
            Evidence(baseline, "AC1", "key") with { SourceAcDeviceId = null! },
            Evidence(baseline, "AC1", "key") with { SourceAcDeviceId = "" },
            Evidence(baseline, "AC1", "key") with { SourceSiteId = "other-site" },
            Evidence(baseline, "AC1", "key") with { SourceObservationIds = null! },
            Evidence(baseline, "AC1", "key") with { SourceObservationIds = new string[] { null! } },
            Evidence(baseline, "AC1", "key") with { SourceObservationIds = new[] { "hidden-observation" } },
            Evidence(baseline, "AC1", "key") with { SourceObservationIds = new[] { "" } }
        })
        {
            var result = Reproject(baseline, new[] { invalid });
            Assert(result.ApGroups.Single().GroupIdentityKey is null && result.ApGroups.Single().SourceGroupKey is null &&
                result.ApGroups.Single().SourceRevision == baseline.ApGroups.Single().SourceRevision,
                "missing/foreign source and invalid membership references must not influence identity, counts or revisions");
        }
        var unproven = Reproject(baseline, new[] { Evidence(baseline, "AC1", "key") with { RawManifestRef = "" } }).ApGroups.Single();
        Assert(unproven.GroupIdentityKey is null && unproven.GroupIdentityStatus == "CANDIDATE", "a key without source provenance cannot confirm identity");
    }

    private static void RenameAndMembershipRevisionsAreIndependent()
    {
        var first = Project(new[] { Ap("AC1", "old-name", 100) });
        var second = Project(new[] { Ap("AC1", "new-name", 200) });
        var firstGroup = Reproject(first, new[] { Evidence(first, "AC1", "stable-key") }).ApGroups.Single();
        var secondGroup = Reproject(second, new[] { Evidence(second, "AC1", "stable-key") }).ApGroups.Single();
        Assert(firstGroup.Id == secondGroup.Id && firstGroup.GroupIdentityKey == secondGroup.GroupIdentityKey &&
            firstGroup.MembershipRevision == secondGroup.MembershipRevision && firstGroup.SourceRevision != secondGroup.SourceRevision,
            "SC-39: rename keeps stable identity and member relation revision while advancing source evidence");
        var unchanged = Project(new[] { Ap("AC1", "new-name", 300) });
        Assert(Reproject(unchanged, new[] { Evidence(unchanged, "AC1", "stable-key") }).ApGroups.Single().MembershipRevision == secondGroup.MembershipRevision,
            "unchanged membership refreshes do not create membership revisions");
        var combined = Project(new[] { Ap("AC1", "old-name", 100), Ap("AC1", "new-name", 200) });
        var historical = Evidence(combined, "AC1", "stable-key") with
        {
            DisplayName = "old-name", CapturedMs = 100, SourceRevision = "group-revision:100",
            SourceObservationIds = combined.ApResources.SelectMany(item => item.Observations).Where(item => item.CapturedMs == 100).Select(item => item.Id).ToArray()
        };
        var current = Evidence(combined, "AC1", "stable-key") with
        {
            DisplayName = "new-name", CapturedMs = 200,
            SourceObservationIds = combined.ApResources.SelectMany(item => item.Observations).Where(item => item.CapturedMs == 200).Select(item => item.Id).ToArray()
        };
        var renamed = Reproject(combined, new[] { historical, current }).ApGroups.Single();
        Assert(renamed.DisplayName == "new-name" && renamed.SourceGroupObservations.Count == 2 && renamed.SourceObservationIds.SequenceEqual(current.SourceObservationIds),
            "rename evidence remains auditable while membership uses the latest source revision");
        var changed = Project(new[] { Ap("AC1", "new-name", 400), Ap("AC1", "new-name", 400, "AP2", "SN-2", "4c6fd6080402") });
        var changedGroup = Reproject(changed, new[] { Evidence(changed, "AC1", "stable-key") }).ApGroups.Single();
        Assert(changedGroup.Id == secondGroup.Id && changedGroup.MembershipRevision != secondGroup.MembershipRevision && changedGroup.MemberResourceIds.Count == 2,
            "actual member relation changes advance membership revision");
    }

    private static void ContradictoryNamesAndKeysRemainSeparate()
    {
        var baseline = Project(new[] { Ap("AC1", "same-name", 100) });
        var distinctKeys = Reproject(baseline, new[] { Evidence(baseline, "AC1", "key-a"), Evidence(baseline, "AC1", "key-b") }).ApGroups;
        Assert(distinctKeys.Count == 2 && distinctKeys.All(item => item.GroupIdentityStatus == "CONFLICT" && item.ConflictReason == "GROUP_NAME_KEY_CONFLICT"),
            "SC-40: equal display names with distinct vendor keys stay separate and expose conflict");
        var first = Evidence(baseline, "AC1", "key-a");
        var conflicting = Reproject(baseline, new[] { first, first with { DisplayName = "other-name", SourceRevision = "another-revision" } }).ApGroups.Single();
        Assert(conflicting.GroupIdentityStatus == "CONFLICT" && conflicting.ConflictReason == "GROUP_DISPLAY_NAME_CONFLICT" && conflicting.SourceGroupObservations.Count == 2,
            "simultaneous source names for one stable key retain both conflicting revisions");
    }

    private static void DomainMemberChangeDoesNotPromoteGroups()
    {
        var before = Project(new[] { Ap("AC1", "one", 100) });
        var local = Evidence(before, "AC1", "key-a");
        var oldGroup = Reproject(before, new[] { local }).ApGroups.Single();
        var changedTopology = new[] { Topology[0] with { MemberDeviceIds = new[] { "AC1", "AC2", "AC3" } } };
        var after = Project(new[] { Ap("AC1", "one", 100) }, topology: changedTopology);
        var current = Evidence(after, "AC1", "key-a");
        var localAfter = Reproject(after, new[] { current }, changedTopology).ApGroups.Single();
        Assert(localAfter.GroupScope == "AC" && localAfter.Id == oldGroup.Id, "SC-41: Domain member changes never upgrade an AC group");
        var staleShared = Reproject(after, new[] { Shared(current) }, changedTopology).ApGroups.Single();
        Assert(staleShared.GroupScope == "AC" && staleShared.GroupIdentityStatus == "CONFLICT" && staleShared.ConflictReason == "DOMAIN_SHARED_EVIDENCE_CONFLICT",
            "a shared-group assertion for the old Domain member set cannot confirm the new Domain membership");
    }

    private static void GroupMembersRetainOnlyActualObservationProvenance()
    {
        var baseline = Project(new[] { Ap("AC1", "first", 100), Ap("AC2", "second", 200) });
        var group = baseline.ApGroups.Single(item => item.SourceAcDeviceId == "AC1");
        var member = WirelessProjectionEngine.GroupMembers(baseline, group).Single();
        Assert(member.Observations.Single().SourceAcDeviceId == "AC1" && member.SourceObservationIds.SequenceEqual(group.SourceObservationIds) &&
            member.FirstSeenMs == 100 && member.LastSeenMs == 100 && member.SourceRevision == WirelessProjectionEngine.Fingerprint(new[] { "ap-revision:100" }) &&
            member.SourceAcDomainIds.SequenceEqual(new[] { "domain-1" }) && member.StateConflictReason is null,
            "group members must reproject names, timestamps, state and revisions from this group's actual observations only");
        var contradictory = Ap("AC1", "first", 100);
        var conflict = Project(new[] { contradictory, contradictory with { RawState = "R/B", NormalizedState = "RUNNING_BACKUP" } });
        var conflictGroup = conflict.ApGroups.Single();
        var conflictMember = WirelessProjectionEngine.GroupMembers(conflict, conflictGroup).Single();
        Assert(conflictMember.Observations.Count == 2 && conflictMember.Observations.Select(item => item.Id).Distinct().Count() == 2 &&
            conflictMember.SourceObservationIds.Order().SequenceEqual(conflictGroup.SourceObservationIds.Order()) && conflictMember.StateConflictReason == "SAME_REVISION_STATE_CONFLICT",
            "same-revision conflict suffixes and original raw observations survive group member reprojection");
    }

    private static void SourceAndSiteFilteringPrecedesProjection()
    {
        var path = Path.Combine(Path.GetTempPath(), "next-group-fixture-" + Guid.NewGuid().ToString("N") + ".json");
        var previous = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FIXTURE_PATH");
        try
        {
            var acs = new[] { Ac("AC1"), Ac("AC2", "site-b") };
            var observations = new[] { Ap("AC1", "same-name", 100) with { SerialEvidence = null },
                Ap("AC2", "same-name", 200) with { SourceSiteId = "site-b" } };
            var baseline = new WirelessProjectionEngine().Project(acs, observations, Topology, Full);
            var evidence = new[] { Shared(Evidence(baseline, "AC1", "key")), Shared(Evidence(baseline, "AC2", "key")) };
            var fixture = new WirelessFixtureDocument(acs, observations, Topology, GroupEvidence: evidence);
            File.WriteAllText(path, JsonSerializer.Serialize(fixture));
            Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FIXTURE_PATH", path);
            var service = new WirelessReadModelService(new Catalog(new RuntimeSettings
            {
                DataRoot = Path.GetDirectoryName(path)!, WebPort = 18771, SyslogPort = 0,
                SyslogBind = System.Net.IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024
            }));
            var oneSite = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "site-a" }, "site-a-claim");
            var partial = service.GetGroupSnapshot(oneSite);
            var visible = partial.ApGroups.Single();
            Assert(visible.SourceAcDeviceIds.SequenceEqual(new[] { "AC1" }) && visible.SourceGroupObservations.All(item =>
                (item.DomainMemberDeviceIds ?? Array.Empty<string>()).All(id => id == "AC1") && (item.DomainSharedEvidenceRefs?.Count ?? 0) == 0) &&
                !JsonSerializer.Serialize(partial.ApGroups).Contains("AC2", StringComparison.Ordinal),
                "invisible Site topology/evidence cannot influence or leak through group provenance");
            var sourceFiltered = service.GetGroupSnapshot(P2B5SiteScopeDecision.Global, "AC1");
            Assert(sourceFiltered.ApGroups.Single().SourceAcDeviceIds.SequenceEqual(new[] { "AC1" }) &&
                sourceFiltered.ApResources.Single().Observations.Single().SourceAcDeviceId == "AC1",
                "sourceAC filtering runs before identity, member and revision aggregation");
            var selectedGroup = sourceFiltered.ApGroups.Single();
            var selectedMember = WirelessProjectionEngine.GroupMembers(sourceFiltered, selectedGroup).Single();
            Assert(sourceFiltered.ApResources.Single().Id == baseline.ApResources.Single().Id &&
                selectedGroup.MemberResourceIds.Single() == baseline.ApResources.Single().Id && selectedMember.Id == baseline.ApResources.Single().Id,
                "a MAC-only selected source preserves the complete authorized projection's physical AP locator");
            Assert(selectedMember.SerialEvidence is null && selectedMember.Observations.Single().SourceAcDeviceId == "AC1" &&
                selectedMember.LastSeenMs == 100 && selectedMember.StateConflictReason is null &&
                selectedMember.SourceRevision == WirelessProjectionEngine.Fingerprint(new[] { "ap-revision:100" }),
                "preserving canonical resource IDs does not expose another source's serial, timestamps, state or revision");
            File.WriteAllText(path, JsonSerializer.Serialize(fixture with
            {
                Observations = new[] { observations[0], observations[1] with { ApGroup = "changed-hidden", SourceRevision = "other-revision", CapturedMs = 999 } },
                GroupEvidence = new[] { evidence[0], evidence[1] with { SourceGroupKey = "changed-key", DisplayName = "changed-hidden" } }
            }));
            Assert(service.GetGroupSnapshot(P2B5SiteScopeDecision.Global, "AC1").SourceRevision == sourceFiltered.SourceRevision &&
                service.GetGroupSnapshot(oneSite).SourceRevision == partial.SourceRevision,
                "another source's changes cannot alter selected-source or Site-scoped group revisions");
            Assert(service.GetGroupSnapshot(P2B5SiteScopeDecision.Global, "not-visible").ApGroups.Count == 0 &&
                service.GetGroupSnapshot(P2B5SiteScopeDecision.Denied("revoked")).ApGroups.Count == 0,
                "missing source and revoked claims produce no group evidence");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FIXTURE_PATH", previous);
            File.Delete(path);
        }
    }

    private static void LegacyProjectionMigrationIsAtomicAndIdempotent()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Execute(db, "CREATE TABLE schema_versions(version INTEGER PRIMARY KEY,applied_ms INTEGER NOT NULL)");
        using (var resource = typeof(Catalog).Assembly.GetManifestResourceStream("schema_v18.sql")!)
        using (var reader = new StreamReader(resource)) Execute(db, reader.ReadToEnd());
        Execute(db, "INSERT INTO schema_versions VALUES(23,23)");
        Execute(db, "INSERT INTO wireless_ap_groups VALUES('legacy','AC1','domain-1','display-as-key','old-name','AC_OWNED','[\"ap-1\"]','old-revision','FULL',100)");
        Execute(db, "CREATE TRIGGER fail_group_migration BEFORE INSERT ON schema_versions WHEN NEW.version=24 BEGIN SELECT RAISE(ABORT,'fixture migration failure'); END");
        try { Catalog.ApplyV24Migration(db); throw new InvalidOperationException("migration unexpectedly succeeded"); }
        catch (SqliteException) { }
        Assert(Scalar(db, "SELECT source_group_key FROM wireless_ap_groups WHERE id='legacy'") == "display-as-key" &&
            Scalar(db, "SELECT COUNT(*) FROM schema_versions WHERE version=24") == "0" &&
            Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE name='wireless_ap_groups_v18'") == "0", "migration failure rolls back the table replacement and all data changes");
        Execute(db, "DROP TRIGGER fail_group_migration");
        Catalog.ApplyV24Migration(db);
        Catalog.ApplyV24Migration(db);
        Assert(Scalar(db, "SELECT COUNT(*) FROM schema_versions WHERE version IN (23,24)") == "2" &&
            Scalar(db, "SELECT group_identity_status FROM wireless_ap_groups WHERE id='legacy'") == "CANDIDATE" &&
            Scalar(db, "SELECT source_group_key IS NULL AND group_identity_key IS NULL FROM wireless_ap_groups WHERE id='legacy'") == "1" &&
            Scalar(db, "SELECT display_name || '|' || member_resource_ids_json FROM wireless_ap_groups WHERE id='legacy'") == "old-name|[\"ap-1\"]",
            "23-to-24 upgrade preserves legacy data as unconfirmed name-only projection and remains restart safe");
        Execute(db, "INSERT INTO wireless_ap_groups SELECT 'another',source_ac_device_id,source_ac_domain_id,NULL,display_name,group_scope,member_resource_ids_json,source_revision,scope_status,updated_ms,NULL,group_identity_status,membership_revision,source_observation_ids_json,source_ac_device_ids_json,source_evidence_refs_json,observed_at_ms,conflict_reason,source_group_observations_json FROM wireless_ap_groups WHERE id='legacy'");
        Assert(Scalar(db, "SELECT COUNT(*) FROM wireless_ap_groups") == "2", "multiple unconfirmed groups per AC must not collide on a fabricated key");
    }

    private static void CandidateCacheCompactsOnlyObservedSources()
    {
        using var fixture = new GroupCacheFixture();
        var baseline = Project(new[] { Ap("AC1", "confirmed", 10), Ap("AC2", "unobserved", 10) });
        var confirmed = Reproject(baseline, new[] { Evidence(baseline, "AC1", "stable-key") });
        fixture.Catalog.PersistWirelessProjection(confirmed);
        var preserved = Scalar(fixture.Db, "SELECT id FROM wireless_ap_groups WHERE source_ac_device_id='AC2'");
        for (var captured = 100; captured < 110; captured++)
        {
            var snapshot = Project(new[] { Ap("AC1", "first", captured),
                Ap("AC1", "second", captured, "AP2", "SN-2", "4c6fd6080402") });
            fixture.Catalog.PersistWirelessProjection(snapshot);
            fixture.Catalog.PersistWirelessProjection(snapshot);
            Assert(Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups WHERE source_ac_device_id='AC1' AND group_identity_key IS NULL") == "2" &&
                Scalar(fixture.Db, "SELECT MIN(observed_at_ms) FROM wireless_ap_groups WHERE source_ac_device_id='AC1' AND group_identity_key IS NULL") == captured.ToString(),
                "each observed source retains all current candidates without accumulating prior revisions or duplicate refreshes");
        }
        Assert(Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups WHERE group_identity_key='AC|AC1|stable-key'") == "1" &&
            Scalar(fixture.Db, "SELECT id FROM wireless_ap_groups WHERE source_ac_device_id='AC2'") == preserved &&
            Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_observations") == "22",
            "candidate cache compaction preserves confirmed identities, sources without current evidence, and historical source observations");
        fixture.Catalog.PersistWirelessProjection(Project(Array.Empty<WirelessObservationSeed>()));
        Assert(Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups") == "4", "FULL authorization with no source evidence cannot empty the group cache");
        var partial = Project(new[] { Ap("AC1", "partial", 1000) }) with { Scope = new("PARTIAL", "fixture") };
        var manifestCount = Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_collection_manifests");
        try { fixture.Catalog.PersistWirelessProjection(partial); throw new Exception("partial projection unexpectedly persisted"); }
        catch (InvalidOperationException) { }
        Assert(Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups") == "4" &&
            Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_observations") == "22" &&
            Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_collection_manifests") == manifestCount,
            "partial-scope rejection occurs before any insertion or compaction");
    }

    private static void DelayedSnapshotsCannotRestoreEvictedCandidates()
    {
        using var fixture = new GroupCacheFixture();
        var older = Project(new[] { Ap("AC1", "old", 100) });
        var newer = Project(new[] { Ap("AC1", "new", 200) });
        fixture.Catalog.PersistWirelessProjection(older);
        fixture.Catalog.PersistWirelessProjection(newer);
        fixture.Catalog.PersistWirelessProjection(older);
        Assert(Scalar(fixture.Db, "SELECT display_name FROM wireless_ap_groups") == "new" &&
            Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups") == "1",
            "a delayed older snapshot neither deletes the newer candidate nor restores the evicted candidate");
        var noGroup = Project(new[] { Ap("AC1", "", 300) });
        fixture.Catalog.PersistWirelessProjection(noGroup);
        fixture.Catalog.PersistWirelessProjection(newer);
        fixture.Catalog.PersistWirelessProjection(older);
        Assert(Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups") == "0", "source observation watermarks survive a zero-candidate projection");

        var snapshots = Enumerable.Range(400, 8).Select(captured => Project(new[] { Ap("AC1", "revision-" + captured, captured) })).ToArray();
        Parallel.ForEach(snapshots.Reverse(), snapshot => fixture.Catalog.PersistWirelessProjection(snapshot));
        fixture.Catalog.PersistWirelessProjection(snapshots[0]);
        Assert(Scalar(fixture.Db, "SELECT display_name FROM wireless_ap_groups") == "revision-407" &&
            Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups") == "1",
            "concurrent transactions preserve the newest source candidate regardless of snapshot arrival order");
    }

    private static void EmptyGroupEvidenceRetainsPerSourceWatermarks()
    {
        using var fixture = new GroupCacheFixture();
        var seed = Project(new[] { Ap("AC1", "one", 100), Ap("AC2", "two", 100) });
        var first = Shared(Evidence(seed, "AC1", "shared-key")) with
            { SourceObservationIds = Array.Empty<string>(), CapturedMs = 500, SourceRevision = "group:AC1:500" };
        var second = Shared(Evidence(seed, "AC2", "shared-key")) with
            { SourceObservationIds = Array.Empty<string>(), CapturedMs = 200, SourceRevision = "group:AC2:200" };
        fixture.Catalog.PersistWirelessProjection(Project(Array.Empty<WirelessObservationSeed>(), new[] { first, second }));
        var sourceTwo = Evidence(seed, "AC2", null) with
            { SourceObservationIds = Array.Empty<string>(), CapturedMs = 300, SourceRevision = "group:AC2:300" };
        fixture.Catalog.PersistWirelessProjection(Project(Array.Empty<WirelessObservationSeed>(), new[] { sourceTwo }));
        Assert(Scalar(fixture.Db, "SELECT observed_at_ms FROM wireless_ap_groups WHERE group_identity_key IS NULL") == "300",
            "a Domain's maximum observation time cannot suppress a newer observation from its slower source");
        var equalTime = sourceTwo with { SourceRevision = "group:AC2:300:other-evidence" };
        fixture.Catalog.PersistWirelessProjection(Project(Array.Empty<WirelessObservationSeed>(), new[] { equalTime }));
        Assert(Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups WHERE group_identity_key IS NULL") == "2",
            "same-time revisions lack a safe ordering and must both remain candidates");
        var sourceOneOld = Evidence(seed, "AC1", null) with { SourceObservationIds = Array.Empty<string>(), CapturedMs = 400 };
        fixture.Catalog.PersistWirelessProjection(Project(Array.Empty<WirelessObservationSeed>(), new[] { sourceOneOld }));
        Assert(Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups WHERE group_identity_key IS NULL AND source_ac_device_id='AC1'") == "0",
            "explicit empty-group evidence supplies a watermark without fabricated AP observations");

        // Replacing an existing stable empty group with an older snapshot must
        // not erase the only source watermark and allow a later stale candidate.
        var staleShared = first with { CapturedMs = 100, SourceRevision = "group:AC1:100" };
        fixture.Catalog.PersistWirelessProjection(Project(Array.Empty<WirelessObservationSeed>(), new[] { staleShared, second }));
        fixture.Catalog.PersistWirelessProjection(Project(Array.Empty<WirelessObservationSeed>(), new[] { sourceOneOld }));
        Assert(Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups WHERE group_identity_key IS NULL AND source_ac_device_id='AC1'") == "0" &&
            Scalar(fixture.Db, "SELECT observed_at_ms FROM wireless_ap_groups WHERE group_identity_key='DOMAIN|domain-1|shared-key'") == "500",
            "older stable-group cache writes cannot regress per-source evidence watermarks");
        var newest = sourceTwo with { CapturedMs = 400, SourceRevision = "group:AC2:400" };
        fixture.Catalog.PersistWirelessProjection(Project(Array.Empty<WirelessObservationSeed>(), new[] { newest }));
        fixture.Catalog.PersistWirelessProjection(Project(Array.Empty<WirelessObservationSeed>(), new[] { equalTime }));
        Assert(Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups WHERE group_identity_key IS NULL") == "1" &&
            Scalar(fixture.Db, "SELECT observed_at_ms FROM wireless_ap_groups WHERE group_identity_key IS NULL") == "400",
            "empty-group candidate revisions compact safely and cannot be restored by a delayed snapshot");
    }

    private static void CandidateCacheCompactionRollsBackAtomically()
    {
        using var fixture = new GroupCacheFixture();
        fixture.Catalog.PersistWirelessProjection(Project(new[] { Ap("AC1", "before", 100) }));
        Execute(fixture.Db, "CREATE TRIGGER fail_group_cache BEFORE INSERT ON wireless_collection_manifests BEGIN SELECT RAISE(ABORT,'fixture cache publication failure'); END");
        var newer = Project(new[] { Ap("AC1", "after", 200) });
        try { fixture.Catalog.PersistWirelessProjection(newer); throw new InvalidOperationException("cache publication unexpectedly succeeded"); }
        catch (SqliteException) { }
        Assert(Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups") == "1" &&
            Scalar(fixture.Db, "SELECT display_name FROM wireless_ap_groups") == "before" &&
            Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_observations") == "1" &&
            Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_collection_manifests") == "1" &&
            Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_collection_manifest_items") == "1" &&
            Scalar(fixture.Db, "SELECT last_seen_ms FROM wireless_ap_resources") == "100",
            "a publication failure rolls back candidate deletion, candidate insertion, observations, resource updates and manifests together");
        Execute(fixture.Db, "DROP TRIGGER fail_group_cache");
        fixture.Catalog.PersistWirelessProjection(newer);
        Assert(Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_groups") == "1" &&
            Scalar(fixture.Db, "SELECT display_name FROM wireless_ap_groups") == "after" &&
            Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_ap_observations") == "2" &&
            Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_collection_manifests") == "2" &&
            Scalar(fixture.Db, "SELECT COUNT(*) FROM wireless_collection_manifest_items") == "2",
            "retry publishes the new candidate and compacts the old cache while retaining source history");
    }

    private sealed class GroupCacheFixture : IDisposable
    {
        private readonly string _tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        private readonly string _root;
        public Catalog Catalog { get; }
        public SqliteConnection Db { get; }

        public GroupCacheFixture()
        {
            _root = Path.Combine(_tempRoot, "next-group-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Catalog = new(new RuntimeSettings { DataRoot = _root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = System.Net.IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 });
            Catalog.Initialize();
            Db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "catalog.db"), Pooling = false }.ToString());
            Db.Open();
        }

        public void Dispose()
        {
            Db.Dispose();
            SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(_root).StartsWith(_tempRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("group cache fixture cleanup escaped its temporary root");
            Directory.Delete(_root, true);
        }
    }

    private static WirelessAcDeviceSeed Ac(string id, string site = "site-a") => new(id, id, site, "192.0.2.1", "H3C", "WX", "CONNECTED", null, "V7", true);
    private static WirelessObservationSeed Ap(string ac, string group, long captured, string name = "AP1", string serial = "SN-1", string mac = "4c6fd6080400") =>
        new(ac, ac, "site-a", "WX", "V7", name, serial, new[] { mac }, "WA", group, "R/M", "RUNNING_MASTER", "HIGH", captured,
            "fixture:ap:" + captured, "fixture.ap.v1", "ap-revision:" + captured);
    private static WirelessProjectionSnapshot Project(IReadOnlyList<WirelessObservationSeed> observations,
        IReadOnlyList<WirelessSourceGroupEvidence>? evidence = null, IReadOnlyList<WirelessTopologyEvidence>? topology = null) =>
        new WirelessProjectionEngine().Project(Acs, observations, topology ?? Topology, Full, evidence);
    private static WirelessProjectionSnapshot Reproject(WirelessProjectionSnapshot snapshot, IReadOnlyList<WirelessSourceGroupEvidence> evidence,
        IReadOnlyList<WirelessTopologyEvidence>? topology = null) => Project(snapshot.ApResources.SelectMany(resource => resource.Observations).Select(item =>
            new WirelessObservationSeed(item.SourceAcDeviceId, item.SourceAcDeviceId, item.SourceSiteId, "WX", "V7", item.ObservedApName, item.SerialEvidence,
                item.NormalizedMacSet, item.ApModel, item.ApGroup, item.RawState, item.NormalizedState, item.StateConfidence, item.CapturedMs,
                item.RawManifestRef, item.ParserRevision, item.SourceRevision)).ToArray(), evidence, topology);
    private static WirelessSourceGroupEvidence Evidence(WirelessProjectionSnapshot snapshot, string ac, string? key)
    {
        var observations = snapshot.ApResources.SelectMany(item => item.Observations).Where(item => item.SourceAcDeviceId == ac).ToArray();
        return new(ac, observations[0].SourceSiteId, key, observations.OrderByDescending(item => item.CapturedMs).First().ApGroup!,
            observations.Select(item => item.Id).ToArray(), observations.Max(item => item.CapturedMs), "group-revision:" + observations.Max(item => item.CapturedMs),
            "fixture:group:" + ac, "fixture.group.v1");
    }
    private static WirelessSourceGroupEvidence Shared(WirelessSourceGroupEvidence evidence) => evidence with
    { SourceAcDomainId = "domain-1", DomainMemberDeviceIds = new[] { "AC1", "AC2" }, DomainSharedEvidenceRefs = new[] { "fixture:domain-shared" } };
    private static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static string? Scalar(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar()?.ToString(); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
