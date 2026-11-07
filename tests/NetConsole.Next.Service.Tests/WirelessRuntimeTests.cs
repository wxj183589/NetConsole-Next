using NetConsole.Next.Service;

internal static class WirelessRuntimeTests
{
    public static void Run()
    {
        ChangesAreBoundedAndUnchangedSamplesOnlyRefreshCurrent();
        FailureUnknownAndEmptyPreserveEffectiveEvidence();
        StaleOfflineAndUnverifiedStatesAreDistinct();
        ConflictsDoNotBecomePreviousEffective();
        InputOrderAndDuplicateDeliveryDoNotAlterHistory();
        ScopeAndPhysicalIdentityCannotBeBypassed();
        FreshnessBoundaryAndClockSkewFailClosed();
        PartialHistoryNeverClaimsCompleteness();
        MultiAcAggregationRetainsIndependentHistories();
        PolicyIsBoundedAndRejectsInvalidConfiguration();
        OfflineReplayCannotReplaceLiveEvidence();
        ResourceHistoryRecordsEffectiveChangesAndKeepsProvenance();
        SimultaneousFailoverDoesNotInventIntermediateApChanges();
        AsynchronousSourcesRequireCompleteFreshRounds();
        Console.WriteLine("WIRELESS_RUNTIME_TESTS=14/14 PASS");
    }

    private static readonly WirelessRuntimePolicy Policy = new(10_000);
    private static readonly WirelessScopeView Scope = new("FULL", "fixture-claims");
    private static WirelessApResourceView Resource(string identity = "CONFIRMED")
    {
        var acs = new[] { new WirelessAcDeviceSeed("AC1", "AC1", "site-a", "192.0.2.1", "H3C", "WX", "CONNECTED", "profile1", "V7", true),
            new WirelessAcDeviceSeed("AC2", "AC2", "site-b", "192.0.2.2", "H3C", "WX", "CONNECTED", "profile2", "V7", true) };
        var observations = acs.Select(ac => new WirelessObservationSeed(ac.DeviceId, ac.DisplayName, ac.SiteId,
            "WX", "V7", "AP", "SN-ONE", new[] { "4c6f-d608-0400" }, "WA", "default", "R/M", "RUNNING_MASTER",
            "HIGH", 1000, "fact:fixture", "fixture.v1", "r1")).ToArray();
        return new WirelessProjectionEngine().Project(acs, observations, Array.Empty<WirelessTopologyEvidence>(), Scope)
            .ApResources.Single() with { IdentityStatus = identity };
    }

    private static WirelessRuntimeSample Sample(long time, string raw, string ac = "AC1", string outcome = "SUCCESS",
        string verification = "LIVE_VERIFIED", bool profile = true) =>
        new(ac, ac == "AC1" ? "site-a" : "site-b", "SN-ONE", new[] { "4c6f-d608-0400" }, raw, outcome,
            verification, profile, time, "r" + time, "fact:r" + time, "fixture.ap.v1");

    private static WirelessRuntimeStateView Project(IEnumerable<WirelessRuntimeSample> samples, long now = 20_000,
        bool truncated = false, string[]? incomplete = null, WirelessApResourceView? resource = null) =>
        new WirelessRuntimeStateProjection().Project(resource ?? Resource(), new(samples.ToArray(), truncated,
            new HashSet<string>(incomplete ?? Array.Empty<string>(), StringComparer.Ordinal)), Scope, now, Policy);

    private static void ChangesAreBoundedAndUnchangedSamplesOnlyRefreshCurrent()
    {
        var samples = Enumerable.Range(1, 13).SelectMany(index => new[]
        {
            Sample(index * 1000, index % 2 == 1 ? "R/M" : "R/B"),
            Sample(index * 1000 + 1, index % 2 == 1 ? "R/M" : "R/B")
        });
        var source = Project(samples).Sources.First();
        Assert(source.Current?.CapturedMs == 13_001 && source.Current.NormalizedState == "RUNNING_MASTER", "Current must refresh even for identical effective values");
        Assert(source.PreviousEffective?.CapturedMs == 12_001 && source.PreviousEffective.NormalizedState == "RUNNING_BACKUP", "PreviousEffective must reference the preceding valid value and its latest evidence");
        Assert(source.Recent10.Count == 10 && source.Recent10[0].CapturedMs == 13_000 && source.Recent10[^1].CapturedMs == 4_000,
            "Recent10 must contain exactly the last ten transitions, not ten poll samples or an invented initial transition");
    }

    private static void FailureUnknownAndEmptyPreserveEffectiveEvidence()
    {
        foreach (var outcome in new[] { "FAILED", "EMPTY", "PARTIAL", "UNSUPPORTED", "PARSE_FAILED" })
        {
            var source = Project(new[] { Sample(10_000, "R/M"), Sample(11_000, "R/B"), Sample(12_000, "", outcome: outcome) }).Sources.First();
            Assert(source.NormalizedState == "STALE" && source.Current?.NormalizedState == "RUNNING_BACKUP" && source.Current.CapturedMs == 11_000 &&
                source.PreviousEffective?.NormalizedState == "RUNNING_MASTER" && source.Recent10.Count == 1,
                "failed/empty/partial commands must not replace either effective value or append a transition");
        }
        var unknown = Project(new[] { Sample(10_000, "R/M"), Sample(11_000, "not-a-known-state") }).Sources.First();
        Assert(unknown.Current?.CapturedMs == 10_000 && unknown.Recent10.Count == 0 && unknown.LatestOutcome == "UNKNOWN", "unknown raw state cannot overwrite Current");
    }

    private static void StaleOfflineAndUnverifiedStatesAreDistinct()
    {
        Assert(Project(new[] { Sample(1_000, "R/M") }).Sources.First().NormalizedState == "STALE", "expired evidence must not appear running");
        var offline = Project(new[] { Sample(15_000, "I") }).Sources.First();
        Assert(offline.NormalizedState == "OFFLINE", "I requires verified state profile and fresh evidence");
        Assert(Project(new[] { Sample(15_000, "I", profile: false) }).Sources.First().Current is null, "unverified I cannot prove offline");
        var fixture = Project(new[] { Sample(15_000, "R/M", verification: "OFFLINE_FIXTURE") });
        Assert(fixture.Sources.First().NormalizedState == "STALE" && fixture.Warnings.Contains("OFFLINE_FIXTURE_EVIDENCE"), "offline replay must remain labeled historical evidence");
    }

    private static void ConflictsDoNotBecomePreviousEffective()
    {
        var source = Project(new[] { Sample(10_000, "R/M"), Sample(11_000, "R/B"), Sample(11_000, "I") }).Sources.First();
        Assert(source.NormalizedState == "CONFLICT" && source.Current?.NormalizedState == "RUNNING_MASTER" && source.PreviousEffective is null && source.Recent10.Count == 0,
            "conflicting revisions must retain candidates without poisoning effective history");
        Assert(Project(new[] { Sample(11_000, "R/B"), Sample(11_000, "I") }).Sources.First().NormalizedState == "CONFLICT", "conflict is visible even without previous valid evidence");
    }

    private static void InputOrderAndDuplicateDeliveryDoNotAlterHistory()
    {
        var ordered = new[] { Sample(10_000, "R/M"), Sample(11_000, "R/B"), Sample(12_000, "I") };
        var a = Project(ordered);
        var b = Project(ordered.Reverse().Concat(ordered));
        Assert(a.SourceRevision == b.SourceRevision && a.Sources.First().Recent10.Count == 2, "late/out-of-order arrival and duplicate import must yield the same projection");
    }

    private static void ScopeAndPhysicalIdentityCannotBeBypassed()
    {
        var resource = Resource();
        resource = resource with { Observations = resource.Observations.Where(item => item.SourceAcDeviceId == "AC1").ToArray() };
        var result = Project(new[] { Sample(15_000, "R/M"), Sample(15_000, "R/B", "AC2"),
            Sample(16_000, "I") with { SerialEvidence = "replacement" }, Sample(17_000, "I") with { SourceSiteId = "site-b" } }, resource: resource);
        Assert(result.Sources.Count == 1 && result.Sources.Single().Current?.CapturedMs == 15_000 && result.NormalizedState == "RUNNING_MASTER", "hidden sources, mismatched Sites and replacement APs cannot alter visible state/history/revision");
        Assert(Project(new[] { Sample(15_000, "R/M") }, resource: Resource("CONFLICT")).Sources.Count == 0, "identity conflicts cannot receive physical AP runtime history");
    }

    private static void FreshnessBoundaryAndClockSkewFailClosed()
    {
        Assert(Project(new[] { Sample(10_000, "R/M") }, now: 19_999).Sources.First().NormalizedState == "RUNNING_MASTER", "freshness window is explicit");
        Assert(Project(new[] { Sample(10_000, "R/M") }, now: 20_000).Sources.First().NormalizedState == "STALE", "deadline equality must expire the observation");
        var future = Project(new[] { Sample(15_000, "R/M"), Sample(30_000, "I") }).Sources.First();
        Assert(future.Current?.CapturedMs == 15_000 && future.NormalizedState == "UNKNOWN" && future.LatestOutcome == "CLOCK_SKEW", "future time must not extend online evidence or manufacture offline");
    }

    private static void PartialHistoryNeverClaimsCompleteness()
    {
        var limited = Project(new[] { Sample(15_000, "R/M") }, truncated: true);
        Assert(limited.Sources.First().HistoryStatus == "PARTIAL_SCAN_LIMIT" && limited.Warnings.Contains("RUNTIME_HISTORY_INCOMPLETE"), "bounded reads must disclose missing history");
        Assert(Project(new[] { Sample(15_000, "R/M") }, incomplete: new[] { "AC1" }).Sources.First().HistoryStatus == "PARTIAL_EVIDENCE", "missing exact identity evidence cannot be guessed from AP name");
    }

    private static void MultiAcAggregationRetainsIndependentHistories()
    {
        var normal = Project(new[] { Sample(15_000, "R/M"), Sample(15_001, "R/B", "AC2") });
        Assert(normal.NormalizedState == "RUNNING_MASTER" && normal.Sources.Count == 2 && normal.Sources.All(item => item.Recent10.Count == 0), "master/backup observations remain independent");
        var conflict = Project(new[] { Sample(15_000, "R/M"), Sample(15_001, "R/M", "AC2") });
        Assert(conflict.NormalizedState == "CONFLICT" && conflict.Warnings.Contains("MULTI_SOURCE_MASTER"), "two active masters must not be resolved by timestamp");
    }

    private static void PolicyIsBoundedAndRejectsInvalidConfiguration()
    {
        var original = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FRESHNESS_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FRESHNESS_SECONDS", "60");
            Assert(WirelessRuntimePolicy.FromEnvironment().FreshnessWindowMs == 60_000, "freshness policy must be explicit and inspectable");
            foreach (var value in new[] { "0", "-1", "86401", "invalid" })
            {
                Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FRESHNESS_SECONDS", value);
                try { WirelessRuntimePolicy.FromEnvironment(); throw new InvalidOperationException("invalid policy accepted"); }
                catch (InvalidDataException) { }
            }
        }
        finally { Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FRESHNESS_SECONDS", original); }
    }

    private static void OfflineReplayCannotReplaceLiveEvidence()
    {
        var result = Project(new[] { Sample(15_000, "R/M"), Sample(16_000, "I", verification: "OFFLINE_FIXTURE"),
            Sample(17_000, "", outcome: "FAILED", verification: "OFFLINE_FIXTURE") });
        Assert(result.Sources.First().Current?.CapturedMs == 15_000 && result.Sources.First().NormalizedState == "RUNNING_MASTER" &&
            result.Sources.First().Recent10.Count == 0 && result.Warnings.Contains("OFFLINE_FIXTURE_EXCLUDED_FROM_LIVE_CURRENT"),
            "offline replay cannot poison a verified live Current, its previous value or its effective-change history");
    }

    private static void ResourceHistoryRecordsEffectiveChangesAndKeepsProvenance()
    {
        var resource = Resource();
        resource = resource with { Observations = resource.Observations.Where(item => item.SourceAcDeviceId == "AC1").ToArray() };
        var samples = Enumerable.Range(1, 13).Select(index => Sample(index * 1000, index % 2 == 1 ? "R/M" : "R/B"));
        var state = Project(samples, resource: resource);
        Assert(state.Current?.NormalizedState == "RUNNING_MASTER" && state.PreviousEffective?.NormalizedState == "RUNNING_BACKUP" &&
            state.Recent10?.Count == 10 && state.Recent10[0].CapturedMs == 13_000 && state.Recent10[^1].CapturedMs == 4_000 &&
            state.Recent10.All(item => item.Sources.Single().SourceAcDeviceId == "AC1"), "AP-level Current, PreviousEffective and Recent10 must retain exact visible source provenance");
        var failed = Project(new[] { Sample(15_000, "R/M"), Sample(16_000, "", outcome: "FAILED") }, resource: resource);
        Assert(failed.Current?.CapturedMs == 15_000 && failed.Current.FreshnessState == "STALE" && failed.Recent10?.Count == 0,
            "AP-level failure must not become effective Current or a synthetic history transition");
    }

    private static void SimultaneousFailoverDoesNotInventIntermediateApChanges()
    {
        var state = Project(new[] { Sample(10_000, "R/M"), Sample(10_000, "R/B", "AC2"),
            Sample(11_000, "R/B"), Sample(11_000, "R/M", "AC2") });
        Assert(state.Current?.NormalizedState == "RUNNING_MASTER" && state.Current.Sources.Count == 2 &&
            state.PreviousEffective is null && state.Recent10?.Count == 0 && state.Sources.All(item => item.Recent10.Count == 1),
            "same-time HA source role changes do not change the physical AP's effective state or create order-dependent transitions");
        var conflict = Project(new[] { Sample(15_000, "R/M"), Sample(15_000, "R/M", "AC2") });
        Assert(conflict.Current is null && conflict.NormalizedState == "CONFLICT", "initial dual-master conflict cannot manufacture an effective AP Current");
    }

    private static void AsynchronousSourcesRequireCompleteFreshRounds()
    {
        var samples = new[] { Sample(1_000, "R/M"), Sample(1_060, "R/B", "AC2"),
            Sample(15_000, "R/B", "AC2"), Sample(15_060, "R/M"),
            Sample(16_000, "R/B"), Sample(16_060, "R/M", "AC2") };
        var state = Project(samples, now: 17_000);
        Assert(state.Current?.NormalizedState == "RUNNING_MASTER" && state.Current.CapturedMs == 16_060 &&
            state.Recent10?.Count == 0 && state.Sources.All(item => item.Recent10.Count == 1),
            "asynchronous source updates and expired earlier rounds must not create aggregate failovers");
        var partial = Project(samples.Take(3), now: 16_000);
        Assert(partial.Current?.CapturedMs == 1_060 && partial.Recent10?.Count == 0,
            "a partial round must preserve the previous aggregate evidence");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
