using Microsoft.Data.Sqlite;
using System.Net;

namespace NetConsole.Next.Service.Tests;

internal static class P2B5SiteScopeTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-p2b5-site-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "catalog.db");
        var previousV13Gate = Environment.GetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable);
        var previousSiteScopeGate = Environment.GetEnvironmentVariable(P2B5SiteScopeMigrationContext.ExplicitMigrationEnvironmentVariable);
        try
        {
            var settings = new RuntimeSettings
            {
                DataRoot = root,
                WebPort = 18771,
                SyslogPort = 0,
                SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024
            };
            new Catalog(settings).Initialize();

            Environment.SetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable, "1");
            using (var connection = Open(databasePath))
                P2B2SchemaV13Migration.Apply(connection, P2B2MigrationContext.ForIsolatedTest(databasePath));

            Environment.SetEnvironmentVariable(P2B5SiteScopeMigrationContext.ExplicitMigrationEnvironmentVariable, "1");
            using (var connection = Open(databasePath))
            {
                var migration = P2B5SiteScopeMigration.Apply(connection, P2B5SiteScopeMigrationContext.ForIsolatedTest(databasePath));
                Assert(!migration.AlreadyApplied && migration.BaseSchemaVersion == 13, "P2B5 migration must add the auxiliary schema without changing Schema v13.");
            }

            string adminId;
            using (var connection = Open(databasePath))
            using (var query = connection.CreateCommand())
            {
                query.CommandText = "SELECT id FROM web_users WHERE username='admin'";
                adminId = (string?)query.ExecuteScalar() ?? throw new InvalidOperationException("default admin fixture is missing");
            }

            var store = new P2B5SiteScopeStore(settings);
            var admin = new WebUserIdentity(adminId, "admin", "ADMIN", "isolated-session");
            Assert(store.Resolve(admin).State == "DENIED", "an explicit authority with no membership must deny the ADMIN user");
            InsertMembership(databasePath, adminId, "site-a", "ACTIVE", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000, "direct-a", 1);
            var authorized = store.Resolve(admin);
            Assert(authorized.IsAuthorized && authorized.AllowedSiteReferences.SetEquals(new[] { "site-a" }),
                "an active exact user claim must authorize only its explicit Site");
            InsertMembership(databasePath, adminId, "site-a", "REVOKED", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000, "direct-a", 2);
            Assert(store.Resolve(admin).State == "DENIED",
                "a newer REVOKED revision must not be overridden by an older ACTIVE revision from the same source");
            InsertMembership(databasePath, adminId, "site-a", "ACTIVE", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000, "direct-a", 3);
            Assert(store.Resolve(admin).IsAuthorized && store.Resolve(admin).AllowedSiteReferences.SetEquals(new[] { "site-a" }),
                "a newer ACTIVE revision must become the effective revision for the same source");
            InsertMembership(databasePath, adminId, "site-b", "ACTIVE", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000, "group-b", 1);
            var multiSource = store.Resolve(admin);
            Assert(multiSource.IsAuthorized && multiSource.AllowedSiteReferences.SetEquals(new[] { "site-a", "site-b" }),
                "independent active sources must retain their own Site grants");
            using (var connection = Open(databasePath))
            using (var update = connection.CreateCommand())
            {
                update.CommandText = "UPDATE web_user_site_memberships SET valid_to_ms=$expired WHERE user_id=$user";
                update.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 100);
                update.Parameters.AddWithValue("$user", adminId);
                update.ExecuteNonQuery();
            }
            Assert(store.Resolve(admin).State == "DENIED", "expired Site claims must not authorize a user");
            InsertMembership(databasePath, adminId, "site-a", "CONFLICT", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000, "conflicting-source", 2);
            Assert(store.Resolve(admin).State == "CONFLICT", "an explicit conflict claim at the effective revision must fail closed");
            Assert(store.Resolve(new WebUserIdentity("root", "superadmin", "SUPERADMIN", "isolated-session")).IsGlobal,
                "SUPERADMIN must retain the explicit global identity contract");
            Console.WriteLine("PASS: P2B5 Site Scope migration, stable web user subject, active/expired/conflict claims and SUPERADMIN global scope");
        }
        finally
        {
            Environment.SetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable, previousV13Gate);
            Environment.SetEnvironmentVariable(P2B5SiteScopeMigrationContext.ExplicitMigrationEnvironmentVariable, previousSiteScopeGate);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void InsertMembership(string databasePath, string userId, string site, string state,
        long from, long to, string sourceReference, long revision)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO web_user_site_memberships
                (id,user_id,site_reference,claim_state,source_kind,source_reference,revision,lifecycle,valid_from_ms,valid_to_ms,created_ms,updated_ms)
            VALUES($id,$user,$site,$state,'DIRECT',$source,$revision,'ACTIVE',$from,$to,$now,$now)
            """;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$site", site);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$source", sourceReference);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void Assert(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }
}

internal static class P2B5SiteScopeMigrationChild
{
    public static Task RunAsync(string[] arguments)
    {
        if (arguments.Length != 1) throw new ArgumentException("Expected: --p2b5-site-scope-migrate <database-path>.");
        var databasePath = Path.GetFullPath(arguments[0]);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        var result = P2B5SiteScopeMigration.Apply(connection, P2B5SiteScopeMigrationContext.ForIsolatedTest(databasePath));
        Console.WriteLine($"P2B5SiteScopeMigration: AlreadyApplied = {result.AlreadyApplied}, BaseSchemaVersion = {result.BaseSchemaVersion}");
        return Task.CompletedTask;
    }
}
