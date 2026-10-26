using System.Text;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed class P2B3SchemaUnavailableException(string reason) : Exception(reason);
internal sealed class P2B3QueryException(string message) : Exception(message);
internal sealed record P2B3CursorPayload(string Id, string FilterFingerprint);

internal static class P2B3ReadApiFeatureGate
{
    public static bool IsEnabled() => P2BFeatureGate.FromEnvironment().Enabled;
}

internal sealed record P2B3PageOptions(int Limit, string? Cursor)
{
    public const int DefaultLimit = 20;
    public const int MaximumLimit = 100;

    public static P2B3PageOptions From(HttpRequest request)
    {
        var rawLimit = request.Query["limit"].ToString();
        var limit = string.IsNullOrWhiteSpace(rawLimit)
            ? DefaultLimit
            : int.TryParse(rawLimit, out var parsed) ? parsed : throw new P2B3QueryException("limit must be an integer.");
        if (limit is < 1 or > MaximumLimit)
            throw new P2B3QueryException($"limit must be between 1 and {MaximumLimit}.");

        var rawCursor = request.Query["cursor"].ToString();
        return new(limit, string.IsNullOrWhiteSpace(rawCursor) ? null : DecodeCursor(rawCursor));
    }

    public static string EncodeCursor(string id, string filterFingerprint) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new P2B3CursorPayload(id, filterFingerprint))))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? CursorId(string? cursor, string filterFingerprint)
    {
        if (cursor is null) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<P2B3CursorPayload>(cursor)
                ?? throw new P2B3QueryException("cursor is invalid.");
            if (string.IsNullOrWhiteSpace(payload.Id) || !string.Equals(payload.FilterFingerprint, filterFingerprint, StringComparison.Ordinal))
                throw new P2B3QueryException("cursor does not match the current filters.");
            return payload.Id;
        }
        catch (JsonException)
        {
            throw new P2B3QueryException("cursor is invalid.");
        }
    }

    private static string DecodeCursor(string value)
    {
        if (value.Length > 512) throw new P2B3QueryException("cursor is too long.");
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            if (string.IsNullOrWhiteSpace(decoded) || decoded.Any(char.IsControl))
                throw new P2B3QueryException("cursor is invalid.");
            return decoded;
        }
        catch (FormatException exception)
        {
            throw new P2B3QueryException("cursor is invalid.") { Source = exception.Source };
        }
    }
}

internal sealed record P2B3ReadPage<T>(
    int Limit,
    long Total,
    bool HasMore,
    string? NextCursor,
    IReadOnlyList<T> Items);

internal sealed record P2B3ScopeRead(
    string ScopeKind,
    string ScopeReference,
    string Resolution,
    string SourceKind,
    string SourceReference,
    long Revision);

internal sealed record P2B3AddressRead(
    string Id,
    string DeviceId,
    string? NetworkDomainId,
    string AddressFamily,
    string NormalizedAddress,
    int PrefixLength,
    string? GatewayAddress,
    string VrfName,
    string AddressRole,
    string InterfaceReference,
    string SourceKind,
    string SourceReference,
    long Revision,
    long? ObservedAtMs,
    long? ValidFromMs,
    long? ValidToMs,
    string DomainResolutionState,
    string ConflictState,
    string Lifecycle,
    string? EffectiveSourceKind,
    string? EffectiveSourceReference,
    string EffectiveState);

internal sealed record P2B3MembershipRead(
    string Id,
    string DeviceId,
    string NetworkDomainId,
    string DomainName,
    string DomainKind,
    string MembershipRole,
    string SourceKind,
    string SourceReference,
    long Revision,
    string Lifecycle,
    string ScopeResolution,
    IReadOnlyList<P2B3ScopeRead> Scopes,
    string ConflictState);

internal sealed record P2B3DomainRead(
    string Id,
    string Name,
    string DomainKind,
    string? ParentDomainId,
    string VrfName,
    string SourceKind,
    string SourceReference,
    long Revision,
    string Lifecycle,
    string ScopeResolution,
    IReadOnlyList<P2B3ScopeRead> Scopes,
    string AuthorizationState);

internal sealed record P2B3VlanPlanRead(
    string Id,
    string NetworkDomainId,
    int VlanId,
    string Name,
    string Purpose,
    string TaggingMode,
    int? NativeVlanId,
    int? Pvid,
    int? QinQOuterId,
    int? QinQInnerId,
    string ScopeResolution,
    string SourceKind,
    string SourceReference,
    long Revision,
    string Lifecycle,
    long? ValidFromMs,
    long? ValidToMs,
    string ConflictState,
    string EffectiveState,
    string? EffectiveSourceKind,
    string? EffectiveSourceReference);

internal sealed record P2B3DiffFact(
    string Id,
    string SourceKind,
    string SourceReference,
    long Revision,
    string? NetworkDomainId,
    string InterfaceReference,
    string Value,
    string ConflictState,
    string DomainResolutionState,
    string Lifecycle,
    long? ValidFromMs,
    long? ValidToMs,
    int FactCount,
    bool Incomplete);

internal sealed record P2B3DiffItem(
    string Key,
    string Status,
    string EffectiveState,
    string? EffectiveSourceKind,
    string? EffectiveSourceReference,
    IReadOnlyList<P2B3DiffFact> Planned,
    IReadOnlyList<P2B3DiffFact> Observed);

internal sealed record P2B3NetworkDiffRead(
    string DeviceId,
    long? AsOfMs,
    bool Truncated,
    IReadOnlyList<P2B3DiffItem> AddressItems,
    IReadOnlyList<P2B3DiffItem> VlanItems);

internal sealed class P2B3ReadRepository
{
    private readonly RuntimeSettings _settings;
    private string DatabasePath => Path.Combine(_settings.DataRoot, "catalog.db");

    public P2B3ReadRepository(RuntimeSettings settings) => _settings = settings;

    public bool IsV13Available()
    {
        try
        {
            using var connection = OpenReadOnly();
            EnsureV13Schema(connection);
            return true;
        }
        catch (P2B3SchemaUnavailableException) { return false; }
        catch (SqliteException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public bool DeviceExists(string deviceId)
    {
        using var connection = OpenV13();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM device_inventory WHERE id=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", RequiredId(deviceId, "device id"));
        return command.ExecuteScalar() is not null;
    }

    public P2B3ReadPage<P2B3AddressRead> ReadAddresses(string deviceId, P2B3PageOptions page,
        string? domainId, string? sourceKind, string? addressFamily, string? lifecycle)
    {
        using var connection = OpenV13();
        var device = RequiredId(deviceId, "device id");
        var filters = "a.device_id=$device" +
                      " AND ($domain IS NULL OR a.network_domain_id=$domain)" +
                      " AND ($source IS NULL OR a.source_kind=$source)" +
                      " AND ($family IS NULL OR a.address_family=$family)" +
                      " AND ($lifecycle IS NULL OR a.lifecycle=$lifecycle)";
        var cursorFingerprint = CursorFingerprint("addresses", device, domainId, sourceKind, addressFamily, lifecycle);
        var cursor = P2B3PageOptions.CursorId(page.Cursor, cursorFingerprint);
        var total = Count(connection, $"SELECT COUNT(*) FROM device_addresses a WHERE {filters}",
            ("$device", device), ("$domain", NullIfBlank(domainId)), ("$source", NullIfBlank(sourceKind)),
            ("$family", NullIfBlank(addressFamily)), ("$lifecycle", NullIfBlank(lifecycle)));
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH projections AS (
                SELECT device_id,network_domain_id,address_family,normalized_address,prefix_length,address_role,interface_reference,
                       COUNT(*) AS fact_count,
                       COUNT(DISTINCT COALESCE(gateway_address,'')||char(31)||vrf_name) AS value_count,
                       MAX(CASE WHEN conflict_state NOT IN ('NONE','HISTORICAL_REUSE_ALLOWED') THEN 1 ELSE 0 END) AS conflict_flag,
                       MAX(CASE WHEN domain_resolution_state<>'RESOLVED' THEN 1 ELSE 0 END) AS unresolved_flag
                FROM device_addresses
                WHERE ($lifecycle IS NULL OR lifecycle=$lifecycle)
                GROUP BY device_id,network_domain_id,address_family,normalized_address,prefix_length,address_role,interface_reference
            )
            SELECT a.id,a.device_id,a.network_domain_id,a.address_family,a.normalized_address,a.prefix_length,
                   a.gateway_address,a.vrf_name,a.address_role,a.interface_reference,a.source_kind,a.source_reference,
                   a.revision,a.observed_at_ms,a.valid_from_ms,a.valid_to_ms,a.domain_resolution_state,a.conflict_state,a.lifecycle,
                   CASE WHEN p.conflict_flag=1 THEN 'CONFLICT'
                        WHEN p.unresolved_flag=1 THEN 'UNRESOLVED'
                        WHEN p.value_count=1 THEN 'CONSISTENT'
                        ELSE 'CONFLICT' END AS effective_state,
                   CASE WHEN p.fact_count=1 AND p.value_count=1 AND p.conflict_flag=0 AND p.unresolved_flag=0
                        THEN a.source_kind END AS effective_source_kind,
                   CASE WHEN p.fact_count=1 AND p.value_count=1 AND p.conflict_flag=0 AND p.unresolved_flag=0
                        THEN a.source_reference END AS effective_source_reference
            FROM device_addresses a
            JOIN projections p ON p.device_id=a.device_id
                AND p.network_domain_id IS a.network_domain_id
                AND p.address_family=a.address_family AND p.normalized_address=a.normalized_address
                AND p.prefix_length=a.prefix_length AND p.address_role=a.address_role
                AND p.interface_reference=a.interface_reference
            WHERE {filters} AND ($cursor IS NULL OR a.id>$cursor)
            ORDER BY a.id ASC LIMIT $take
            """;
        AddParameters(command, ("$device", device), ("$domain", NullIfBlank(domainId)),
            ("$source", NullIfBlank(sourceKind)), ("$family", NullIfBlank(addressFamily)),
            ("$lifecycle", NullIfBlank(lifecycle)), ("$cursor", cursor), ("$take", page.Limit + 1));
        var rows = new List<(string Id, P2B3AddressRead Value)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var conflict = reader.GetString(17);
            var domainState = reader.GetString(16);
            rows.Add((reader.GetString(0), new(
                reader.GetString(0), reader.GetString(1), NullableString(reader, 2), reader.GetString(3), reader.GetString(4),
                reader.GetInt32(5), NullableString(reader, 6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
                reader.GetString(10), reader.GetString(11), reader.GetInt64(12), NullableInt64(reader, 13), NullableInt64(reader, 14),
                NullableInt64(reader, 15), domainState, conflict, reader.GetString(18),
                NullableString(reader, 20), NullableString(reader, 21), NullableString(reader, 19) ?? "CONFLICT")));
        }
        return ToPage(rows, page, total, cursorFingerprint);
    }

    public P2B3ReadPage<P2B3MembershipRead> ReadMemberships(string deviceId, P2B3PageOptions page,
        string? siteId, string? domainId, string? lifecycle)
    {
        using var connection = OpenV13();
        var device = RequiredId(deviceId, "device id");
        var filters = "m.device_id=$device" +
                      " AND ($domain IS NULL OR m.network_domain_id=$domain)" +
                      " AND ($lifecycle IS NULL OR m.lifecycle=$lifecycle)" +
                      " AND ($site IS NULL OR EXISTS (SELECT 1 FROM network_domain_scopes s WHERE s.network_domain_id=m.network_domain_id AND s.scope_kind='SITE' AND s.scope_reference=$site))";
        var cursorFingerprint = CursorFingerprint("memberships", device, siteId, domainId, lifecycle);
        var cursor = P2B3PageOptions.CursorId(page.Cursor, cursorFingerprint);
        var total = Count(connection, $"SELECT COUNT(*) FROM device_domain_memberships m WHERE {filters}",
            ("$device", device), ("$domain", NullIfBlank(domainId)), ("$lifecycle", NullIfBlank(lifecycle)), ("$site", NullIfBlank(siteId)));
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT m.id,m.device_id,m.network_domain_id,d.name,d.domain_kind,m.membership_role,
                   m.source_kind,m.source_reference,m.revision,m.lifecycle,d.scope_resolution
            FROM device_domain_memberships m JOIN network_domains d ON d.id=m.network_domain_id
            WHERE {filters} AND ($cursor IS NULL OR m.id>$cursor)
            ORDER BY m.id ASC LIMIT $take
            """;
        AddParameters(command, ("$device", device), ("$domain", NullIfBlank(domainId)),
            ("$lifecycle", NullIfBlank(lifecycle)), ("$site", NullIfBlank(siteId)),
            ("$cursor", cursor), ("$take", page.Limit + 1));
        var rows = new List<(string Id, P2B3MembershipRead Value)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var domain = reader.GetString(2);
            var resolution = reader.GetString(10);
            rows.Add((reader.GetString(0), new(reader.GetString(0), reader.GetString(1), domain, reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt64(8), reader.GetString(9), resolution,
                Array.Empty<P2B3ScopeRead>(), resolution == "RESOLVED" ? "NONE" : resolution)));
        }
        reader.Close();
        var scopes = ReadScopes(connection, rows.Select(x => x.Value.NetworkDomainId));
        rows = rows.Select(row => (row.Id, row.Value with { Scopes = scopes.TryGetValue(row.Value.NetworkDomainId, out var domainScopes) ? domainScopes : Array.Empty<P2B3ScopeRead>() })).ToList();
        return ToPage(rows, page, total, cursorFingerprint);
    }

    public P2B3ReadPage<P2B3DomainRead> ReadDomains(P2B3PageOptions page, string? siteId, string? stationId,
        string? lineId, string? sectionId, string? domainKind, string? scopeResolution, string? lifecycle, string? search)
    {
        using var connection = OpenV13();
        var filters = "($site IS NULL OR EXISTS (SELECT 1 FROM network_domain_scopes s WHERE s.network_domain_id=d.id AND s.scope_kind='SITE' AND s.scope_reference=$site))" +
                      " AND ($station IS NULL OR EXISTS (SELECT 1 FROM network_domain_scopes s WHERE s.network_domain_id=d.id AND s.scope_kind='STATION' AND s.scope_reference=$station))" +
                      " AND ($line IS NULL OR EXISTS (SELECT 1 FROM network_domain_scopes s WHERE s.network_domain_id=d.id AND s.scope_kind='LINE' AND s.scope_reference=$line))" +
                      " AND ($section IS NULL OR EXISTS (SELECT 1 FROM network_domain_scopes s WHERE s.network_domain_id=d.id AND s.scope_kind='SECTION' AND s.scope_reference=$section))" +
                      " AND ($kind IS NULL OR d.domain_kind=$kind)" +
                      " AND ($resolution IS NULL OR d.scope_resolution=$resolution)" +
                      " AND ($lifecycle IS NULL OR d.lifecycle=$lifecycle)" +
                      " AND ($search IS NULL OR d.id LIKE $search OR d.name LIKE $search)";
        var cursorFingerprint = CursorFingerprint("domains", siteId, stationId, lineId, sectionId, domainKind, scopeResolution, lifecycle, search);
        var cursor = P2B3PageOptions.CursorId(page.Cursor, cursorFingerprint);
        var parameters = DomainParameters(siteId, stationId, lineId, sectionId, domainKind, scopeResolution, lifecycle, search);
        var total = Count(connection, $"SELECT COUNT(*) FROM network_domains d WHERE {filters}", parameters);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.id,d.name,d.domain_kind,d.parent_domain_id,d.vrf_name,d.source_kind,d.source_reference,
                   d.revision,d.lifecycle,d.scope_resolution
            FROM network_domains d
            WHERE {filters} AND ($cursor IS NULL OR d.id>$cursor)
            ORDER BY d.id ASC LIMIT $take
            """;
        AddParameters(command, parameters);
        AddParameters(command, ("$cursor", cursor), ("$take", page.Limit + 1));
        var rows = new List<(string Id, P2B3DomainRead Value)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var resolution = reader.GetString(9);
            rows.Add((reader.GetString(0), new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7), reader.GetString(8), resolution,
                Array.Empty<P2B3ScopeRead>(), resolution == "RESOLVED" ? "SCOPED" : "UNRESOLVED_OR_CONFLICT")));
        }
        reader.Close();
        var scopes = ReadScopes(connection, rows.Select(x => x.Value.Id));
        rows = rows.Select(row => (row.Id, row.Value with { Scopes = scopes.TryGetValue(row.Value.Id, out var domainScopes) ? domainScopes : Array.Empty<P2B3ScopeRead>() })).ToList();
        return ToPage(rows, page, total, cursorFingerprint);
    }

    public P2B3ReadPage<P2B3VlanPlanRead> ReadVlanPlans(P2B3PageOptions page, string? domainId, string? siteId,
        string? sourceKind, string? scopeResolution, string? lifecycle, int? vlanId, long? asOfMs)
    {
        using var connection = OpenV13();
        var filters = "($domain IS NULL OR p.network_domain_id=$domain)" +
                      " AND ($site IS NULL OR EXISTS (SELECT 1 FROM network_domain_scopes s WHERE s.network_domain_id=p.network_domain_id AND s.scope_kind='SITE' AND s.scope_reference=$site))" +
                      " AND ($source IS NULL OR p.source_kind=$source)" +
                      " AND ($resolution IS NULL OR p.scope_resolution=$resolution)" +
                      " AND ($lifecycle IS NULL OR p.lifecycle=$lifecycle)" +
                      " AND ($vlan IS NULL OR p.vlan_id=$vlan)" +
                      " AND ($asOf IS NULL OR (p.valid_from_ms IS NULL OR p.valid_from_ms<=$asOf) AND (p.valid_to_ms IS NULL OR p.valid_to_ms>$asOf))";
        var cursorFingerprint = CursorFingerprint("vlan-plans", domainId, siteId, sourceKind, scopeResolution, lifecycle,
            vlanId?.ToString(System.Globalization.CultureInfo.InvariantCulture), asOfMs?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var cursor = P2B3PageOptions.CursorId(page.Cursor, cursorFingerprint);
        var parameters = new (string Name, object? Value)[]
        {
            ("$domain", NullIfBlank(domainId)), ("$site", NullIfBlank(siteId)), ("$source", NullIfBlank(sourceKind)),
            ("$resolution", NullIfBlank(scopeResolution)), ("$lifecycle", NullIfBlank(lifecycle)), ("$vlan", vlanId), ("$asOf", asOfMs)
        };
        var total = Count(connection, $"SELECT COUNT(*) FROM vlan_plans p WHERE {filters}", parameters);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.id,p.network_domain_id,p.vlan_id,p.name,p.purpose,p.tagging_mode,p.native_vlan_id,p.pvid,
                   p.qinq_outer_id,p.qinq_inner_id,p.scope_resolution,p.source_kind,p.source_reference,p.revision,
                   p.lifecycle,p.valid_from_ms,p.valid_to_ms,
                   CASE WHEN p.scope_resolution<>'RESOLVED' THEN p.scope_resolution
                        WHEN EXISTS (SELECT 1 FROM vlan_plans p2 WHERE p2.id<>p.id AND p2.network_domain_id=p.network_domain_id
                            AND p2.vlan_id=p.vlan_id AND p2.lifecycle='ACTIVE'
                            AND (p2.valid_from_ms IS NULL OR p.valid_to_ms IS NULL OR p2.valid_from_ms<p.valid_to_ms)
                            AND (p.valid_from_ms IS NULL OR p2.valid_to_ms IS NULL OR p.valid_from_ms<p2.valid_to_ms)
                            AND p2.source_kind<>'OBSERVED' AND p.source_kind<>'OBSERVED') THEN 'VLAN_VALIDITY_OVERLAP'
                        WHEN EXISTS (SELECT 1 FROM vlan_plans p2 WHERE p2.id<>p.id AND p2.network_domain_id=p.network_domain_id
                            AND p2.vlan_id=p.vlan_id AND p2.lifecycle='ACTIVE' AND p2.source_kind='OBSERVED'
                            AND p.source_kind<>'OBSERVED') THEN 'VLAN_PLANNED_OBSERVED_COEXISTENCE'
                        ELSE 'NONE' END AS conflict_state
            FROM vlan_plans p
            WHERE {filters} AND ($cursor IS NULL OR p.id>$cursor)
            ORDER BY p.id ASC LIMIT $take
            """;
        AddParameters(command, parameters);
        AddParameters(command, ("$cursor", cursor), ("$take", page.Limit + 1));
        var rows = new List<(string Id, P2B3VlanPlanRead Value)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var conflict = reader.GetString(17);
            var source = reader.GetString(11);
            var state = conflict == "NONE" ? "CONSISTENT" : "CONFLICT";
            rows.Add((reader.GetString(0), new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), NullableInt32(reader, 6), NullableInt32(reader, 7), NullableInt32(reader, 8), NullableInt32(reader, 9),
                reader.GetString(10), source, reader.GetString(12), reader.GetInt64(13), reader.GetString(14), NullableInt64(reader, 15),
                NullableInt64(reader, 16), conflict, state, state == "CONSISTENT" ? source : null,
                state == "CONSISTENT" ? reader.GetString(12) : null)));
        }
        return ToPage(rows, page, total, cursorFingerprint);
    }

    public P2B3NetworkDiffRead ReadNetworkDiff(string deviceId, string? domainId, string? resolutionState, long? asOfMs)
    {
        using var connection = OpenV13();
        var device = RequiredId(deviceId, "device id");
        var addressFilters = "a.device_id=$device AND ($domain IS NULL OR a.network_domain_id=$domain)" +
            " AND ($resolution IS NULL OR a.domain_resolution_state=$resolution)" +
            " AND ($asOf IS NULL OR (a.valid_from_ms IS NULL OR a.valid_from_ms<=$asOf) AND (a.valid_to_ms IS NULL OR a.valid_to_ms>$asOf))";
        using var address = connection.CreateCommand();
        address.CommandText = $"""
            WITH ranked AS (
                SELECT a.id,a.source_kind,a.source_reference,a.revision,a.network_domain_id,a.interface_reference,
                       a.address_family||':'||a.normalized_address||'/'||a.prefix_length||':'||COALESCE(a.gateway_address,'')||':'||a.vrf_name AS value,
                       a.conflict_state,a.domain_resolution_state,a.lifecycle,a.valid_from_ms,a.valid_to_ms,
                       COUNT(*) OVER (PARTITION BY a.network_domain_id,a.interface_reference) AS fact_count,
                       ROW_NUMBER() OVER (PARTITION BY a.network_domain_id,a.interface_reference ORDER BY a.id ASC) AS fact_rank
                FROM device_addresses a WHERE {addressFilters}
            )
            SELECT id,source_kind,source_reference,revision,network_domain_id,interface_reference,value,
                   conflict_state,domain_resolution_state,lifecycle,valid_from_ms,valid_to_ms,fact_count
            FROM ranked WHERE fact_rank <= $groupTake
            ORDER BY network_domain_id,interface_reference,id
            """;
        AddParameters(address, ("$device", device), ("$domain", NullIfBlank(domainId)), ("$resolution", NullIfBlank(resolutionState)),
            ("$asOf", asOfMs), ("$groupTake", P2B3PageOptions.MaximumLimit));
        var addressFacts = ReadDiffFacts(address);

        using var vlan = connection.CreateCommand();
        var vlanFilters = "v.device_id=$device AND ($domain IS NULL OR v.network_domain_id=$domain)" +
            " AND ($asOf IS NULL OR (v.valid_from_ms IS NULL OR v.valid_from_ms<=$asOf) AND (v.valid_to_ms IS NULL OR v.valid_to_ms>$asOf))";
        vlan.CommandText = $"""
            WITH ranked AS (
                SELECT v.id,v.source_kind,v.source_reference,v.revision,v.network_domain_id,v.interface_reference,
                       v.vlan_id||':'||v.tagging_mode||':'||COALESCE(v.native_vlan_id,'')||':'||COALESCE(v.pvid,'')||':'||
                       COALESCE(v.qinq_outer_id,'')||':'||COALESCE(v.qinq_inner_id,'') AS value,
                       'NONE' AS conflict_state,d.scope_resolution AS domain_resolution_state,v.lifecycle,
                       v.valid_from_ms,v.valid_to_ms,
                       COUNT(*) OVER (PARTITION BY v.network_domain_id,v.interface_reference) AS fact_count,
                       ROW_NUMBER() OVER (PARTITION BY v.network_domain_id,v.interface_reference ORDER BY v.id ASC) AS fact_rank
                FROM vlan_assignments v JOIN network_domains d ON d.id=v.network_domain_id
                WHERE {vlanFilters}
            )
            SELECT id,source_kind,source_reference,revision,network_domain_id,interface_reference,value,
                   conflict_state,domain_resolution_state,lifecycle,valid_from_ms,valid_to_ms,fact_count
            FROM ranked WHERE fact_rank <= $groupTake
            ORDER BY network_domain_id,interface_reference,id
            """;
        AddParameters(vlan, ("$device", device), ("$domain", NullIfBlank(domainId)), ("$asOf", asOfMs),
            ("$groupTake", P2B3PageOptions.MaximumLimit));
        var vlanFacts = ReadDiffFacts(vlan);
        return new(device, asOfMs, addressFacts.Truncated || vlanFacts.Truncated,
            BuildDiffItems(addressFacts.Facts), BuildDiffItems(vlanFacts.Facts));
    }

    private SqliteConnection OpenV13()
    {
        var connection = OpenReadOnly();
        try
        {
            EnsureV13Schema(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private SqliteConnection OpenReadOnly()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void EnsureV13Schema(SqliteConnection connection)
    {
        try
        {
            using var version = connection.CreateCommand();
            version.CommandText = "SELECT COALESCE(MAX(version),0) FROM schema_versions";
            var schemaVersion = Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (schemaVersion != 13) throw new P2B3SchemaUnavailableException("P2B_SCHEMA_UNAVAILABLE");
            P2B2SchemaV13Migration.ValidateV13Schema(connection);
        }
        catch (SqliteException)
        {
            throw new P2B3SchemaUnavailableException("P2B_SCHEMA_UNAVAILABLE");
        }
        catch (InvalidDataException exception)
        {
            throw new P2B3SchemaUnavailableException(exception.Message);
        }
    }

    private static IReadOnlyList<P2B3ScopeRead> EmptyScopes => Array.Empty<P2B3ScopeRead>();

    private static Dictionary<string, IReadOnlyList<P2B3ScopeRead>> ReadScopes(SqliteConnection connection, IEnumerable<string> ids)
    {
        var distinct = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        var result = distinct.ToDictionary(id => id, _ => (IReadOnlyList<P2B3ScopeRead>)Array.Empty<P2B3ScopeRead>(), StringComparer.Ordinal);
        if (distinct.Length == 0) return result;
        using var command = connection.CreateCommand();
        var placeholders = new List<string>(distinct.Length);
        for (var index = 0; index < distinct.Length; index++)
        {
            var name = "$domain" + index;
            placeholders.Add(name);
            command.Parameters.AddWithValue(name, distinct[index]);
        }
        command.CommandText = $"SELECT network_domain_id,scope_kind,scope_reference,resolution,source_kind,source_reference,revision FROM network_domain_scopes WHERE network_domain_id IN ({string.Join(',', placeholders)}) ORDER BY network_domain_id,scope_kind,scope_reference,id";
        var grouped = new Dictionary<string, List<P2B3ScopeRead>>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!grouped.TryGetValue(id, out var list)) grouped[id] = list = new();
            list.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6)));
        }
        foreach (var pair in grouped) result[pair.Key] = pair.Value;
        return result;
    }

    private static (IReadOnlyList<P2B3DiffFact> Facts, bool Truncated) ReadDiffFacts(SqliteCommand command)
    {
        var facts = new List<P2B3DiffFact>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            facts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                NullableString(reader, 4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetString(9), NullableInt64(reader, 10), NullableInt64(reader, 11), reader.GetInt32(12),
                reader.GetInt32(12) > P2B3PageOptions.MaximumLimit));
        }
        return (facts, facts.Any(fact => fact.Incomplete));
    }

    internal static IReadOnlyList<P2B3DiffItem> BuildDiffItems(IReadOnlyList<P2B3DiffFact> facts)
    {
        return facts.GroupBy(f => $"{f.NetworkDomainId ?? "<unresolved>"}|{f.InterfaceReference}", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var planned = group.Where(f => f.SourceKind != "OBSERVED").ToArray();
                var observed = group.Where(f => f.SourceKind == "OBSERVED").ToArray();
                var incomplete = group.Any(f => f.Incomplete);
                var conflict = group.Any(f => f.ConflictState is not "NONE" and not "HISTORICAL_REUSE_ALLOWED");
                var unresolved = group.Any(f => f.DomainResolutionState != "RESOLVED");
                var valuesMatch = planned.Length > 0 && observed.Length > 0 &&
                    new HashSet<string>(planned.Select(ComparableValue), StringComparer.Ordinal)
                        .SetEquals(new HashSet<string>(observed.Select(ComparableValue), StringComparer.Ordinal));
                var status = incomplete ? "INCOMPLETE" : conflict ? "CONFLICT" : unresolved ? "UNRESOLVED" : planned.Length == 0 ? "OBSERVED_ONLY" :
                    observed.Length == 0 ? "PLANNED_ONLY" : valuesMatch ? "MATCH" : "MISMATCH";
                var effectiveState = status == "MATCH" ? "CONSISTENT" : status;
                return new P2B3DiffItem(group.Key, status, effectiveState, null, null, planned, observed);
            }).ToArray();
    }

    private static (string Name, object? Value)[] DomainParameters(string? siteId, string? stationId, string? lineId,
        string? sectionId, string? domainKind, string? scopeResolution, string? lifecycle, string? search) =>
        new[]
        {
            ("$site", NullIfBlank(siteId)), ("$station", NullIfBlank(stationId)), ("$line", NullIfBlank(lineId)),
            ("$section", NullIfBlank(sectionId)), ("$kind", NullIfBlank(domainKind)),
            ("$resolution", NullIfBlank(scopeResolution)), ("$lifecycle", NullIfBlank(lifecycle)),
            ("$search", string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%")
        };

    private static long Count(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static P2B3ReadPage<T> ToPage<T>(List<(string Id, T Value)> rows, P2B3PageOptions page, long total, string cursorFingerprint)
    {
        var hasMore = rows.Count > page.Limit;
        if (hasMore) rows.RemoveRange(page.Limit, rows.Count - page.Limit);
        return new(page.Limit, total, hasMore, hasMore ? P2B3PageOptions.EncodeCursor(rows[^1].Id, cursorFingerprint) : null,
            rows.Select(row => row.Value).ToArray());
    }

    private static string ComparableValue(P2B3DiffFact fact) =>
        string.Join('\u001f', fact.Value, fact.Lifecycle, fact.ValidFromMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            fact.ValidToMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");

    private static string CursorFingerprint(params string?[] values) =>
        string.Join('\u001f', values.Select(value => value ?? "<null>"));

    private static string RequiredId(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new P2B3QueryException($"{label} is invalid.");
        return value;
    }

    private static object? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddParameters(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static long? NullableInt64(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
    private static int? NullableInt32(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
}
