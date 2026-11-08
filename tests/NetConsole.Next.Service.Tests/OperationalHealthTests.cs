using NetConsole.Next.Service;

internal static class OperationalHealthTests
{
    private const long Now = 2_000_000;
    public static async Task RunAsync()
    {
        MissingEvidenceIsUnknown();
        CancelledTaskIsNotFault();
        WirelessFaultScanRotatesBeyondFirstPage();
        OnlyFreshVerifiedEvidenceCounts();
        InterfaceAndStaleEvidence();
        RouteReachedPreservesEvidence();
        RouteRequiresExactResource();
        RouteRequiresFreshTerminalEvidence();
        RouteRequiresHopObservations();
        RouteNonBusinessFailuresStayUnknown();
        RouteExplicitFailuresAreObserved();
        LldpLossRequiresCompletePartitionEvidence();
        CompleteEmptyLldpIsObservedZero();
        await TransientFailureRecovers();
        await AuthenticationDoesNotRetry();
        await RetryBoundAndRevocation();
        await CancellationStopsRetries();
        Console.WriteLine("OPERATIONAL_HEALTH_TESTS=17/17 PASS");
    }
    private static void CancelledTaskIsNotFault()
    {
        Assert(!OperationalHealthProjection.IsCollectionFailure("CANCELLED", "PARTIAL"),
            "user-cancelled collection remains task history and does not become a fault warning");
        Assert(OperationalHealthProjection.IsCollectionFailure("FAILED", "FAILED") &&
            OperationalHealthProjection.IsCollectionFailure("TIMED_OUT", "PARTIAL") &&
            OperationalHealthProjection.IsCollectionFailure("PARTIAL", "PARTIAL"),
            "failed, timed-out and incomplete collection outcomes remain fault evidence");
        Assert(!OperationalHealthProjection.IsCollectionFailure("SUCCEEDED", "COMPLETE") &&
            !OperationalHealthProjection.IsCollectionFailure("RUNNING", "PENDING"),
            "successful and active collection work does not become a fault");
    }
    private static void WirelessFaultScanRotatesBeyondFirstPage()
    {
        var ids = Enumerable.Range(0, 23).Select(index => $"ap-{index:D2}").ToArray();
        var first = OperationalHealthProjection.SelectWirelessFaultResourceIds(ids, 0);
        var second = OperationalHealthProjection.SelectWirelessFaultResourceIds(ids, 10);
        var third = OperationalHealthProjection.SelectWirelessFaultResourceIds(ids, 20);
        Assert(first.Count == 10 && second.Count == 10 && third.Count == 10 &&
            first.Concat(second).Concat(third).Distinct(StringComparer.Ordinal).Count() == ids.Length,
            "consecutive bounded scans must eventually cover every authorized AP beyond the initial ten");
        Assert(OperationalHealthProjection.SelectWirelessFaultResourceIds(ids, 20).SequenceEqual(third),
            "scan selection is deterministic for a given cursor");
        Assert(OperationalHealthProjection.SelectWirelessFaultResourceIds(ids, -1).SequenceEqual(ids.TakeLast(1).Concat(ids.Take(9))),
            "negative cursor normalization wraps without skipping a resource");
        Assert(OperationalHealthProjection.SelectWirelessFaultResourceIds(ids, 0, 0).Count == 0 &&
            OperationalHealthProjection.SelectWirelessFaultResourceIds([], 0).Count == 0,
            "empty resources and nonpositive budgets remain bounded");
    }
    private static void CompleteEmptyLldpIsObservedZero()
    {
        var table = new H3cFactSnapshot("empty-table", "profile-a", "lldp.table", "scope-a", "STALE", "H3C_TASK_RAW:empty",
            Now, true, "{\"status\":\"PARSED\",\"neighbors\":[]}") { CollectedRawVerified = true, CapturedScopeKey = "scope-a" };
        var metric = OperationalHealthProjection.Device(Device(), [table], Now).LldpNeighbors;
        Assert(metric.State == "OBSERVED" && metric.Value == 0 && metric.EvidenceIds.SequenceEqual([table.Id]),
            "fresh complete empty table proves an observed zero with exact evidence");
        foreach (var rejected in new[] { table with { Status = "PARTIAL" }, table with { CollectedRawVerified = false },
            table with { CapturedMs = Now - 300001 }, table with { PayloadJson = "{bad" } })
            Assert(OperationalHealthProjection.Device(Device(), [rejected], Now).LldpNeighbors.Value is null,
                "partial, unverified, stale and malformed empty tables remain unknown");
    }
    private static void LldpLossRequiresCompletePartitionEvidence()
    {
        var prior = new H3cFactSnapshot("neighbor", "profile-a", "lldp.neighbor", "GE1|GE2|AA", "STALE",
            "H3C_TASK_RAW:old", Now - 10, false, "{}") { CollectedRawVerified = true, CapturedScopeKey = "scope-a" };
        var table = prior with { Id = "table", Kind = "lldp.table", IdentityKey = "table", Source = "H3C_TASK_RAW:new",
            CapturedMs = Now, IsCurrent = true, PayloadJson = "{\"status\":\"PARSED\",\"neighbors\":[]}" };
        var lost = OperationalHealthProjection.LostLldpNeighbors(Device(), [prior, prior with { Id = "older", CapturedMs = Now - 20 }], [table], Now);
        Assert(lost.Count == 1 && lost[0].Kind == "LLDP_NEIGHBOR_LOST" && lost[0].EvidenceId == "table" &&
            lost[0].ResourceKey == prior.IdentityKey && lost[0].SourceAuthority == "H3C_TASK_RAW" && lost[0].CapturedScopeKey == "scope-a",
            "complete empty table proves one lost exact neighbor, with authority, capture partition and table evidence");
        foreach (var rejected in new[] {
            table with { CapturedMs = Now + 1 }, table with { CapturedMs = Now - 300001 },
            table with { Source = "OTHER" }, table with { CapturedScopeKey = "scope-b" },
            table with { CapturedScopeKey = "UNSCOPED" }, table with { ProfileId = "profile-b" },
            table with { IsCurrent = false }, table with { CollectedRawVerified = false },
            table with { Status = "PARTIAL" }, table with { PayloadJson = "{bad" },
            table with { PayloadJson = "{\"status\":\"PARTIAL\",\"neighbors\":[]}" },
            table with { PayloadJson = "{\"status\":\"PARSED\",\"neighbors\":[null]}" }
        }) Assert(OperationalHealthProjection.LostLldpNeighbors(Device(), [prior], [rejected], Now).Count == 0,
            "incomplete, stale, future, foreign, unscoped and malformed tables cannot imply loss");
        Assert(OperationalHealthProjection.LostLldpNeighbors(Device(), [prior with { CollectedRawVerified = false }], [table], Now).Count == 0,
            "unverified historical neighbor cannot create a loss");
        var restored = table with { PayloadJson = "{\"status\":\"PARSED\",\"neighbors\":[{\"localInterface\":\"GE1\",\"neighborInterface\":\"GE2\",\"chassisId\":\"AA\"}]}" };
        Assert(OperationalHealthProjection.LostLldpNeighbors(Device(), [prior], [restored], Now).Count == 0,
            "present exact neighbor suppresses loss; names never substitute identity");
    }
    private static DeviceInventoryItem Device() => new("device-a", "device a", "192.0.2.10", 22,
        "fixture", "H3C", "fixture", "SW", "SSH", "site-a", "", true, "DISCONNECTED", Now,
        true, "REJECT", "UTF-8", "fixture failure", true, [], ManagementIp: "192.0.2.10", SiteId: "site-a", TerminalProfileId: "profile-a");
    private static H3cFactSnapshot Fact(string id, long at, bool verified = true,
        string json = "{\"linkStatus\":\"DOWN\"}", string status = "LIVE_VERIFIED") =>
        new(id, "profile-a", "interface", id, status, "H3C_SSH_READONLY", at, true, json) { CollectedRawVerified = verified };
    private static void MissingEvidenceIsUnknown()
    {
        var result = OperationalHealthProjection.Device(Device(), [], Now);
        Assert(result.State == "UNKNOWN" && result.Reachability.Value is null && result.InterfaceDown.Value is null,
            "disconnected terminal and failed capture must not imply device offline or zero interfaces down");
        Assert(result.CpuPercent.State == "UNKNOWN" && result.MemoryPercent.Value is null, "unsupported numerical metrics stay unknown");
    }
    private static void OnlyFreshVerifiedEvidenceCounts()
    {
        var result = OperationalHealthProjection.Device(Device(),
            [Fact("fixture", Now, false), Fact("future", Now + 1), Fact("partial", Now, status: "PARTIAL")], Now);
        Assert(result.State == "UNKNOWN" && result.InterfaceDown.EvidenceIds.Count == 0,
            "fixture, future and partial samples cannot make device health live");
        var live = OperationalHealthProjection.Device(Device(), [Fact("live", Now - 100)], Now);
        Assert(live.State == "ATTENTION" && live.Reachability.State == "OBSERVED" &&
            live.InterfaceDown.Value == 1 && live.InterfaceDown.EvidenceIds.Single() == "live", "verified fresh down link has explicit evidence");
        var compatibleStatus = OperationalHealthProjection.Device(Device(), [Fact("raw", Now, status: "STALE")], Now);
        Assert(compatibleStatus.InterfaceDown.Value == 1 && compatibleStatus.Reachability.State == "OBSERVED",
            "legacy STALE catalog status must use Raw verification and capture age for live freshness");
    }
    private static void InterfaceAndStaleEvidence()
    {
        var stale = OperationalHealthProjection.Device(Device(), [Fact("old", Now - 300001)], Now);
        Assert(stale.State == "UNKNOWN" && stale.Warnings.Contains("VERIFIED_EVIDENCE_STALE"), "stale evidence does not prove current state");
        var malformed = OperationalHealthProjection.Device(Device(), [Fact("bad", Now, json: "{bad")], Now);
        Assert(malformed.InterfaceDown.State == "UNKNOWN", "malformed payload must not synthesize a healthy interface total");
        var link = OperationalHealthProjection.Device(Device(), [Fact("up", Now, json: "{\"linkStatus\":\"UP\"}")], Now);
        Assert(link.InterfaceDown.Value == 0 && link.InterfaceDown.State == "OBSERVED", "zero means observed known interfaces only");
    }
    private static NetworkDiagnosticItemSnapshot Route() => new("route-item", "device-a", "device a", "site-a",
        "TRACEROUTE", "SUCCEEDED", "OK", null, Now - 100, Now, 5, 2, ["ICMP_TRACEROUTE"],
        new NetworkTracerouteSnapshot("192.0.2.10", true, 30,
            [new(1, "192.0.2.1", "TTL_EXPIRED", 2), new(2, "192.0.2.10", "REACHED", 3)]));
    private static void AssertUnknownRoute(NetworkDiagnosticItemSnapshot item, string message)
    {
        var metric = OperationalHealthProjection.RouteMetric(item, "route-run", Device(), Now);
        Assert(metric.State == "UNKNOWN" && metric.Value is null && metric.Unit == "route" &&
            metric.CapturedMs is null && metric.EvidenceIds.Count == 0 && metric.ResourceKey is null, message);
    }
    private static void RouteReachedPreservesEvidence()
    {
        var metric = OperationalHealthProjection.RouteMetric(Route() with { EndedMs = Now - 5 }, "route-run", Device(), Now);
        Assert(metric.State == "OBSERVED" && metric.Value == 1 && metric.Unit == "route" && metric.CapturedMs == Now - 5 &&
            metric.EvidenceIds.SequenceEqual(["route-run"]) && metric.ResourceKey == "[\"TRACEROUTE\",\"192.0.2.10\",null]",
            "fresh successful destination arrival must yield one and preserve the exact run, capture time and route endpoint");
    }
    private static void RouteRequiresExactResource()
    {
        var valid = Route();
        foreach (var mismatched in new[]
        {
            valid with { DeviceId = "device-b" },
            valid with { DeviceId = "DEVICE-A" },
            valid with { SiteId = "site-b" },
            valid with { SiteId = "" },
            valid with { TraceRoute = valid.TraceRoute! with { DestinationAddress = "192.0.2.11" } },
            valid with { TraceRoute = valid.TraceRoute! with { DestinationAddress = "192.0.2.010" } },
            valid with { Kind = "PING" }
        }) AssertUnknownRoute(mismatched, "device display name, a different Site, endpoint aliases or another probe kind cannot bind route evidence to this resource");
    }
    private static void RouteRequiresFreshTerminalEvidence()
    {
        var valid = Route();
        foreach (var at in new[] { Now, Now - OperationalHealthProjection.FreshnessWindowMs })
        {
            var observed = OperationalHealthProjection.RouteMetric(valid with { EndedMs = at }, "route-run", Device(), Now);
            Assert(observed.State == "OBSERVED" && observed.Value == 1 && observed.CapturedMs == at,
                "capture now and the exact inclusive freshness boundary remain usable");
        }
        foreach (var invalid in new[]
        {
            valid with { EndedMs = null },
            valid with { EndedMs = Now + 1 },
            valid with { EndedMs = Now - OperationalHealthProjection.FreshnessWindowMs - 1 },
            valid with { State = "QUEUED" },
            valid with { State = "RUNNING" },
            valid with { State = "CANCELLED" },
            valid with { State = "UNKNOWN" }
        }) AssertUnknownRoute(invalid, "missing, future, stale or unfinished/cancelled route evidence must not produce a current value");
    }
    private static void RouteRequiresHopObservations()
    {
        var valid = Route();
        AssertUnknownRoute(valid with { TraceRoute = null }, "success metadata without a route snapshot is not route evidence");
        AssertUnknownRoute(valid with { TraceRoute = valid.TraceRoute! with { Hops = [] }, EvidenceCount = 100,
            EvidenceSources = ["ICMP_TRACEROUTE", "PREFLIGHT_OK"] },
            "nonzero evidence counters and preflight metadata cannot replace observed hops even when marked reached");
        AssertUnknownRoute(valid with { State = "FAILED", Outcome = "TRACEROUTE_UNREACHABLE",
            TraceRoute = valid.TraceRoute! with { DestinationReached = false, Hops = [] } },
            "unreachable without any hop observation cannot create a route fault");
    }
    private static void RouteNonBusinessFailuresStayUnknown()
    {
        var partial = Route() with { State = "FAILED", TraceRoute = new NetworkTracerouteSnapshot("192.0.2.10", false, 30,
            [new(1, "192.0.2.1", "TTL_EXPIRED", 2)]) };
        foreach (var outcome in new string?[] { "ICMP_PERMISSION_DENIED", "ICMP_UNAVAILABLE", "CANCELLED", "TIMEOUT", "DNS_FAILED", "UNKNOWN", null })
            AssertUnknownRoute(partial with { Outcome = outcome },
                "permission, cancellation, tool availability and unknown probe failures with partial hops must not imply a business route outage");
        AssertUnknownRoute(Route() with { TraceRoute = partial.TraceRoute }, "OK without destination arrival is not observed success");
        AssertUnknownRoute(Route() with { Outcome = "TRACEROUTE_UNREACHABLE" }, "a success state and failure outcome conflict must remain unknown");
        AssertUnknownRoute(partial with { Outcome = "TRACEROUTE_UNREACHABLE", TraceRoute = Route().TraceRoute },
            "a reached destination and unreachable outcome conflict must not synthesize a fault");
    }
    private static void RouteExplicitFailuresAreObserved()
    {
        foreach (var outcome in new[] { "TRACEROUTE_UNREACHABLE", "TRACEROUTE_MAX_HOPS" })
        {
            IReadOnlyList<NetworkDiagnosticHopSnapshot> hops = outcome == "TRACEROUTE_UNREACHABLE"
                ? [new(1, "192.0.2.1", "UNREACHABLE", 4)]
                : Enumerable.Range(1, 30).Select(ttl => new NetworkDiagnosticHopSnapshot(ttl, null, "TIMED_OUT", null)).ToArray();
            var failed = Route() with { State = "FAILED", Outcome = outcome, EndedMs = Now - 10,
                TraceRoute = new NetworkTracerouteSnapshot("192.0.2.10", false, 30, hops) };
            var metric = OperationalHealthProjection.RouteMetric(failed, "failed-route-run", Device(), Now);
            Assert(metric.State == "OBSERVED" && metric.Value == 0 && metric.CapturedMs == Now - 10 &&
                metric.EvidenceIds.SequenceEqual(["failed-route-run"]) &&
                metric.ResourceKey == "[\"TRACEROUTE\",\"192.0.2.10\",null]",
                "explicit unreachable or exhausted max-hop results with probe observations must yield zero for that endpoint only");
        }
    }
    private static async Task TransientFailureRecovers()
    {
        var calls = 0; var guards = 0;
        var attempts = await OperationalRecoveryRunner.ProbeAsync(() => Task.FromResult(++calls < 2 ? "CONNECT_TIMEOUT" : "CONNECTED"),
            () => guards++, CancellationToken.None, NoDelay);
        Assert(calls == 2 && guards == 2 && attempts.Count == 2 && attempts[0].Status == "CONNECT_TIMEOUT",
            "transient retry preserves first failure and checks authority on every attempt");
    }
    private static async Task AuthenticationDoesNotRetry()
    {
        foreach (var status in new[] { "AUTHENTICATION_FAILED", "CREDENTIAL_UNAVAILABLE", "HOST_KEY_REJECTED", "PROTOCOL_MISMATCH" })
        {
            var calls = 0;
            var attempts = await OperationalRecoveryRunner.ProbeAsync(() => { calls++; return Task.FromResult(status); },
                () => { }, CancellationToken.None, NoDelay);
            Assert(calls == 1 && attempts.Count == 1, "security and configuration failures must not cause repeated login attempts");
        }
    }
    private static async Task RetryBoundAndRevocation()
    {
        var calls = 0;
        var attempts = await OperationalRecoveryRunner.ProbeAsync(() => { calls++; return Task.FromResult("UNREACHABLE"); },
            () => { }, CancellationToken.None, NoDelay);
        Assert(calls == 3 && attempts.Count == 3, "retry budget is fixed at three");
        calls = 0; var guards = 0;
        try
        {
            await OperationalRecoveryRunner.ProbeAsync(() => { calls++; return Task.FromResult("CONNECT_TIMEOUT"); },
                () => { if (++guards == 2) throw new TaskScopeRevokedException("TEST_ENDPOINT_CHANGED"); }, CancellationToken.None, NoDelay);
            throw new Exception("revocation was ignored");
        }
        catch (TaskScopeRevokedException) { Assert(calls == 1, "endpoint/scope change must stop before the next connection"); }
    }
    private static async Task CancellationStopsRetries()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        try
        {
            await OperationalRecoveryRunner.ProbeAsync(() => { calls++; cancellation.Cancel(); return Task.FromResult("UNREACHABLE"); },
                () => { }, cancellation.Token, (_, token) => Task.Delay(1, token));
            throw new Exception("cancellation was ignored");
        }
        catch (OperationCanceledException) { Assert(calls == 1, "cancelled recovery must not continue attempts"); }
    }
    private static Task NoDelay(int _, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
