using System.Net;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Credentials;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class SqliteSecurityTests
{
    private static readonly Version MinimumSqliteVersion = new(3, 50, 2);

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-sqlite-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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

            var catalog = new Catalog(settings);
            catalog.Initialize();
            VerifyVersionAndTransaction(root);
            VerifyWebPasswordReset(root);

            int before;
            using (var connection = OpenCatalog(root))
            using (var query = connection.CreateCommand())
            {
                query.CommandText = "SELECT COUNT(*) FROM schema_versions";
                before = Convert.ToInt32(query.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
            new Catalog(settings).Initialize();
            int after;
            using (var connection = OpenCatalog(root))
            using (var query = connection.CreateCommand())
            {
                query.CommandText = "SELECT COUNT(*) FROM schema_versions";
                after = Convert.ToInt32(query.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
            Assert(before == after, "Catalog initialization must remain idempotent with the secure SQLite provider");

            using (var connection = OpenCatalog(root))
            using (var query = connection.CreateCommand())
            {
                query.CommandText = "PRAGMA quick_check(1)";
                Assert(string.Equals(query.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase),
                    "SQLite quick_check must remain healthy after migration and password reset operations");
            }

            Console.WriteLine("PASS: SQLite secure provider version, transaction/query/write, web-password reset and Catalog migration idempotence");
        }
        finally
        {
            DeleteTestDirectoryWithRetry(root);
        }
    }

    private static void VerifyVersionAndTransaction(string root)
    {
        using var connection = OpenCatalog(root);
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "SELECT sqlite_version()";
            var text = version.ExecuteScalar()?.ToString() ?? throw new InvalidOperationException("SQLite version query returned no value");
            var parsed = Version.Parse(text);
            Assert(parsed >= MinimumSqliteVersion, $"SQLite runtime {parsed} is below the security baseline {MinimumSqliteVersion}");
            Console.WriteLine($"SQLITE_RUNTIME_VERSION={text}");
        }

        using var transaction = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = "CREATE TABLE security_provider_probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO security_provider_probe(value) VALUES($value)";
            insert.Parameters.AddWithValue("$value", "secure-provider");
            Assert(insert.ExecuteNonQuery() == 1, "SQLite transaction write must affect one row");
        }
        transaction.Commit();

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT value FROM security_provider_probe WHERE id=1";
        Assert(string.Equals(read.ExecuteScalar()?.ToString(), "secure-provider", StringComparison.Ordinal),
            "SQLite transaction write must be readable after commit");
    }

    private static void VerifyWebPasswordReset(string root)
    {
        string userId;
        using (var connection = OpenCatalog(root))
        {
            using (var find = connection.CreateCommand())
            {
                find.CommandText = "SELECT id FROM web_users WHERE username='admin'";
                userId = find.ExecuteScalar()?.ToString() ?? throw new InvalidOperationException("Catalog must seed the admin user");
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO web_sessions
                    (id,user_id,token_hash,csrf_hash,created_ms,last_seen_ms,expires_ms,remote_ip)
                VALUES($id,$user,$token,$csrf,$now,$now,$expires,$ip)
                """;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$user", userId);
            insert.Parameters.AddWithValue("$token", Guid.NewGuid().ToByteArray());
            insert.Parameters.AddWithValue("$csrf", Guid.NewGuid().ToByteArray());
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$expires", now + 60_000);
            insert.Parameters.AddWithValue("$ip", "127.0.0.1");
            insert.ExecuteNonQuery();
        }

        var result = WebPasswordRecovery.Reset(root, "admin", "sqlite-security-test-password", "sqlite-security-test");
        Assert(result.Username == "admin" && result.RevokedSessions == 1, "web-password reset must update the user and revoke the active session");

        using var verify = OpenCatalog(root);
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT revoked_ms FROM web_sessions WHERE user_id=$user";
        query.Parameters.AddWithValue("$user", userId);
        Assert(query.ExecuteScalar() is not null and not DBNull, "web-password reset must persist session revocation");
        query.Parameters.Clear();
        query.CommandText = "SELECT result FROM web_password_reset_audit WHERE username='admin' ORDER BY at_ms DESC LIMIT 1";
        Assert(string.Equals(query.ExecuteScalar()?.ToString(), "RESET", StringComparison.Ordinal),
            "web-password reset must persist its audit record");
    }

    private static SqliteConnection OpenCatalog(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "catalog.db"),
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Assert(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private static void DeleteTestDirectoryWithRetry(string root)
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        for (var attempt = 0; attempt < 100 && Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                Thread.Sleep(100);
            }
        }

        if (Directory.Exists(root))
            Console.Error.WriteLine($"WARN: SQLite security test root cleanup was delayed by a Windows file lock: {root}");
    }
}
