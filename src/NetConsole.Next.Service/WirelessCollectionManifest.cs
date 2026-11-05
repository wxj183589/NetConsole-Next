using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record WirelessCollectionManifestView(
    string Id,
    string SourceRevision,
    string ScopeStatus,
    IReadOnlyList<string> SourceAcDeviceIds,
    long FirstCapturedMs,
    long LastCapturedMs,
    int ObservationCount,
    string Status,
    long CreatedMs);

internal sealed record WirelessCollectionManifestItemView(
    string ObservationId,
    string SourceAcDeviceId,
    string SourceAcDomainId,
    string SourceSiteId,
    string RawManifestRef,
    string SourceRevision,
    long CapturedMs,
    string Outcome);

internal sealed record WirelessCollectionManifestDetailView(
    WirelessCollectionManifestView Manifest,
    IReadOnlyList<WirelessCollectionManifestItemView> Items,
    long AsOf,
    WirelessScopeView Scope,
    string SourceRevision,
    IReadOnlyList<string> Warnings);

internal sealed record WirelessCollectionManifestPage(
    IReadOnlyList<WirelessCollectionManifestView> Items,
    int Page,
    int PageSize,
    long Total,
    bool HasMore,
    long AsOf,
    WirelessScopeView Scope,
    string SourceRevision,
    IReadOnlyList<string> Warnings);

internal sealed partial class Catalog
{
    public WirelessCollectionManifestPage QueryWirelessCollectionManifests(
        P2B5SiteScopeDecision scope, string? domainId, string? sourceAcId, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        using var db = Open();
        var parameters = new List<(string Name, object? Value)>();
        var where = WirelessManifestItemFilter(scope, domainId, sourceAcId, parameters, "i");

        long total;
        using (var count = db.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(DISTINCT m.id) FROM wireless_collection_manifests m JOIN wireless_collection_manifest_items i ON i.manifest_id=m.id WHERE {where}";
            DeviceSiteScope.AddParameters(count, parameters);
            total = Convert.ToInt64(count.ExecuteScalar());
        }

        var items = new List<WirelessCollectionManifestView>();
        using (var query = db.CreateCommand())
        {
            query.CommandText = $"""
                SELECT m.id,m.source_revision,m.scope_status,json_group_array(DISTINCT i.source_ac_device_id),
                       MIN(i.captured_ms),MAX(i.captured_ms),COUNT(i.observation_id),m.status,m.created_ms
                FROM wireless_collection_manifests m
                JOIN wireless_collection_manifest_items i ON i.manifest_id=m.id
                WHERE {where}
                GROUP BY m.id,m.source_revision,m.scope_status,m.status,m.created_ms
                ORDER BY m.last_captured_ms DESC,m.id DESC
                LIMIT $limit OFFSET $offset
                """;
            DeviceSiteScope.AddParameters(query, parameters);
            query.Parameters.AddWithValue("$limit", pageSize);
            query.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
            using var reader = query.ExecuteReader();
            while (reader.Read()) items.Add(ReadManifest(reader));
        }

        var asOf = items.Count == 0 ? 0 : items.Max(item => item.LastCapturedMs);
        var sourceRevision = items.Count == 0 ? "" : items[0].SourceRevision;
        return new(items, page, pageSize, total, page * (long)pageSize < total, asOf,
            WirelessScopeViewFor(scope), sourceRevision, Array.Empty<string>());
    }

    public WirelessCollectionManifestDetailView? GetWirelessCollectionManifest(
        string id, P2B5SiteScopeDecision scope, string? sourceAcId = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        using var db = Open();
        var parameters = new List<(string Name, object? Value)>();
        var itemFilter = WirelessManifestItemFilter(scope, null, sourceAcId, parameters, "visible");
        WirelessCollectionManifestView? manifest = null;
        using (var query = db.CreateCommand())
        {
            query.CommandText = $"""
                SELECT m.id,m.source_revision,m.scope_status,json_group_array(DISTINCT visible.source_ac_device_id),
                       MIN(visible.captured_ms),MAX(visible.captured_ms),COUNT(visible.observation_id),m.status,m.created_ms
                FROM wireless_collection_manifests m
                JOIN wireless_collection_manifest_items visible ON visible.manifest_id=m.id
                WHERE m.id=$id AND {itemFilter}
                GROUP BY m.id,m.source_revision,m.scope_status,m.status,m.created_ms
                """;
            query.Parameters.AddWithValue("$id", id);
            DeviceSiteScope.AddParameters(query, parameters);
            using var reader = query.ExecuteReader();
            if (reader.Read()) manifest = ReadManifest(reader);
        }
        if (manifest is null) return null;

        var items = new List<WirelessCollectionManifestItemView>();
        using (var query = db.CreateCommand())
        {
            query.Parameters.AddWithValue("$id", id);
            var itemParameters = new List<(string Name, object? Value)>();
            // Rebuild the predicate parameters using the same aliases used above.
            query.CommandText = $"""
                SELECT i.observation_id,i.source_ac_device_id,i.source_ac_domain_id,i.source_site_id,
                       i.raw_manifest_ref,i.source_revision,i.captured_ms,i.outcome
                FROM wireless_collection_manifest_items i
                WHERE i.manifest_id=$id AND {WirelessManifestItemFilter(scope, null, sourceAcId, itemParameters, "i")}
                ORDER BY i.captured_ms DESC,i.observation_id
                """;
            DeviceSiteScope.AddParameters(query, itemParameters);
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7)));
            }
        }
        return new(manifest, items, manifest.LastCapturedMs, WirelessScopeViewFor(scope), manifest.SourceRevision, Array.Empty<string>());
    }

    private static WirelessCollectionManifestView ReadManifest(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ReadStringArray(reader.GetString(3)),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt32(6), reader.GetString(7), reader.GetInt64(8));

    private static IReadOnlyList<string> ReadStringArray(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static string WirelessManifestItemFilter(P2B5SiteScopeDecision scope, string? domainId,
        string? sourceAcId, List<(string Name, object? Value)> parameters, string alias)
    {
        var filters = new List<string> { DeviceSiteScope.BuildSqlFilter(scope, parameters, $"{alias}.source_site_id", "$wirelessManifestSite") };
        if (!string.IsNullOrWhiteSpace(domainId)) { parameters.Add(("$wirelessManifestDomain", domainId)); filters.Add($"{alias}.source_ac_domain_id=$wirelessManifestDomain"); }
        if (!string.IsNullOrWhiteSpace(sourceAcId)) { parameters.Add(("$wirelessManifestAc", sourceAcId)); filters.Add($"{alias}.source_ac_device_id=$wirelessManifestAc"); }
        return string.Join(" AND ", filters);
    }

    private static WirelessScopeView WirelessScopeViewFor(P2B5SiteScopeDecision scope) =>
        new(scope.IsGlobal ? "FULL" : "PARTIAL_SCOPE", scope.IsGlobal ? "GLOBAL" : scope.ClaimsRevision ?? "UNKNOWN");
}

internal sealed partial class WirelessReadModelService
{
    public WirelessCollectionManifestPage GetCollectionManifests(P2B5SiteScopeDecision scope,
        string? domainId, string? sourceAcId, int page, int pageSize)
    {
        if (LoadFixture() is not null)
        {
            var snapshot = GetSnapshot(scope);
            var detail = BuildFixtureManifest(snapshot, domainId, sourceAcId);
            var items = detail is null ? Array.Empty<WirelessCollectionManifestView>() : new[] { detail.Manifest };
            var selected = items.Skip(Math.Max(0, page - 1) * Math.Clamp(pageSize, 1, 100)).Take(Math.Clamp(pageSize, 1, 100)).ToArray();
            return new(selected, page, Math.Clamp(pageSize, 1, 100), items.Length, page * (long)Math.Clamp(pageSize, 1, 100) < items.Length,
                snapshot.AsOf, snapshot.Scope, snapshot.SourceRevision, snapshot.Warnings);
        }
        return _catalog.QueryWirelessCollectionManifests(scope, domainId, sourceAcId, page, pageSize);
    }

    public WirelessCollectionManifestDetailView? GetCollectionManifest(string id, P2B5SiteScopeDecision scope, string? sourceAcId = null)
    {
        if (LoadFixture() is not null)
        {
            var snapshot = GetSnapshot(scope);
            return string.Equals(id, "wireless-collection:" + snapshot.SourceRevision, StringComparison.Ordinal)
                ? BuildFixtureManifest(snapshot, null, sourceAcId) : null;
        }
        return _catalog.GetWirelessCollectionManifest(id, scope, sourceAcId);
    }

    private static WirelessCollectionManifestDetailView? BuildFixtureManifest(WirelessProjectionSnapshot snapshot,
        string? domainId, string? sourceAcId)
    {
        var observations = snapshot.ApResources.SelectMany(resource => resource.Observations)
            .Where(item => (domainId is null || string.Equals(item.SourceAcDomainId, domainId, StringComparison.Ordinal)) &&
                           (sourceAcId is null || string.Equals(item.SourceAcDeviceId, sourceAcId, StringComparison.Ordinal)))
            .OrderByDescending(item => item.CapturedMs).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (observations.Length == 0) return null;
        var manifest = new WirelessCollectionManifestView("wireless-collection:" + snapshot.SourceRevision,
            snapshot.SourceRevision, snapshot.Scope.Status,
            observations.Select(item => item.SourceAcDeviceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            observations.Min(item => item.CapturedMs), observations.Max(item => item.CapturedMs), observations.Length,
            "PUBLISHED", snapshot.AsOf);
        var items = observations.Select(item => new WirelessCollectionManifestItemView(item.Id, item.SourceAcDeviceId,
            item.SourceAcDomainId, item.SourceSiteId, item.RawManifestRef, item.SourceRevision, item.CapturedMs,
            item.NormalizedState is "UNKNOWN" or "CONFLICT" ? "AMBIGUOUS" : "OBSERVED")).ToArray();
        return new(manifest, items, snapshot.AsOf, snapshot.Scope, snapshot.SourceRevision, snapshot.Warnings);
    }
}
