using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class LinuxServerTests
{
    private const string FirstKey = "SHA256:AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";
    private const string SecondKey = "SHA256:AgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fICE";

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-linux-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 65536 };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            using var db = new SqliteConnection($"Data Source={Path.Combine(root, "catalog.db")};Pooling=False");
            db.Open();
            Execute(db, "INSERT INTO sites(id,name) VALUES('site-a','Site A'),('site-b','Site B')");
            CredentialsScopeAndIdentity(catalog, settings, db);
            ProbeRechecksMovedServerScope(catalog);
            HostKeyConcurrency(catalog, db);
            UpgradePreservesTerminalHistory();
            UpgradeRollsBackOnInvalidForeignKey();
            UpgradeCatalogRestart(settings);
            Console.WriteLine("LINUX_SERVER_TESTS=PASS");
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch { } }
    }

    private static void CredentialsScopeAndIdentity(Catalog catalog, RuntimeSettings settings, SqliteConnection db)
    {
        var input = new LinuxServerRequest(" Linux fixture ", "192.0.2.31", "reader", Password: "synthetic-password",
            GroupName: " Group A ", SiteId: "site-a");
        var view = catalog.SaveLinuxServer(null, input);
        Assert(view is { Name: "Linux fixture", GroupName: "Group A", CredentialConfigured: true }, "server metadata must be normalized");
        var json = JsonSerializer.Serialize(view);
        using var safe = JsonDocument.Parse(json);
        var names = safe.RootElement.EnumerateObject().Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(!names.Contains("password") && !names.Contains("credentialPassword") && !names.Contains("credentialReference") &&
            !json.Contains("synthetic-password", StringComparison.Ordinal), "safe server views must not expose password fields or values");
        var a = Scope("site-a"); var b = Scope("site-b");
        Assert(catalog.QueryLinuxServers(a).Single().Id == view.Id && catalog.QueryLinuxServers(b).Count == 0 &&
            catalog.LinuxServerIsInScope(view.Id, a) && !catalog.LinuxServerIsInScope(view.Id, b), "server reads must obey Site scope");
        var before = catalog.GetLinuxServer(view.Id)!;
        catalog.SaveLinuxServer(view.Id, input with { Name = "Renamed", Password = "", GroupName = "New group" });
        Assert(catalog.GetLinuxServerPassword(view.Id) == "synthetic-password" &&
            catalog.GetLinuxServer(view.Id)!.UpdatedMs > before.UpdatedMs, "blank password must retain the saved credential and advance revision");
        catalog.SaveLinuxServer(view.Id, input with { Password = null });
        Assert(catalog.GetLinuxServerPassword(view.Id) == "synthetic-password", "omitted password must retain the saved credential");
        foreach (var invalid in new[] { input with { Port = 0 }, input with { Host = "invalid host" },
                     input with { Password = new string('x', 4097) }, input with { GroupName = "invalid\ngroup" } })
        {
            before = catalog.GetLinuxServer(view.Id)!;
            Expect<ArgumentException>(() => catalog.SaveLinuxServer(view.Id, invalid));
            Assert(catalog.GetLinuxServer(view.Id) == before && catalog.GetLinuxServerPassword(view.Id) == "synthetic-password",
                "invalid saves must preserve metadata and password atomically");
        }
        before = catalog.GetLinuxServer(view.Id)!;
        Expect<KeyNotFoundException>(() => catalog.SaveLinuxServer(view.Id, input with { SiteId = "missing" }));
        Expect<KeyNotFoundException>(() => catalog.SaveLinuxServer("missing", input));
        Assert(catalog.GetLinuxServer(view.Id) == before, "missing Site must preserve the saved server");
        Assert(catalog.CommitLinuxHostKey(before, "ssh-ed25519", FirstKey), "authenticated first key must be saved");
        catalog.RecordLinuxConnectionResult(view.Id, "CONNECTED", connected: true, used: true);
        var sessionId = Guid.NewGuid().ToString("N");
        catalog.CreateTerminalSession(new(sessionId, view.Id, view.Name, "SSH", "LINUX_INTERACTIVE", view.Host, view.Port,
            null, null, "CONNECTING", 1000, null, 1000, null, null, null, null, null, 0, null, false,
            OperatorIdentity: "test-reader", SiteName: "site-a"));
        catalog.UpdateTerminalState(sessionId, "CLOSED", disconnectReason: "TEST");
        Assert(catalog.GetTerminalSession(sessionId) is { OperatorIdentity: "test-reader", SiteName: "site-a" } &&
            catalog.TerminalSessionIsInScope(sessionId, a) && !catalog.TerminalSessionIsInScope(sessionId, b),
            "Linux history must retain its operator and captured Site");
        var query = new TerminalSessionQuery(null, null, null, null, null, null, null, null, 1, 20, "started", true);
        Assert(catalog.QueryTerminalSessions(query, a).Total == 1 && catalog.QueryTerminalSessions(query, b).Total == 0,
            "Linux session lists must apply captured Site scope in SQL");
        catalog.SaveLinuxServer(view.Id, input with { Password = null, Host = "192.0.2.32", SiteId = "site-b" });
        Assert(catalog.GetLinuxServer(view.Id)!.View is { HostKeySha256.Length: 0, LastConnectedMs: null, LastResult: null } &&
            catalog.GetLinuxServerPassword(view.Id) == "synthetic-password", "identity changes must reset trust and success metadata while preserving credentials");
        catalog.SaveLinuxServer(view.Id, input with { Password = "replacement", ClearPassword = true });
        Assert(!catalog.GetLinuxServer(view.Id)!.View.CredentialConfigured && catalog.GetLinuxServerPassword(view.Id) is null,
            "explicit clear must override the submitted password");
        catalog.SaveLinuxServer(view.Id, input with { Password = "restart-password" });
        var restarted = new Catalog(settings); restarted.Initialize();
        Assert(restarted.GetLinuxServerPassword(view.Id) == "restart-password" && restarted.GetTerminalSession(sessionId) is { State: "CLOSED" },
            "server credentials and closed terminal history must survive catalog restart");
        Assert(restarted.DeleteLinuxServer(view.Id) && !restarted.DeleteLinuxServer(view.Id) && restarted.GetLinuxServer(view.Id) is null,
            "deletion must remove exactly one server");
        Assert(restarted.GetTerminalSession(sessionId) is not null && restarted.TerminalSessionIsInScope(sessionId, a) &&
            !restarted.TerminalSessionIsInScope(sessionId, b) && restarted.QueryTerminalSessions(query, a).Total == 1,
            "deleting a server must retain its scoped terminal history");
        Assert(Scalar(db, "SELECT COUNT(*) FROM linux_host_key_events") == 1, "deletion must retain separate host-key history");
    }

    private static void ProbeRechecksMovedServerScope(Catalog catalog)
    {
        var request = new LinuxServerRequest("probe scope fixture", "192.0.2.43", "reader", Password: "synthetic",
            SiteId: "site-a", Enabled: false);
        var server = catalog.SaveLinuxServer(null, request);
        var admittedScope = Scope("site-a");
        Assert(catalog.LinuxServerIsInScope(server.Id, admittedScope), "the original request filter admits Site A");
        catalog.SaveLinuxServer(server.Id, request with { SiteId = "site-b" });
        var probes = new LinuxServerConnectionTests(catalog);
        for (var attempt = 0; attempt < 5; attempt++)
            Expect<KeyNotFoundException>(() => probes.TestAsync(server.Id, admittedScope, CancellationToken.None).GetAwaiter().GetResult());
        Expect<KeyNotFoundException>(() => probes.TestAsync("missing", Scope("site-b"), CancellationToken.None).GetAwaiter().GetResult());
        var allowed = probes.TestAsync(server.Id, Scope("site-b"), CancellationToken.None).GetAwaiter().GetResult();
        Assert(JsonSerializer.Serialize(allowed).Contains("DISABLED", StringComparison.Ordinal),
            "out-of-scope probes must stay hidden and release every concurrency slot before an authorized probe");
        catalog.DeleteLinuxServer(server.Id);
    }

    private static void HostKeyConcurrency(Catalog catalog, SqliteConnection db)
    {
        var request = new LinuxServerRequest("key fixture", "192.0.2.42", "reader", Password: "synthetic");
        var server = catalog.SaveLinuxServer(null, request);
        var initial = catalog.GetLinuxServer(server.Id)!;
        var eventsBefore = Scalar(db, "SELECT COUNT(*) FROM linux_host_key_events");
        Assert(catalog.CommitLinuxHostKey(initial, "ssh-ed25519", FirstKey), "first authenticated key must be accepted");
        Assert(catalog.CommitLinuxHostKey(initial, "ssh-ed25519", FirstKey), "concurrent first connections observing the same key must both succeed");
        Assert(Scalar(db, "SELECT COUNT(*) FROM linux_host_key_events") == eventsBefore + 1, "concurrent matching keys must record one event");
        var trusted = catalog.GetLinuxServer(server.Id)!;
        Assert(catalog.CommitLinuxHostKey(trusted, "ssh-ed25519", SecondKey), "authenticated rotation must update automatically");
        Assert(!catalog.CommitLinuxHostKey(initial, "ssh-ed25519", FirstKey), "a stale connection must not restore the previous fingerprint");
        var beforeEdit = catalog.GetLinuxServer(server.Id)!;
        catalog.SaveLinuxServer(server.Id, request with { Enabled = false });
        Assert(!catalog.CommitLinuxHostKey(beforeEdit, "ssh-ed25519", FirstKey) &&
            catalog.GetLinuxServer(server.Id)!.View.HostKeySha256 == SecondKey, "a connection from an older configuration must not commit trust");
        Assert(Scalar(db, "SELECT COUNT(*) FROM linux_host_key_events") == eventsBefore + 2, "trust history must include first trust and one rotation");
        var failure = LinuxSshTerminalTransport.Failure(new Exception("synthetic-password"));
        Assert(!failure.Message.Contains("synthetic-password", StringComparison.Ordinal), "safe connection failures must not echo exception contents");
    }

    private static void UpgradePreservesTerminalHistory()
    {
        using var db = OldTerminalDatabase();
        var before = SnapshotTables(db);
        LinuxServerSchema.Apply(db);
        Assert(SnapshotTables(db) == before, "schema 23 must preserve every terminal, event, segment and export field");
        Assert(Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('idx_terminal_sessions_target_started','idx_terminal_sessions_started','fixture_terminal_operator')") == 3,
            "schema 23 must preserve standard and custom terminal indexes");
        Assert(Scalar(db, "PRAGMA foreign_keys") == 1 && Scalar(db, "SELECT COUNT(*) FROM pragma_foreign_key_check") == 0,
            "schema 23 must restore enforced foreign keys without orphaned records");
        Execute(db, "INSERT INTO terminal_sessions(id,target_id,target_name,protocol,mode,state,started_ms,last_activity_ms) VALUES('linux-session','server','Linux','SSH','LINUX_INTERACTIVE','CLOSED',2000,2000)");
        Expect<SqliteException>(() => Execute(db, "UPDATE terminal_sessions SET mode='INVALID' WHERE id='linux-session'"));
        var upgraded = SnapshotTables(db);
        LinuxServerSchema.Apply(db);
        Assert(SnapshotTables(db) == upgraded && Scalar(db, "SELECT COUNT(*) FROM schema_versions WHERE version=23") == 1,
            "reapplying schema 23 must be idempotent without duplicating history");
        Execute(db, "DELETE FROM terminal_sessions WHERE id='old-session'");
        Assert(Scalar(db, "SELECT COUNT(*) FROM terminal_events") == 0 && Scalar(db, "SELECT COUNT(*) FROM terminal_recording_segments") == 0 &&
            Scalar(db, "SELECT COUNT(*) FROM terminal_exports") == 0, "child foreign keys must still reference the migrated terminal table");
    }

    private static void UpgradeRollsBackOnInvalidForeignKey()
    {
        using var db = OldTerminalDatabase();
        Execute(db, "PRAGMA foreign_keys=OFF; INSERT INTO terminal_events(session_id,at_ms,event_type,detail_json) VALUES('missing',1,'CONNECTED','{}'); PRAGMA foreign_keys=ON");
        var before = SnapshotTables(db);
        Expect<InvalidDataException>(() => LinuxServerSchema.Apply(db));
        Assert(SnapshotTables(db) == before && Scalar(db, "PRAGMA foreign_keys") == 1 &&
            Scalar(db, "SELECT COUNT(*) FROM schema_versions WHERE version=23") == 0,
            "failed migration must preserve terminal records, restore foreign keys and avoid marking schema 23 applied");
        using var sql = db.CreateCommand();
        sql.CommandText = "SELECT sql FROM sqlite_master WHERE name='terminal_sessions'";
        Assert(!((string)sql.ExecuteScalar()!).Contains("LINUX_INTERACTIVE", StringComparison.Ordinal), "failed migration must restore the original CHECK constraint");
    }

    private static void UpgradeCatalogRestart(RuntimeSettings settings)
    {
        var root = Path.Combine(settings.DataRoot, "upgrade-restart");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".netconsole-next-root"), "NETCONSOLE_NEXT_V1\n");
        string before;
        using (var original = OldTerminalDatabase())
        using (var target = new SqliteConnection($"Data Source={Path.Combine(root, "catalog.db")};Pooling=False"))
        {
            before = SnapshotTables(original);
            target.Open(); original.BackupDatabase(target);
        }
        settings = new RuntimeSettings { DataRoot = root, WebPort = settings.WebPort, SyslogPort = 0,
            SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 65536 };
        for (var restart = 0; restart < 2; restart++)
        {
            var catalog = new Catalog(settings); catalog.Initialize();
            using var db = new SqliteConnection($"Data Source={Path.Combine(root, "catalog.db")};Pooling=False"); db.Open();
            Assert(SnapshotTables(db) == before && catalog.GetTerminalSession("old-session") is { OperatorIdentity: "old-operator", State: "CLOSED" },
                "catalog startup migration and restart must preserve existing terminal history exactly");
            Assert(Scalar(db, "SELECT COUNT(*) FROM pragma_foreign_key_check") == 0 &&
                Scalar(db, "SELECT COUNT(*) FROM schema_versions WHERE version=23") == 1, "catalog restart must keep one valid schema 23 migration");
        }
    }

    private static SqliteConnection OldTerminalDatabase()
    {
        var db = new SqliteConnection("Data Source=:memory:"); db.Open();
        Execute(db, "PRAGMA foreign_keys=ON; CREATE TABLE schema_versions(version INTEGER PRIMARY KEY,applied_ms INTEGER NOT NULL); CREATE TABLE sites(id TEXT PRIMARY KEY,name TEXT NOT NULL)");
        foreach (var resource in new[] { "schema_v6.sql", "schema_v7.sql" })
        {
            using var stream = typeof(Catalog).Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            if (resource == "schema_v7.sql")
                Execute(db, """
                    ALTER TABLE terminal_sessions ADD COLUMN operator_identity TEXT NOT NULL DEFAULT 'UNATTRIBUTED';
                    ALTER TABLE terminal_sessions ADD COLUMN site_name TEXT;
                    ALTER TABLE terminal_sessions ADD COLUMN command_count INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE terminal_sessions ADD COLUMN output_bytes INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE terminal_sessions ADD COLUMN recording_status TEXT NOT NULL DEFAULT 'INCOMPLETE';
                    ALTER TABLE terminal_sessions ADD COLUMN recording_verified_ms INTEGER;
                    ALTER TABLE terminal_sessions ADD COLUMN recording_integrity_detail TEXT;
                    """);
            Execute(db, reader.ReadToEnd());
        }
        Execute(db, """
            CREATE INDEX fixture_terminal_operator ON terminal_sessions(operator_identity,state);
            INSERT INTO schema_versions(version,applied_ms) VALUES(22,1000);
            INSERT INTO terminal_sessions VALUES('old-session','network-device','Switch','SSH','RESTRICTED_READONLY','192.0.2.1',22,NULL,NULL,
                'CLOSED',1000,1100,1100,'CONNECTED','USER_REQUEST','curve25519/ed25519','FIRST_TRUST','terminal/synthetic',42,'synthetic-sha',1,
                'old-operator','Site A',2,16,'COMPLETE',1111,'verified synthetic');
            INSERT INTO terminal_events(session_id,at_ms,event_type,detail_json) VALUES('old-session',1000,'CONNECTING','{}'),('old-session',1100,'SESSION_CLOSED','{"reason":"USER_REQUEST"}');
            INSERT INTO terminal_recording_segments VALUES('old-session',0,'terminal/synthetic.0000',1000,1100,42,4,'synthetic-sha','COMPLETE');
            INSERT INTO terminal_exports VALUES('old-export','old-session','JSONL',1200,1300,'COMPLETE','old-operator','COMPLETED',42,'exported');
            """);
        return db;
    }

    private static string SnapshotTables(SqliteConnection db)
    {
        var result = new List<object[]>();
        foreach (var table in new[] { "terminal_sessions", "terminal_events", "terminal_recording_segments", "terminal_exports" })
        {
            using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY 1";
            using var reader = command.ExecuteReader();
            while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); result.Add(row); }
        }
        return JsonSerializer.Serialize(result);
    }

    private static P2B5SiteScopeDecision Scope(string site) => new("AUTHORIZED", false, new HashSet<string> { site }, "test");
    private static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static long Scalar(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
