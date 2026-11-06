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
            AtomicOnboardRollsBackAllRecords(catalog, verify);
            Console.WriteLine("H3C_ONBOARD_SITE_TESTS=PASS");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void AtomicOnboardRollsBackAllRecords(Catalog catalog, SqliteConnection db)
    {
        DeviceInventoryWriteRequest Inventory(string vendor = "Cisco") => new(null, "Core switch", "192.0.2.78",
            vendor, "", "IOS_XE", "site-a", null, "Synthetic fixture", true, null);
        H3cDeviceProfileSnapshot Profile() => new(Guid.NewGuid().ToString("N"), "192.0.2.78", 22, "reader", "", "",
            true, 0, 0, Password: "synthetic", SiteId: "site-a");
        long Count(string table)
        {
            using var count = db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM " + table;
            return (long)count.ExecuteScalar()!;
        }
        var profile = Profile();
        catalog.CreateH3cDeviceProfile(profile, Inventory(), "test-operator");
        var detail = catalog.GetDeviceInventoryDetail(profile.Id)!;
        Assert(detail.Item.Name == "Core switch" && detail.Item.Vendor == "Cisco" &&
            detail.Item.TerminalProfileId == profile.Id && catalog.GetNetmikoVendor(profile.Id) == "cisco_ios_xe",
            "atomic onboarding must save metadata and bind the selected driver");
        var tables = new[] { "h3c_device_profiles", "collection_sources", "ssh_host_keys", "device_inventory", "audit_events" };
        var before = tables.ToDictionary(table => table, Count);
        var duplicate = Profile();
        try { catalog.CreateH3cDeviceProfile(duplicate, Inventory(), "test-operator"); throw new Exception("duplicate accepted"); }
        catch (InvalidOperationException) { }
        Assert(catalog.GetH3cDeviceProfile(duplicate.Id) is null && tables.All(table => Count(table) == before[table]),
            "duplicate address must not leave credentials, source, host key or inventory behind");
        using (var fault = db.CreateCommand())
        {
            fault.CommandText = "CREATE TRIGGER reject_onboard_fixture BEFORE INSERT ON device_inventory WHEN NEW.vendor='reject-fixture' BEGIN SELECT RAISE(ABORT,'synthetic inventory failure'); END";
            fault.ExecuteNonQuery();
        }
        var failed = Profile() with { Host = "192.0.2.79" };
        try { catalog.CreateH3cDeviceProfile(failed, Inventory("reject-fixture"), "test-operator"); throw new Exception("injected failure accepted"); }
        catch (SqliteException) { }
        Assert(catalog.GetH3cDeviceProfile(failed.Id) is null && tables.All(table => Count(table) == before[table]),
            "inventory write failure must roll back all connection records");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
