using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

// Internal read boundary for the candidate v13 store. Public API and UI work
// remain deliberately out of scope for P2B-2.
internal sealed record P2B2SchemaV13Snapshot(
    int SchemaVersion,
    long NetworkDomainCount,
    long NetworkDomainScopeCount,
    long DeviceDomainMembershipCount,
    long DeviceAddressCount,
    long DeviceAddressClaimCount,
    long LegacyResolutionCount,
    long PendingDomainCount,
    long ConflictCount,
    long VlanPlanCount,
    long VlanAssignmentCount,
    long DeviceInventoryCount,
    long TerminalProfileCount,
    long CollectionSourceCount,
    long TerminalSessionCount,
    long DeviceOperationCount,
    long AuditEventCount);

internal sealed record P2B2DeviceAddressSnapshot(
    string Id,
    string DeviceId,
    string? NetworkDomainId,
    string AddressFamily,
    string NormalizedAddress,
    int PrefixLength,
    string AddressRole,
    string SourceKind,
    string SourceReference,
    string SourceFingerprint,
    string DomainResolutionState,
    string ConflictState,
    string Lifecycle,
    long Revision);

internal sealed record P2B2LegacyResolutionSnapshot(
    string Id,
    string DeviceId,
    string DeviceAddressId,
    string? TerminalProfileId,
    string ManagementIp,
    string NormalizedAddress,
    string SourceReference,
    string State,
    string? NetworkDomainId,
    string ConflictDetail);

internal sealed class P2B2SchemaRepository
{
    private readonly SqliteConnection _connection;

    public P2B2SchemaRepository(SqliteConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("The P2B-2 repository requires an open connection.");
        _connection = connection;
    }

    public P2B2SchemaV13Snapshot ReadSnapshot()
    {
        return new(
            checked((int)ReadInt("SELECT COALESCE(MAX(CASE WHEN version <= 13 THEN version END),0) FROM schema_versions")),
            ReadInt("SELECT COUNT(*) FROM network_domains"),
            ReadInt("SELECT COUNT(*) FROM network_domain_scopes"),
            ReadInt("SELECT COUNT(*) FROM device_domain_memberships"),
            ReadInt("SELECT COUNT(*) FROM device_addresses"),
            ReadInt("SELECT COUNT(*) FROM device_address_claims"),
            ReadInt("SELECT COUNT(*) FROM legacy_address_resolution"),
            ReadInt("SELECT COUNT(*) FROM legacy_address_resolution WHERE state='LEGACY_ADDRESS_PENDING_DOMAIN'"),
            ReadInt("SELECT COUNT(*) FROM legacy_address_resolution WHERE state='LEGACY_ADDRESS_CONFLICT'"),
            ReadInt("SELECT COUNT(*) FROM vlan_plans"),
            ReadInt("SELECT COUNT(*) FROM vlan_assignments"),
            ReadInt("SELECT COUNT(*) FROM device_inventory"),
            ReadInt("SELECT COUNT(*) FROM h3c_device_profiles"),
            ReadInt("SELECT COUNT(*) FROM collection_sources"),
            ReadInt("SELECT COUNT(*) FROM terminal_sessions"),
            ReadInt("SELECT COUNT(*) FROM device_operations"),
            ReadInt("SELECT COUNT(*) FROM audit_events"));
    }

    public IReadOnlyList<P2B2DeviceAddressSnapshot> ReadDeviceAddresses(string? deviceId = null)
    {
        using var command = CreateCommand("""
            SELECT id,device_id,network_domain_id,address_family,normalized_address,prefix_length,address_role,
                   source_kind,source_reference,source_fingerprint,domain_resolution_state,conflict_state,lifecycle,revision
            FROM device_addresses
            WHERE ($device IS NULL OR device_id=$device)
            ORDER BY id
            """);
        command.Parameters.AddWithValue("$device", (object?)deviceId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<P2B2DeviceAddressSnapshot>();
        while (reader.Read())
        {
            result.Add(new(
                reader.GetString(0), reader.GetString(1), GetNullableString(reader, 2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                reader.GetString(12), reader.GetInt64(13)));
        }
        return result;
    }

    public IReadOnlyList<P2B2LegacyResolutionSnapshot> ReadLegacyResolutions()
    {
        using var command = CreateCommand("""
            SELECT id,device_id,device_address_id,terminal_profile_id,management_ip,normalized_address,
                   source_reference,state,network_domain_id,conflict_detail
            FROM legacy_address_resolution ORDER BY id
            """);
        using var reader = command.ExecuteReader();
        var result = new List<P2B2LegacyResolutionSnapshot>();
        while (reader.Read())
        {
            result.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), GetNullableString(reader, 3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                GetNullableString(reader, 8), reader.GetString(9)));
        }
        return result;
    }

    public IReadOnlyList<(string Id, string Name, string ScopeResolution)> ReadNetworkDomains()
    {
        using var command = CreateCommand("SELECT id,name,scope_resolution FROM network_domains ORDER BY id");
        using var reader = command.ExecuteReader();
        var result = new List<(string, string, string)>();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    private SqliteCommand CreateCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private long ReadInt(string sql)
    {
        using var command = CreateCommand(sql);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
