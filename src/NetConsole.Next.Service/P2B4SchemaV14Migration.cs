using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record P2B4MigrationContext(string DatabasePath, string DataRoot, bool IsolatedTest)
{
    public const string AllowedEnvironmentVariable = "NETCONSOLE_NEXT_P2B4_MIGRATION_ALLOWED";
    public const string TestAuthEnvironmentVariable = "NETCONSOLE_NEXT_P2B4_TEST_AUTH";

    public static P2B4MigrationContext ForIsolatedTest(string databasePath, string dataRoot) =>
        new(databasePath, dataRoot, true);

    public void Validate(SqliteConnection connection)
    {
        if (!IsolatedTest)
            throw new InvalidOperationException("P2B-4 migration is not authorized outside an isolated test context.");
        if (!string.Equals(Environment.GetEnvironmentVariable(AllowedEnvironmentVariable), "1", StringComparison.Ordinal) ||
            !string.Equals(Environment.GetEnvironmentVariable(TestAuthEnvironmentVariable), "1", StringComparison.Ordinal))
            throw new InvalidOperationException($"Set {AllowedEnvironmentVariable}=1 and {TestAuthEnvironmentVariable}=1 only for an isolated migration test.");

        var databasePath = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(DatabasePath, "migration context");
        var dataRoot = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(DataRoot, "data root");
        var tempRoot = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(Path.GetTempPath(), "temporary root");
        P2B2MigrationPathSecurity.ValidateTemporaryDatabaseBoundary(databasePath, tempRoot);
        P2B2MigrationPathSecurity.ValidateTemporaryDatabaseBoundary(dataRoot, tempRoot);
        P2B2MigrationPathSecurity.ValidateNoReparsePoints(tempRoot);
        P2B2MigrationPathSecurity.ValidateNoReparsePoints(dataRoot);
        P2B2MigrationPathSecurity.ValidateNoReparsePoints(databasePath);

        var connectionPath = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(connection.DataSource, "open connection");
        if (!P2B2MigrationPathSecurity.PathsEqual(connectionPath, databasePath))
            throw new InvalidOperationException("P2B-4 migration context database path does not match the open connection.");
    }
}

internal sealed record P2B4MigrationResult(bool AlreadyApplied, int SchemaVersion, string AuthorityRoot);

internal static class P2B4SchemaV14Migration
{
    public static P2B4MigrationResult Apply(SqliteConnection connection, P2B4MigrationContext context)
    {
        context.Validate(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("The migration connection must already be open.");

        var version = ReadSchemaVersion(connection);
        if (version == 14)
        {
            ValidateV14Schema(connection, context.DataRoot);
            return new(true, 14, ReadAuthorityRoot(connection));
        }
        if (version != 13)
            throw new InvalidDataException($"P2B-4 migration requires Schema v13, found {version}.");

        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            ExecuteEmbeddedSchema(connection, transaction);
            using (var authority = connection.CreateCommand())
            {
                authority.Transaction = transaction;
                authority.CommandText = @"INSERT INTO p2b4_authority(id,mode,root_path,migration_authorized,created_ms)
                                          VALUES(1,'ISOLATED_TEST',$root,1,$now)";
                authority.Parameters.AddWithValue("$root", P2B2MigrationPathSecurity.CanonicalizeDatabasePath(context.DataRoot, "data root"));
                authority.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                authority.ExecuteNonQuery();
            }
            using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = "INSERT INTO schema_versions(version,applied_ms) VALUES(14,$now)";
                versionCommand.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                versionCommand.ExecuteNonQuery();
            }
            transaction.Commit();
            ValidateV14Schema(connection, context.DataRoot);
            return new(false, 14, ReadAuthorityRoot(connection));
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
    }

    public static void ValidateV14Schema(SqliteConnection connection, string dataRoot)
    {
        var requiredTables = new[]
        {
            "p2b4_authority", "p2b4_operations", "p2b4_operation_items", "p2b4_address_history",
            "p2b4_vlan_history", "network_reviews", "network_import_runs"
        };
        foreach (var table in requiredTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
            command.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException($"Schema v14 table is missing: {table}");
        }
        if (ReadSchemaVersion(connection) != 14)
            throw new InvalidDataException("Schema v14 is not registered after migration.");
        var authorityRoot = ReadAuthorityRoot(connection);
        var expectedRoot = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(dataRoot, "data root");
        if (!P2B2MigrationPathSecurity.PathsEqual(authorityRoot, expectedRoot))
            throw new InvalidDataException("P2B-4 authority root does not match the isolated data root.");
    }

    public static bool HasV14(SqliteConnection connection)
    {
        try { return ReadSchemaVersion(connection) == 14; }
        catch (SqliteException) { return false; }
    }

    public static string ReadAuthorityRoot(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT root_path FROM p2b4_authority WHERE id=1 AND mode='ISOLATED_TEST' AND migration_authorized=1";
        return command.ExecuteScalar() as string ?? throw new InvalidDataException("P2B-4 isolated authority is missing.");
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(CASE WHEN version <= 14 THEN version END),0) FROM schema_versions";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ExecuteEmbeddedSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var resource = typeof(P2B4SchemaV14Migration).Assembly.GetManifestResourceStream("schema_v14.sql")
            ?? throw new InvalidOperationException("Embedded schema_v14.sql was not found.");
        using var reader = new StreamReader(resource);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
    }
}
