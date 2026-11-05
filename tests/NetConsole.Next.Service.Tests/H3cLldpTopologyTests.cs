using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Text.Json;

internal static class H3cLldpTopologyTests
{
    public static void Run()
    {
        ProjectionClassifiesHistoryWithoutDeletes();
        OfflineFixtureProjectsScopedRelation();
        Console.WriteLine("H3C_LLDP_TOPOLOGY_TESTS=2/2 PASS");
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

    private static H3cFactSnapshot Fact(string profile, string id, long captured, string local,
        string neighbor, string port)
    {
        var payload = JsonSerializer.Serialize(new
        {
            localInterface = local,
            neighborSysname = neighbor,
            neighborInterface = port,
            managementAddress = "10.0.0.2",
            chassisId = "4c6f-d608-0400",
            capturedScope = new { version = 1, deviceId = "device-a", profileId = profile, siteId = "site-a" }
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
