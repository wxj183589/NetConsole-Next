using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal enum P2B2MigrationFaultPoint
{
    NONE,
    BEFORE_TRANSACTION,
    AFTER_SCHEMA,
    AFTER_FIRST_BACKFILL,
    AFTER_BACKFILL_BEFORE_VERSION,
    AFTER_VERSION_BEFORE_COMMIT,
    AFTER_COMMIT
}

internal sealed record P2B2MigrationContext(
    string DatabasePath,
    bool IsolatedTest,
    P2B2MigrationFaultPoint FaultPoint = P2B2MigrationFaultPoint.NONE,
    bool CrashOnFault = false)
{
    public const string ExplicitMigrationEnvironmentVariable = "NETCONSOLE_NEXT_P2B_V13_MIGRATION_ALLOWED";

    public static P2B2MigrationContext ForIsolatedTest(string databasePath,
        P2B2MigrationFaultPoint faultPoint = P2B2MigrationFaultPoint.NONE,
        bool crashOnFault = false) => new(databasePath, true, faultPoint, crashOnFault);

    public void Validate(SqliteConnection connection)
    {
        if (!IsolatedTest)
            throw new InvalidOperationException("P2B-2 migration is not authorized outside an isolated test context.");
        if (!string.Equals(Environment.GetEnvironmentVariable(ExplicitMigrationEnvironmentVariable), "1", StringComparison.Ordinal))
            throw new InvalidOperationException($"Set {ExplicitMigrationEnvironmentVariable}=1 only for an isolated migration test.");

        var databasePath = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(DatabasePath, "migration context");
        var tempRoot = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(Path.GetTempPath(), "temporary root");
        P2B2MigrationPathSecurity.ValidateTemporaryDatabaseBoundary(databasePath, tempRoot);
        P2B2MigrationPathSecurity.ValidateNoReparsePoints(tempRoot);
        P2B2MigrationPathSecurity.ValidateNoReparsePoints(databasePath);

        var connectionPath = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(connection.DataSource, "open connection");
        if (!P2B2MigrationPathSecurity.PathsEqual(connectionPath, databasePath))
            throw new InvalidOperationException("Migration context database path does not match the open connection.");
    }
}

internal static class P2B2MigrationPathSecurity
{
    public static string CanonicalizeDatabasePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"P2B-2 {label} must be an absolute filesystem path.");

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            throw new InvalidOperationException($"P2B-2 {label} is not a valid filesystem path.", exception);
        }
    }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);

    public static void ValidateTemporaryDatabaseBoundary(string databasePath, string tempRoot)
    {
        var rootWithSeparator = tempRoot.EndsWith(Path.DirectorySeparatorChar) ||
                                tempRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? tempRoot
            : tempRoot + Path.DirectorySeparatorChar;
        if (!PathsEqual(databasePath, tempRoot) &&
            !databasePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("P2B-2 migration authorization is restricted to a temporary isolated database.");
    }

    public static void ValidateNoReparsePoints(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"P2B-2 migration path contains a symlink, junction or reparse point: {current}");
            }
            catch (FileNotFoundException)
            {
                // The database file may not exist yet; its existing parents are still checked.
            }
            catch (DirectoryNotFoundException)
            {
                // The database directory may be created by the isolated fixture; its existing parents are checked.
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException($"P2B-2 migration path cannot be inspected safely: {current}", exception);
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || PathsEqual(parent, current))
                break;
            current = parent;
        }
    }
}

internal sealed record P2B2MigrationResult(
    bool AlreadyApplied,
    int SchemaVersion,
    int LegacyBackfilled,
    int PendingDomainCount,
    int ConflictCount);

internal static class P2B2SchemaV13Migration
{
    public static P2B2MigrationResult Apply(SqliteConnection connection, P2B2MigrationContext context)
    {
        context.Validate(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("The migration connection must already be open.");

        var currentVersion = ReadSchemaVersion(connection);
        if (currentVersion == 13)
        {
            ValidateV13Schema(connection);
            return new(true, 13, ReadInt(connection, "SELECT COUNT(*) FROM device_addresses"),
                ReadInt(connection, "SELECT COUNT(*) FROM legacy_address_resolution WHERE state='LEGACY_ADDRESS_PENDING_DOMAIN'"),
                ReadInt(connection, "SELECT COUNT(*) FROM legacy_address_resolution WHERE state='LEGACY_ADDRESS_CONFLICT'"));
        }
        if (currentVersion != 12)
            throw new InvalidDataException($"P2B-2 migration requires Schema v12, found {currentVersion}.");

        Fault(context, P2B2MigrationFaultPoint.BEFORE_TRANSACTION);
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            ExecuteEmbeddedSchema(connection, transaction);
            Fault(context, P2B2MigrationFaultPoint.AFTER_SCHEMA);

            var backfill = BackfillLegacyAddresses(connection, transaction, context);
            Fault(context, P2B2MigrationFaultPoint.AFTER_BACKFILL_BEFORE_VERSION);

            using (var version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = "INSERT INTO schema_versions(version,applied_ms) VALUES(13,$now)";
                version.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                version.ExecuteNonQuery();
            }
            Fault(context, P2B2MigrationFaultPoint.AFTER_VERSION_BEFORE_COMMIT);
            transaction.Commit();
            Fault(context, P2B2MigrationFaultPoint.AFTER_COMMIT);
            return new(false, 13, backfill.Backfilled, backfill.PendingDomainCount, backfill.ConflictCount);
        }
        catch
        {
            try { transaction.Rollback(); }
            catch { /* The process may already be in a failed SQLite state. */ }
            throw;
        }
    }

    public static void ValidateV13Schema(SqliteConnection connection)
    {
        ValidateV13CompatibleSchema(connection, allowV14: false);
    }

    public static void ValidateV13ReadSchema(SqliteConnection connection)
    {
        ValidateV13CompatibleSchema(connection, allowV14: true);
    }

    private static void ValidateV13CompatibleSchema(SqliteConnection connection, bool allowV14)
    {
        var requiredTables = new[]
        {
            "network_domains", "network_domain_scopes", "device_domain_memberships",
            "device_addresses", "device_address_claims", "legacy_address_resolution",
            "vlan_plans", "vlan_assignments"
        };
        foreach (var table in requiredTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
            command.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException($"Schema v13 table is missing: {table}");
        }
        var version = ReadSchemaVersion(connection);
        if (version != 13 && (!allowV14 || version != 14))
            throw new InvalidDataException(allowV14
                ? "Schema v13-compatible read tables are not registered."
                : "Schema v13 is not registered after migration.");
    }

    private static (int Backfilled, int PendingDomainCount, int ConflictCount) BackfillLegacyAddresses(
        SqliteConnection connection, SqliteTransaction transaction, P2B2MigrationContext context)
    {
        var rows = new List<LegacyAddressRow>();
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT id,management_ip,terminal_profile_id,updated_ms FROM device_inventory WHERE TRIM(management_ip)<>'' ORDER BY id";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var managementIp = reader.GetString(1).Trim();
                var prefix = managementIp.Contains(':', StringComparison.Ordinal) ? 128 : 32;
                var normalized = P2BAddressNormalization.Normalize(managementIp, prefix);
                rows.Add(new LegacyAddressRow(
                    reader.GetString(0), managementIp, reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3), normalized));
            }
        }

        var duplicateKeys = rows.GroupBy(row => row.Normalized.ClaimKey, StringComparer.Ordinal)
            .Where(group => group.Select(row => row.DeviceId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var pending = 0;
        var conflicts = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var duplicate = duplicateKeys.Contains(row.Normalized.ClaimKey);
            var state = duplicate ? "LEGACY_ADDRESS_CONFLICT" : "LEGACY_ADDRESS_PENDING_DOMAIN";
            var domainState = duplicate ? "CONFLICT" : "PENDING_DOMAIN";
            var conflictCode = duplicate ? P2BErrorCodes.AddressAlreadyClaimed : P2BErrorCodes.NetworkDomainNotResolved;
            var claimState = duplicate ? "CONFLICT" : "PENDING_DOMAIN";
            var sourceReference = P2BLegacyManagementIpRules.SourceReference(row.DeviceId);
            var sourceFingerprint = Fingerprint($"{sourceReference}|{row.ManagementIp}|{row.Normalized.ClaimKey}|{row.UpdatedMs}");
            var addressId = $"legacy-address:{row.DeviceId}";
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using (var address = connection.CreateCommand())
            {
                address.Transaction = transaction;
                address.CommandText = """
                    INSERT OR IGNORE INTO device_addresses
                        (id,device_id,network_domain_id,address_family,normalized_address,prefix_length,gateway_address,vrf_name,
                         address_role,interface_reference,source_kind,source_reference,source_fingerprint,observed_at_ms,revision,
                         domain_resolution_state,conflict_state,lifecycle,valid_from_ms,valid_to_ms,created_ms,updated_ms)
                    VALUES($id,$device,NULL,$family,$address,$prefix,NULL,'','MANAGEMENT','', 'IMPORTED',$source,$fingerprint,NULL,1,
                           $domainState,$conflict,'ACTIVE',NULL,NULL,$now,$updated)
                    """;
                AddLegacyParameters(address, row, addressId, sourceReference, sourceFingerprint, domainState, conflictCode, now);
                address.ExecuteNonQuery();
            }
            FaultAfterFirstBackfill(context, index);

            using (var claim = connection.CreateCommand())
            {
                claim.Transaction = transaction;
                claim.CommandText = """
                    INSERT OR IGNORE INTO device_address_claims
                        (id,device_address_id,device_id,network_domain_id,address_family,normalized_address,valid_from_ms,valid_to_ms,
                         lifecycle,claim_state,conflict_code,created_ms,updated_ms)
                    VALUES($id,$addressId,$device,NULL,$family,$address,NULL,NULL,'ACTIVE',$claimState,$conflict,$now,$updated)
                    """;
                claim.Parameters.AddWithValue("$id", $"legacy-claim:{row.DeviceId}");
                claim.Parameters.AddWithValue("$addressId", addressId);
                claim.Parameters.AddWithValue("$device", row.DeviceId);
                claim.Parameters.AddWithValue("$family", row.Normalized.AddressFamily.ToString());
                claim.Parameters.AddWithValue("$address", row.Normalized.NormalizedAddress);
                claim.Parameters.AddWithValue("$claimState", claimState);
                claim.Parameters.AddWithValue("$conflict", conflictCode);
                claim.Parameters.AddWithValue("$now", now);
                claim.Parameters.AddWithValue("$updated", row.UpdatedMs);
                claim.ExecuteNonQuery();
            }

            using (var legacy = connection.CreateCommand())
            {
                legacy.Transaction = transaction;
                legacy.CommandText = """
                    INSERT OR IGNORE INTO legacy_address_resolution
                        (id,device_id,device_address_id,terminal_profile_id,management_ip,normalized_address,source_reference,state,
                         network_domain_id,conflict_detail,created_ms,updated_ms)
                    VALUES($id,$device,$addressId,$profile,$ip,$address,$source,$state,NULL,$detail,$now,$updated)
                    """;
                legacy.Parameters.AddWithValue("$id", $"legacy-resolution:{row.DeviceId}");
                legacy.Parameters.AddWithValue("$device", row.DeviceId);
                legacy.Parameters.AddWithValue("$addressId", addressId);
                legacy.Parameters.AddWithValue("$profile", (object?)row.TerminalProfileId ?? DBNull.Value);
                legacy.Parameters.AddWithValue("$ip", row.ManagementIp);
                legacy.Parameters.AddWithValue("$address", row.Normalized.NormalizedAddress);
                legacy.Parameters.AddWithValue("$source", sourceReference);
                legacy.Parameters.AddWithValue("$state", state);
                legacy.Parameters.AddWithValue("$detail", duplicate ? "DUPLICATE_NORMALIZED_ADDRESS_REVIEW_REQUIRED" : "NETWORK_DOMAIN_REQUIRED");
                legacy.Parameters.AddWithValue("$now", now);
                legacy.Parameters.AddWithValue("$updated", row.UpdatedMs);
                legacy.ExecuteNonQuery();
            }

            if (duplicate) conflicts++; else pending++;
        }
        return (rows.Count, pending, conflicts);
    }

    private static void AddLegacyParameters(SqliteCommand command, LegacyAddressRow row, string addressId,
        string sourceReference, string sourceFingerprint, string domainState, string conflictCode, long now)
    {
        command.Parameters.AddWithValue("$id", addressId);
        command.Parameters.AddWithValue("$device", row.DeviceId);
        command.Parameters.AddWithValue("$family", row.Normalized.AddressFamily.ToString());
        command.Parameters.AddWithValue("$address", row.Normalized.NormalizedAddress);
        command.Parameters.AddWithValue("$prefix", row.Normalized.PrefixLength);
        command.Parameters.AddWithValue("$source", sourceReference);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint);
        command.Parameters.AddWithValue("$domainState", domainState);
        command.Parameters.AddWithValue("$conflict", conflictCode);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$updated", row.UpdatedMs);
    }

    private static void ExecuteEmbeddedSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var resource = typeof(P2B2SchemaV13Migration).Assembly.GetManifestResourceStream("schema_v13.sql")
            ?? throw new InvalidOperationException("Embedded schema_v13.sql was not found.");
        using var reader = new StreamReader(resource);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
    }

    private static int ReadSchemaVersion(SqliteConnection connection) =>
        ReadInt(connection, "SELECT CASE WHEN EXISTS(SELECT 1 FROM schema_versions WHERE version=13) THEN 13 ELSE COALESCE(MAX(CASE WHEN version <= 12 THEN version END),0) END FROM schema_versions");

    private static int ReadInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Fault(P2B2MigrationContext context, P2B2MigrationFaultPoint point)
    {
        if (context.FaultPoint != point) return;
        if (context.CrashOnFault) Environment.FailFast($"P2B2 migration crash injection: {point}");
        throw new InvalidOperationException($"P2B2 migration fault injection: {point}");
    }

    private static void FaultAfterFirstBackfill(P2B2MigrationContext context, int index)
    {
        if (index == 0) Fault(context, P2B2MigrationFaultPoint.AFTER_FIRST_BACKFILL);
    }

    private sealed record LegacyAddressRow(
        string DeviceId,
        string ManagementIp,
        string? TerminalProfileId,
        long UpdatedMs,
        P2BNormalizedAddress Normalized);
}
