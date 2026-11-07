using NetConsole.Next.Service;

internal static class OperationalHealthTests
{
    private const long Now = 2_000_000;
    public static async Task RunAsync()
    {
        MissingEvidenceIsUnknown();
        OnlyFreshVerifiedEvidenceCounts();
        InterfaceAndStaleEvidence();
        await TransientFailureRecovers();
        await AuthenticationDoesNotRetry();
        await RetryBoundAndRevocation();
        await CancellationStopsRetries();
        Console.WriteLine("OPERATIONAL_HEALTH_TESTS=7/7 PASS");
    }
    private static DeviceInventoryItem Device() => new("device-a", "device a", "192.0.2.10", 22,
        "fixture", "H3C", "fixture", "SW", "SSH", "site-a", "", true, "DISCONNECTED", Now,
        true, "REJECT", "UTF-8", "fixture failure", true, [], SiteId: "site-a", TerminalProfileId: "profile-a");
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
