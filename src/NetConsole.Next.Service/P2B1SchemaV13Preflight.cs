using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record P2B1SchemaV13PreflightReport(
    string SchemaV12Health,
    long LegacyDeviceCount,
    long LegacyAddressCount,
    long MissingSiteReferences,
    long DuplicateAddressClaims,
    long UnresolvedDomainCount,
    long ExpectedV13BackfillCount,
    IReadOnlyList<string> MigrationBlockers,
    int SchemaVersion,
    string JournalMode,
    IReadOnlyList<string> ForeignKeyErrors)
{
    public bool IsReadyForSchemaV13Design =>
        string.Equals(SchemaV12Health, "PASS", StringComparison.Ordinal) &&
        MissingSiteReferences == 0 &&
        ForeignKeyErrors.Count == 0 &&
        MigrationBlockers.Count == 0;
}

/// <summary>
/// Read-only inspection of a synthetic or explicitly supplied v12 catalog.
/// This class deliberately does not create tables, update rows, or register a migration.
/// </summary>
internal static class P2B1SchemaV13Preflight
{
    public static P2B1SchemaV13PreflightReport Analyze(SqliteConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("The preflight connection must already be open.");

        var schemaVersion = checked((int)ReadInt(connection, "SELECT COALESCE(MAX(version),0) FROM schema_versions"));
        var journalMode = ReadText(connection, "PRAGMA journal_mode") ?? "unknown";
        var quickCheck = ReadText(connection, "PRAGMA quick_check(1)") ?? "unknown";
        var foreignKeyErrors = ReadForeignKeyErrors(connection);
        var blockers = new List<string>();

        if (schemaVersion != 12) blockers.Add($"SCHEMA_VERSION_EXPECTED_V12:{schemaVersion}");
        if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase)) blockers.Add("SCHEMA_V12_HEALTH");
        if (foreignKeyErrors.Count > 0) blockers.Add("FOREIGN_KEY_CHECK");

        var legacyDeviceCount = ReadInt(connection, "SELECT COUNT(*) FROM device_inventory");
        var legacyAddressCount = ReadInt(connection, "SELECT COUNT(*) FROM device_inventory WHERE TRIM(management_ip)<>''");
        var missingSiteReferences = ReadInt(connection, """
            SELECT COUNT(*)
            FROM device_inventory d
            LEFT JOIN sites s ON s.id=d.site_id
            WHERE s.id IS NULL
            """);
        var duplicateAddressClaims = ReadInt(connection, """
            SELECT COUNT(*) FROM (
                SELECT TRIM(management_ip)
                FROM device_inventory
                WHERE TRIM(management_ip)<>''
                GROUP BY TRIM(management_ip)
                HAVING COUNT(*)>1
            )
            """);

        // v12 has no Network Domain column. Every non-empty legacy management IP
        // therefore needs an explicit domain resolution or a pending staging state.
        var unresolvedDomainCount = legacyAddressCount;
        var expectedV13BackfillCount = legacyAddressCount;

        if (missingSiteReferences > 0) blockers.Add("MISSING_SITE_REFERENCES");
        if (duplicateAddressClaims > 0) blockers.Add("LEGACY_DUPLICATE_ADDRESS_CLAIM_REVIEW");

        return new P2B1SchemaV13PreflightReport(
            string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase) &&
            schemaVersion == 12 && foreignKeyErrors.Count == 0 ? "PASS" : "BLOCKED",
            legacyDeviceCount,
            legacyAddressCount,
            missingSiteReferences,
            duplicateAddressClaims,
            unresolvedDomainCount,
            expectedV13BackfillCount,
            blockers,
            schemaVersion,
            journalMode,
            foreignKeyErrors);
    }

    private static long ReadInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ReadText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    private static IReadOnlyList<string> ReadForeignKeyErrors(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        var errors = new List<string>();
        while (reader.Read())
            errors.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
        return errors;
    }
}
