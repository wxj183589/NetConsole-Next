using System.Net;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

internal static class H3cOnboardSiteTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-onboard-site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 65536 });
            catalog.Initialize();
            using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "catalog.db")}"))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = "INSERT INTO sites(id,name) VALUES('site-a','Site A'),('site-b','Site B')";
                command.ExecuteNonQuery();
            }
            var id = Guid.NewGuid().ToString("N");
            var input = new H3cDeviceProfileSnapshot(id, "192.0.2.77", 22, "reader", "", "", true, 0, 0,
                Password: "synthetic", SiteId: "site-a");
            catalog.CreateH3cDeviceProfile(input);
            var saved = catalog.GetH3cDeviceProfile(id)!;
            Assert(saved.SiteId == "site-a", "new profile must retain its selected Site");
            var a = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "site-a" }, "test");
            var b = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "site-b" }, "test");
            Assert(catalog.H3cProfileIsInScope(id, a) && !catalog.H3cProfileIsInScope(id, b), "profile visibility must follow the selected Site");
            catalog.CreateH3cDeviceProfile(saved with { SiteId = "site-b", Password = "" });
            Assert(catalog.GetH3cDeviceProfile(id)!.SiteId == "site-a", "updating an existing profile must preserve its Site");
            using var verify = new SqliteConnection($"Data Source={Path.Combine(root, "catalog.db")}");
            verify.Open(); using var query = verify.CreateCommand();
            query.CommandText = "SELECT site_id FROM device_inventory WHERE terminal_profile_id=$id";
            query.Parameters.AddWithValue("$id", id);
            Assert((string?)query.ExecuteScalar() == "site-a", "inventory projection must use the same Site");
            Console.WriteLine("H3C_ONBOARD_SITE_TESTS=PASS");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
