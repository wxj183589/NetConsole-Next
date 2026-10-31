using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record SiteView(
    string Id,
    string Name,
    string Description,
    string Status,
    long CreatedMs,
    long UpdatedMs,
    long? ArchivedMs,
    long DeviceCount,
    long GroupCount,
    long CollectionSourceCount,
    long TerminalProfileCount,
    bool CanWrite);

internal sealed record SiteWriteRequest(string Id, string Name, string? Description, long? ExpectedUpdatedMs = null);
internal sealed record SiteStatusRequest(string Status, long? ExpectedUpdatedMs = null);

internal sealed class SiteConcurrencyException(string message) : InvalidOperationException(message);

internal sealed partial class Catalog
{
    public IReadOnlyList<SiteView> QuerySites(P2B5SiteScopeDecision scope)
    {
        using var db = Open();
        var parameters = new List<(string Name, object? Value)>();
        var filter = P2B5SiteScopeSql.ForSite("s.id", scope, parameters, "site_scope");
        using var command = db.CreateCommand();
        command.CommandText = $@"SELECT s.id,s.name,s.description,s.status,s.created_ms,s.updated_ms,s.archived_ms,
                (SELECT COUNT(*) FROM device_inventory d WHERE d.site_id=s.id),
                (SELECT COUNT(*) FROM device_groups g WHERE g.site_id=s.id),
                (SELECT COUNT(*) FROM collection_sources cs WHERE cs.site_id=s.id),
                (SELECT COUNT(*) FROM h3c_device_profiles p JOIN collection_sources cps ON cps.id=p.source_id WHERE cps.site_id=s.id)
            FROM sites s
            WHERE {filter}
            ORDER BY CASE s.status WHEN 'ACTIVE' THEN 0 WHEN 'DISABLED' THEN 1 ELSE 2 END,s.name,s.id";
        AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<SiteView>();
        while (reader.Read())
        {
            var status = reader.GetString(3);
            result.Add(new SiteView(reader.GetString(0), reader.GetString(1), reader.GetString(2), status,
                reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10),
                scope.IsGlobal));
        }
        return result;
    }

    public SiteView CreateSite(SiteWriteRequest request, string actor)
    {
        var id = NormalizeSiteId(request.Id);
        var name = RequiredText(request.Name, "name", 120);
        var description = Clean(request.Description, 500);
        var now = UtcNowMs();
        using var db = Open();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO sites(id,name,description,status,created_ms,updated_ms,archived_ms)
                VALUES($id,$name,$description,'ACTIVE',$now,$now,NULL)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$description", description);
            insert.Parameters.AddWithValue("$now", now);
            try { insert.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            { throw new InvalidOperationException("A Site with this stable ID already exists.", ex); }
        }
        InsertDeviceAudit(db, transaction, actor, "SITE_CREATE", id, new { siteId = id, name, description });
        transaction.Commit();
        return QuerySites(P2B5SiteScopeDecision.Global).Single(site => site.Id == id);
    }

    public SiteView UpdateSite(string id, SiteWriteRequest request, string actor)
    {
        id = NormalizeSiteId(id);
        if (!string.Equals(id, NormalizeSiteId(request.Id), StringComparison.Ordinal))
            throw new ArgumentException("The stable Site ID cannot be changed.", nameof(request));
        var name = RequiredText(request.Name, "name", 120);
        var description = Clean(request.Description, 500);
        var now = UtcNowMs();
        using var db = Open();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        long currentRevision;
        using (var current = db.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT updated_ms FROM sites WHERE id=$id";
            current.Parameters.AddWithValue("$id", id);
            var value = current.ExecuteScalar();
            if (value is null) throw new KeyNotFoundException("Site was not found.");
            currentRevision = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (request.ExpectedUpdatedMs.HasValue && request.ExpectedUpdatedMs.Value != currentRevision)
            throw new SiteConcurrencyException("The Site changed after it was loaded; reload before saving.");
        using (var update = db.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = @"UPDATE sites SET name=$name,description=$description,
                updated_ms=CASE WHEN $now<=updated_ms THEN updated_ms+1 ELSE $now END
                WHERE id=$id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$description", description);
            update.Parameters.AddWithValue("$now", now);
            update.ExecuteNonQuery();
        }
        InsertDeviceAudit(db, transaction, actor, "SITE_UPDATE", id, new { siteId = id, name, description, expectedUpdatedMs = request.ExpectedUpdatedMs });
        transaction.Commit();
        return QuerySites(P2B5SiteScopeDecision.Global).Single(site => site.Id == id);
    }

    public SiteView SetSiteStatus(string id, SiteStatusRequest request, string actor)
    {
        id = NormalizeSiteId(id);
        var status = (request.Status ?? string.Empty).Trim().ToUpperInvariant();
        if (status is not ("ACTIVE" or "DISABLED" or "ARCHIVED"))
            throw new ArgumentException("Status must be ACTIVE, DISABLED or ARCHIVED.", nameof(request));
        if (id == "local" && status != "ACTIVE")
            throw new InvalidOperationException("The legacy local Site must remain active for compatibility.");

        var now = UtcNowMs();
        using var db = Open();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        long currentRevision;
        using (var current = db.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT updated_ms FROM sites WHERE id=$id";
            current.Parameters.AddWithValue("$id", id);
            var value = current.ExecuteScalar();
            if (value is null) throw new KeyNotFoundException("Site was not found.");
            currentRevision = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (request.ExpectedUpdatedMs.HasValue && request.ExpectedUpdatedMs.Value != currentRevision)
            throw new SiteConcurrencyException("The Site changed after it was loaded; reload before changing status.");
        using (var update = db.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = @"UPDATE sites SET status=$status,
                archived_ms=CASE WHEN $status='ARCHIVED' THEN COALESCE(archived_ms,$now) ELSE NULL END,
                updated_ms=CASE WHEN $now<=updated_ms THEN updated_ms+1 ELSE $now END
                WHERE id=$id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$now", now);
            update.ExecuteNonQuery();
        }
        InsertDeviceAudit(db, transaction, actor, "SITE_STATUS_UPDATE", id, new { siteId = id, status, expectedUpdatedMs = request.ExpectedUpdatedMs });
        transaction.Commit();
        return QuerySites(P2B5SiteScopeDecision.Global).Single(site => site.Id == id);
    }

    private static void AddParameters(SqliteCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string NormalizeSiteId(string? value)
    {
        var id = RequiredText(value, "id", 80).ToLowerInvariant();
        if (!IsValidToken(id, 80)) throw new ArgumentException("Site ID may contain only letters, numbers, dot, underscore or hyphen.", nameof(value));
        return id;
    }
}
