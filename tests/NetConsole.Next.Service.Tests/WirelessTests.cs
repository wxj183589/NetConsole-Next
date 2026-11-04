using NetConsole.Next.Service;

internal static class WirelessTests
{
    public static void Run()
    {
        ProjectsSamePhysicalApAcrossDualAcWithoutDuplicateIdentity();
        PreservesSerialMacConflictWithoutAutoMerge();
        DoesNotUseApNameAsIdentity();
        KeepsSameNamedGroupsAcScoped();
        RejectsMalformedMacEvidence();
        RejectsMismatchedSourceSite();
        CollectionFailureIsNotOffline();
        RepeatedObservationIsIdempotent();
        KeepsIdentityWhenNameMissing();
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
