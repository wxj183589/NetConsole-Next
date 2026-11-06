using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;

internal static class CredentialTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "next-credential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new RuntimeSettings
            {
                DataRoot = root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024
            };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "catalog.db"), Pooling = false
            }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR IGNORE INTO sites(id,name) VALUES('site-a','Site A'),('site-b','Site B')";
                command.ExecuteNonQuery();
            }
            var sharedReference = "SHARED_FIXTURE";
            var firstId = Guid.NewGuid().ToString("N");
            var secondId = Guid.NewGuid().ToString("N");
            catalog.CreateH3cDeviceProfile(new(firstId, "192.0.2.10", 22, "reader", sharedReference,
                string.Empty, true, 0, 0, Password: "first-secret"));
            catalog.CreateH3cDeviceProfile(new(secondId, "192.0.2.11", 22, "reader", sharedReference,
                string.Empty, true, 0, 0, Password: "second-secret"));
            Assert(catalog.GetNetmikoVendor(firstId) == "h3c", "legacy H3C inventory selects H3C");
            using (var inventoryDb = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "catalog.db"), Pooling = false
            }.ToString()))
            {
                inventoryDb.Open();
                using var update = inventoryDb.CreateCommand();
                update.CommandText = "UPDATE device_inventory SET vendor='Cisco',device_type='IOS_XE' WHERE terminal_profile_id=$id";
                update.Parameters.AddWithValue("$id", firstId);
                update.ExecuteNonQuery();
                Assert(catalog.GetNetmikoVendor(firstId) == "cisco_ios_xe", "Cisco platform must select its explicit mapping");
                update.CommandText = "UPDATE device_inventory SET vendor='Cisco',device_type='SWITCH' WHERE terminal_profile_id=$id";
                update.ExecuteNonQuery();
                Assert(catalog.GetNetmikoVendor(firstId) == "", "unspecified Cisco platform must not guess a driver");
                update.CommandText = "UPDATE device_inventory SET vendor='unknown-vendor' WHERE terminal_profile_id=$id";
                update.ExecuteNonQuery();
                Assert(catalog.GetNetmikoVendor(firstId) == "unknown-vendor", "unknown vendor must reach worker for rejection");
            }
            var provider = new DeviceRecordH3cCredentialProvider(catalog);
            Assert(provider.GetPassword(catalog.GetH3cDeviceProfile(firstId)!) == "first-secret",
                "device credentials must be resolved by profile id even when references are shared");
            Assert(provider.GetPassword(catalog.GetH3cDeviceProfile(secondId)!) == "second-secret",
                "each device must retain its own password");
            catalog.UpdateH3cCredential(firstId, "", false);
            Assert(provider.GetPassword(catalog.GetH3cDeviceProfile(firstId)!) == "first-secret",
                "an empty credential update must retain the existing password");
            Assert(catalog.QueryManagedDeviceInventory().Items.Single(item => item.TerminalProfileId == firstId).CredentialConfigured,
                "inventory must report an actually stored password");
            catalog.CreateH3cDeviceProfile(new(firstId, "192.0.2.10", 22, "reader", sharedReference,
                string.Empty, true, 0, 0));
            Assert(provider.GetPassword(catalog.GetH3cDeviceProfile(firstId)!) == "first-secret",
                "omitting a password on profile update must retain the existing password");
            catalog.CreateH3cDeviceProfile(new(firstId, "192.0.2.10", 22, "reader", sharedReference,
                string.Empty, true, 0, 0, Password: null, ClearPassword: true));
            Assert(provider.GetPassword(catalog.GetH3cDeviceProfile(firstId)!) is null,
                "an explicit clear operation must remove only the selected device password");
            Assert(!catalog.QueryManagedDeviceInventory().Items.Single(item => item.TerminalProfileId == firstId).CredentialConfigured,
                "inventory must not report a cleared password as configured");
            Assert(provider.GetPassword(catalog.GetH3cDeviceProfile(secondId)!) == "second-secret",
                "clearing one device must not affect another device sharing a reference");

            var pinA = Convert.ToBase64String(new byte[32]).TrimEnd('=');
            var pinB = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()).TrimEnd('=');
            catalog.UpsertSiteSshJumpHost(new("site-a", "192.0.2.20", 22, "jump-a", "JUMP_A", "jump-secret-a", pinA, true, 0));
            catalog.UpsertSiteSshJumpHost(new("site-b", "192.0.2.21", 2222, "jump-b", "JUMP_B", "jump-secret-b", pinB, true, 0));
            Assert(catalog.GetSiteSshJumpHost("site-a")?.Password == "jump-secret-a" &&
                   catalog.GetSiteSshJumpHost("site-b")?.Password == "jump-secret-b",
                "Jump Host credentials must remain isolated by Site");
            Assert(catalog.GetSiteSshJumpHost("site-a", includePassword: false)?.Password is null,
                "Jump Host API view path must support password redaction");
            Assert(catalog.GetSiteSshJumpHost("site-a", includePassword: false)!.ToView().CredentialConfigured,
                "a redacted Jump Host must still report its actual saved credential state");
            catalog.UpsertSiteSshJumpHost(new("site-a", "192.0.2.20", 22, "jump-a", "JUMP_A", "", string.Empty, true, 0));
            Assert(catalog.GetSiteSshJumpHost("site-a")?.Password == "jump-secret-a",
                "empty Jump Host password updates must retain the existing secret");
            catalog.UpsertSiteSshJumpHost(new("site-a", "192.0.2.20", 22, "jump-a", "JUMP_A", null, string.Empty, true, 0, true));
            Assert(catalog.GetSiteSshJumpHost("site-a")?.Password is null,
                "explicit Jump Host clear must remove only the selected Site secret");
            Assert(!catalog.GetSiteSshJumpHost("site-a", includePassword: false)!.ToView().CredentialConfigured &&
                catalog.GetSiteSshJumpHost("site-a", includePassword: false)!.ToView().RuntimeStatus == "CREDENTIAL_MISSING",
                "cleared credentials must appear as missing in the safe API view");
            Assert(catalog.GetSiteSshJumpHost("site-b")?.Password == "jump-secret-b",
                "clearing one Site Jump Host must not affect another Site");
            Assert(catalog.UpdateSiteSshJumpHostFingerprint("site-b", pinA) &&
                   H3cHostKeyFingerprint.Matches(pinA, catalog.GetSiteSshJumpHost("site-b")!.HostKeySha256),
                "Jump Host fingerprint refresh must update only the selected Site");
            var beforeRotation = catalog.GetSiteSshJumpHost("site-b")!;
            Assert(catalog.UpdateSiteSshJumpHostFingerprint("site-b", pinB, beforeRotation),
                "a changed Jump Host key should be accepted after authentication");
            Assert(catalog.UpdateSiteSshJumpHostFingerprint("site-b", pinB, beforeRotation),
                "concurrent probes observing the same key should both succeed");
            Assert(!catalog.UpdateSiteSshJumpHostFingerprint("site-b", pinA, beforeRotation),
                "a stale probe must not overwrite a newer Jump Host key");
            var staleConfig = catalog.GetSiteSshJumpHost("site-b")!;
            catalog.UpsertSiteSshJumpHost(staleConfig with { Host = "192.0.2.22" });
            Assert(!catalog.UpdateSiteSshJumpHostFingerprint("site-b", pinA, staleConfig),
                "a probe must not attach the old endpoint key to a changed configuration");
            var beforeDelete = catalog.GetSiteSshJumpHost("site-b")!;
            Assert(catalog.ClearSiteSshJumpHostFingerprint("site-b"), "fingerprint-only removal must find the Site");
            var afterDelete = catalog.GetSiteSshJumpHost("site-b")!;
            Assert(afterDelete.HostKeySha256 == "" && afterDelete.Password == beforeDelete.Password &&
                afterDelete.Host == beforeDelete.Host && afterDelete.Enabled == beforeDelete.Enabled,
                "fingerprint removal must preserve connection settings and credentials");
            Assert(!catalog.UpdateSiteSshJumpHostFingerprint("site-b", pinA, beforeDelete),
                "a probe started before fingerprint deletion must not restore the deleted fingerprint");
            Assert(H3cHostKeyFingerprint.Matches(pinA, catalog.GetSiteSshJumpHost("site-a")!.HostKeySha256),
                "fingerprint removal must not affect other Sites");
            catalog.UpsertSiteSshJumpHost(afterDelete with { HostKeySha256 = pinB });
            catalog.UpsertSiteSshJumpHost(afterDelete with { Port = 2223 });
            Assert(catalog.GetSiteSshJumpHost("site-b")!.HostKeySha256 == "",
                "changing an endpoint port must discard the old endpoint fingerprint");
            catalog.DeleteSiteSshJumpHost("site-a");
            catalog.UpsertSiteSshJumpHost(new("site-a", "192.0.2.20", 22, "jump-a", "JUMP_A", "ignored", "", true, 0, true));
            Assert(catalog.GetSiteSshJumpHost("site-a")!.Password is null,
                "explicit clear must also win over a supplied password when first inserting a configuration");
            using (var auditDb = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "catalog.db"), Pooling = false
            }.ToString()))
            {
                auditDb.Open();
                using var audit = auditDb.CreateCommand();
                audit.CommandText = "SELECT detail_json FROM audit_events WHERE action='SITE_JUMP_HOST_KEY_UPDATED' ORDER BY at_ms DESC LIMIT 1";
                var detail = (string)audit.ExecuteScalar()!;
                Assert(detail.Contains("oldFingerprint") && detail.Contains("newFingerprint") && !detail.Contains("jump-secret"),
                    "rotation audit must include both keys and omit credentials");
            }
            Assert(!H3cHostKeyFingerprint.Matches(pinA, pinB),
                "distinct fingerprints must compare as different before automatic authenticated rotation");
            Assert(!new NetmikoWorkerH3cSshTransport(
                new H3cDeviceProfileSnapshot(Guid.NewGuid().ToString("N"), "192.0.2.30", 23, "reader", "DEVICE", string.Empty,
                    true, 0, 0, TerminalProtocol: "TELNET"), "device-secret",
                catalog.GetSiteSshJumpHost("site-b"))!.HasJumpHost,
                "Telnet sessions must not use an SSH Jump Host");
            Assert(catalog.DeleteSiteSshJumpHost("site-a") && catalog.GetSiteSshJumpHost("site-a") is null &&
                   catalog.GetSiteSshJumpHost("site-b") is not null,
                "deleting one Site Jump Host must leave other Sites unchanged");
            Console.WriteLine("CREDENTIAL_TESTS=5/5 PASS");
            Console.WriteLine("JUMP_HOST_TESTS=PASS (Site isolation, safe views, retention, explicit clear, authenticated rotation, fingerprint deletion, endpoint changes, Telnet bypass)");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
