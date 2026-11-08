using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Text.Json;

internal static class H3cLldpTopologyTests
{
    public static void Run()
    {
        ProjectionClassifiesHistoryWithoutDeletes();
        RelationHistoryIsScopedToLocalDeviceAndSite();
        OfflineFixtureProjectsScopedRelation();
        SimulatorSwitchListRetainsColumnsAndRejectsLegends();
        LegacyApBannerReplayRetainsEndpointsWithoutCompleteTableProof();
        ActualAcRadioTablePreservesDownAndZeroClients();
        HistoricalProjectionRejectsLegendsAndInvalidAddresses();
        Console.WriteLine("H3C_LLDP_TOPOLOGY_TESTS=7/7 PASS");
    }

    private static void HistoricalProjectionRejectsLegendsAndInvalidAddresses()
    {
        const string profile = "profile-old-parser";
        H3cFactSnapshot Old(string id, string local, string? neighbor, string? chassis, string? address) =>
            new(id, profile, "lldp.neighbor", id, "STALE", "H3C_TASK_RAW:historical", 1000, true,
                JsonSerializer.Serialize(new { localInterface = local, neighborSysname = neighbor,
                    neighborInterface = "GE1/0/1", chassisId = chassis, managementAddress = address,
                    capturedScope = new { profileId = profile, deviceId = "device-a", siteId = "site-a" } }));
        var facts = new[] {
            Old("legend-chassis", "Chassis", "ID", null, "* -- -- Nearest nontpmr bridge neighbor"),
            Old("legend-default", "Default", "--", null, "Nearest bridge neighbor"),
            Old("old-name-in-ip", "GE1/0/1", "4c48-8d10-0100", null, "AC1"),
            Old("empty-neighbor", "GE1/0/2", "--", null, "--"),
            Old("ipv6", "GE1/0/3", "--", "4c48-cb6e-0400", "2001:db8::4"),
            Old("ipv4-only", "GE1/0/4", null, null, "192.0.2.4"),
            Old("host-only", "GE1/0/5", null, null, "hostname-is-not-management-ip") };
        var rows = H3cLldpTopologyProjection.Project(profile, facts);
        Assert(rows.Count == 3 && rows.All(row => row.LocalInterface.StartsWith("GE", StringComparison.Ordinal)),
            "persisted legend rows and placeholder-only identities cannot become asset links");
        Assert(rows.Single(row => row.FactRevision == "old-name-in-ip").NeighborManagementAddress is null,
            "historical system names in the IP column must display unknown rather than an invented address");
        Assert(rows.Single(row => row.FactRevision == "ipv6") is {
            NeighborManagementAddress: "2001:db8::4", NeighborDevice: "4c48-cb6e-0400" } &&
            rows.Single(row => row.FactRevision == "ipv4-only").NeighborDevice == "192.0.2.4",
            "valid IPv4/IPv6 and chassis fallback retain captured provenance");
        Assert(facts[2].PayloadJson.Contains("AC1", StringComparison.Ordinal) && facts.Length == 7,
            "projection filtering does not rewrite stored facts or historical evidence");
    }

    private static void ActualAcRadioTablePreservesDownAndZeroClients()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "r72_simulator_ac_radio.txt"));
        var rows = H3cComwareParser.ParseWlanApAllRadio(output);
        Assert(rows.Count == 2 && rows.All(row => row.State == "Down" && row.Clients == 0 && row.UsagePercent == 0) &&
            rows[0].RadioId == 1 && rows[0].Channel == 36 && rows[0].RawChannel == "36(auto)" &&
            rows[0].Bandwidth == "80" && rows[0].TxPower == 20 && rows[1].Channel == 11,
            "real AC RID/state table must retain Down, zero clients, auto channel and correct power columns");
        Assert(H3cComwareParser.ParseWlanApAllRadio(output + "\n-- More --").Count == 0 &&
            H3cComwareParser.ParseWlanApAllRadio("Permission denied").Count == 0,
            "paged/denied Radio evidence cannot turn into usable rows");
        using var fixture = new Fixture();
        var profile = new H3cDeviceProfileSnapshot(Guid.NewGuid().ToString("N"), "192.0.2.31", 22,
            "fixture", "FIXTURE_REFERENCE", "", true, 0, 0);
        fixture.Catalog.CreateH3cDeviceProfile(profile);
        fixture.Catalog.ImportH3cOfflineReplay(profile.Id,
            new Dictionary<string, string> { [H3cCommandPolicy.DisplayWlanApAllRadio] = output }, 1000);
        var facts = fixture.Catalog.QueryH3cFacts(profile.Id, "ap.radio");
        Assert(facts.Count == 2 && facts.All(fact => !fact.CollectedRawVerified) &&
            facts.All(fact => JsonDocument.Parse(fact.PayloadJson).RootElement.GetProperty("state").GetString() == "Down"),
            "offline Radio replay publishes exact scoped radio facts without manufacturing live verification");
    }

    private static void LegacyApBannerReplayRetainsEndpointsWithoutCompleteTableProof()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "live_ap_v7_229",
            "display_lldp_neighbor_information_list.txt"));
        var rows = H3cComwareParser.ParseLldpList(output);
        Assert(rows.Count == 1 && rows[0].LocalInterface == "GE1/0/1" && rows[0].NeighborInterface == "ge2" &&
            rows[0].NeighborSysname == "Switch" && rows[0].ChassisId == "00aa-bbcc-ddee",
            "Phase11 banner-bearing offline capture must retain exact local/remote endpoints and chassis");
        Assert(H3cComwareParser.ParseLldpListTable(output, completedExec: true).Status == "PARTIAL",
            "offline extraction without terminal completion proof must not authorize product table replacement");
        using var fixture = new Fixture();
        var profile = new H3cDeviceProfileSnapshot(Guid.NewGuid().ToString("N"), "192.0.2.229", 22,
            "fixture", "FIXTURE_REFERENCE", string.Empty, true, 0, 0);
        fixture.Catalog.CreateH3cDeviceProfile(profile);
        fixture.Catalog.ImportH3cOfflineReplay(profile.Id,
            new Dictionary<string, string> { [H3cCommandPolicy.DisplayLldpNeighborInformationList] = output }, 1000);
        Assert(fixture.Catalog.QueryH3cLldpRelations(profile.Id, false, 100, P2B5SiteScopeDecision.Global).Single().NeighborPort == "ge2" &&
            fixture.Catalog.QueryH3cFacts(profile.Id, "lldp.table").Count == 0,
            "offline replay must preserve the known relationship without creating a collected table authority");
    }

    private static void SimulatorSwitchListRetainsColumnsAndRejectsLegends()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "r71_simulator_sw_lldp_list.txt"));
        var table = H3cComwareParser.ParseLldpListTable(output, completedExec: true);
        Assert(table.Status == "PARSED" && table.Neighbors.Count == 3,
            "the real SW list must not turn Chassis/Default legend rows into neighbors");
        Assert(table.Neighbors[0] is { LocalInterface: "GE1/0/1", NeighborSysname: "AC1",
            NeighborInterface: "GigabitEthernet1/0/1", ChassisId: "4c48-8d10-0100", ManagementAddress: null } &&
            table.Neighbors[1].NeighborSysname == "4c48-cb6e-0400" && table.Neighbors[2].NeighborSysname == "ac2",
            "local-first chassis columns must preserve observed identity and never invent a management IP");
        var header = "Local Interface Chassis ID Port ID System Name";
        Assert(H3cComwareParser.ParseLldpListTable(header, completedExec: true).Status == "PARSED" &&
            H3cComwareParser.ParseLldpListTable(header, completedExec: true).Neighbors.Count == 0,
            "a completed exact table header is an empty complete table");
        foreach (var bad in new[] { "unrecognized output", header + "\nGE1/0/1 truncated",
            header + "\n-- More --", header + "\nPermission denied" })
            Assert(H3cComwareParser.ParseLldpListTable(bad, completedExec: true).Status != "PARSED",
                "malformed, paged and denied output must never authorize set replacement");
    }

    private static void ProjectionClassifiesHistoryWithoutDeletes()
    {
        const string profile = "profile-topology";
        var facts = new[]
        {
            Fact(profile, "fact-1", 1000, "GE1/0/1", "SW-A", "Ten-Gig1/0/1"),
            Fact(profile, "fact-2", 2000, "GE1/0/1", "SW-A", "Ten-Gig1/0/1"),
            Fact(profile, "fact-3", 3000, "GE1/0/1", "SW-A", "Ten-Gig1/0/2"),
            new H3cFactSnapshot("fact-ignored", profile, "lldp.neighbor", "bad", "STALE", "fixture", 4000, true,
                """{"localInterface":"GE1/0/1","neighborSysname":"SW-A"}""")
        };
        var relations = H3cLldpTopologyProjection.Project(profile, facts);
        Assert(relations.Count == 3, "LLDP projection must ignore facts without captured Site scope");
        Assert(relations[0].ChangeKind == "FIRST_SEEN" && relations[1].ChangeKind == "REPEAT" &&
               relations[2].ChangeKind == "CHANGED", "LLDP history must distinguish first, repeat and field change");
        Assert(relations[2].NeighborPort == "Ten-Gig1/0/2" && relations[2].SiteId == "site-a" &&
               relations[2].FactRevision == "fact-3", "LLDP relation must retain endpoint, Site and fact revision");
        Assert(relations.All(item => item.ChangeKind is not "REMOVED"),
            "missing LLDP evidence must never be fabricated as a deletion");
    }

    private static void OfflineFixtureProjectsScopedRelation()
    {
        using var fixture = new Fixture();
        var profile = new H3cDeviceProfileSnapshot(Guid.NewGuid().ToString("N"), "192.0.2.51", 22,
            "fixture", "FIXTURE_REFERENCE", string.Empty, true, 0, 0);
        fixture.Catalog.CreateH3cDeviceProfile(profile);
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "phase9_lldp_list.txt"));
        fixture.Catalog.ImportH3cOfflineReplay(profile.Id,
            new Dictionary<string, string> { [H3cCommandPolicy.DisplayLldpNeighborInformationList] = output }, 1000);
        var relations = fixture.Catalog.QueryH3cLldpRelations(profile.Id, history: false, 100, P2B5SiteScopeDecision.Global);
        Assert(relations.Count == 1 && relations[0].LocalDeviceId.Length > 0 &&
               relations[0].LocalInterface == "GE1/0/1" && relations[0].NeighborDevice == "AC-DEMO" &&
               relations[0].NeighborPort == "GE1/0/1" && relations[0].SiteId.Length > 0 &&
               relations[0].Source == Catalog.H3cOfflineFixtureSource,
            "offline LLDP fixture must produce a scoped relation with provenance");
    }

    private static void RelationHistoryIsScopedToLocalDeviceAndSite()
    {
        const string profile = "profile-topology-scope";
        var facts = new[]
        {
            Fact(profile, "fact-device-a", 1000, "GE1/0/1", "SW-A", "Ten-Gig1/0/1", device: "device-a", site: "site-a"),
            Fact(profile, "fact-device-b", 2000, "GE1/0/1", "SW-A", "Ten-Gig1/0/1", device: "device-b", site: "site-b")
        };
        var relations = H3cLldpTopologyProjection.Project(profile, facts);
        Assert(relations.Count == 2 && relations.All(item => item.ChangeKind == "FIRST_SEEN"),
            "identical ports and neighbors on different local devices must have independent histories");
        Assert(relations.Select(item => item.RelationKey).Distinct(StringComparer.Ordinal).Count() == 2,
            "LLDP relation keys must retain local device and Site scope");
    }

    private static H3cFactSnapshot Fact(string profile, string id, long captured, string local,
        string neighbor, string port, string device = "device-a", string site = "site-a")
    {
        var payload = JsonSerializer.Serialize(new
        {
            localInterface = local,
            neighborSysname = neighbor,
            neighborInterface = port,
            managementAddress = "10.0.0.2",
            chassisId = "4c6f-d608-0400",
            capturedScope = new { version = 1, deviceId = device, profileId = profile, siteId = site }
        });
        return new(id, profile, "lldp.neighbor", $"{local}|{port}|chassis", "STALE", "fixture", captured, true, payload);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "next-lldp-topology-" + Guid.NewGuid().ToString("N"));
        public RuntimeSettings Settings { get; }
        public Catalog Catalog { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings);
            Catalog.Initialize();
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
