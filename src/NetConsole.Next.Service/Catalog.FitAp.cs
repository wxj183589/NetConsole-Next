using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record FitApFactSnapshot(
    string Id,
    string ResourceId,
    string SourceAcDeviceId,
    string SiteId,
    string ApName,
    string Kind,
    string Status,
    long CapturedMs,
    string RawRelativePath,
    string RawSha256,
    string PayloadJson);

internal sealed partial class Catalog
{
    internal void EnsureFitApRawSource(string sourceId, string resourceId, string siteId)
    {
        if (!sourceId.Equals("fit-ap:" + resourceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(resourceId) || string.IsNullOrWhiteSpace(siteId))
            throw new ArgumentException("FIT-AP Raw source identity is invalid.");
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var site = db.CreateCommand())
        {
            site.Transaction = transaction;
            site.CommandText = "SELECT 1 FROM sites WHERE id=$site";
            site.Parameters.AddWithValue("$site", siteId);
            if (site.ExecuteScalar() is null) throw new KeyNotFoundException("FIT-AP Raw site was not found.");
        }
        using (var source = db.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText = @"INSERT INTO collection_sources(id,site_id,kind,label,enabled)
                VALUES($id,$site,'FIT_AP_TELNET',$label,1)
                ON CONFLICT(id) DO UPDATE SET site_id=excluded.site_id,label=excluded.label,enabled=1";
            source.Parameters.AddWithValue("$id", sourceId);
            source.Parameters.AddWithValue("$site", siteId);
            source.Parameters.AddWithValue("$label", "FIT-AP " + resourceId);
            source.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void RecordFitApFact(FitApFactSnapshot fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        using var db = Open();
        EnsureFitApFactsTable(db);
        using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO fit_ap_facts
            (id,resource_id,source_ac_device_id,site_id,ap_name,kind,status,captured_ms,raw_relative_path,raw_sha256,payload_json)
            VALUES($id,$resource,$ac,$site,$name,$kind,$status,$captured,$path,$sha,$payload)
            ON CONFLICT(id) DO NOTHING";
        command.Parameters.AddWithValue("$id", fact.Id);
        command.Parameters.AddWithValue("$resource", fact.ResourceId);
        command.Parameters.AddWithValue("$ac", fact.SourceAcDeviceId);
        command.Parameters.AddWithValue("$site", fact.SiteId);
        command.Parameters.AddWithValue("$name", fact.ApName);
        command.Parameters.AddWithValue("$kind", fact.Kind);
        command.Parameters.AddWithValue("$status", fact.Status);
        command.Parameters.AddWithValue("$captured", fact.CapturedMs);
        command.Parameters.AddWithValue("$path", fact.RawRelativePath);
        command.Parameters.AddWithValue("$sha", fact.RawSha256);
        command.Parameters.AddWithValue("$payload", fact.PayloadJson);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<FitApFactSnapshot> QueryFitApFacts(string resourceId,
        P2B5SiteScopeDecision scope, int limit = 20)
    {
        if (!scope.IsGlobal && !scope.IsAuthorized) return Array.Empty<FitApFactSnapshot>();
        using var db = Open();
        EnsureFitApFactsTable(db);
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT id,resource_id,source_ac_device_id,site_id,ap_name,kind,status,captured_ms,
            raw_relative_path,raw_sha256,payload_json FROM fit_ap_facts
            WHERE resource_id=$resource ORDER BY captured_ms DESC,id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$resource", resourceId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        using var reader = command.ExecuteReader();
        var result = new List<FitApFactSnapshot>();
        while (reader.Read())
        {
            var site = reader.GetString(3);
            if (!scope.IsGlobal && !DeviceSiteScope.Allows(scope, site)) continue;
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), site,
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10)));
        }
        return result;
    }

    private static void EnsureFitApFactsTable(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = @"CREATE TABLE IF NOT EXISTS fit_ap_facts(
            id TEXT PRIMARY KEY,
            resource_id TEXT NOT NULL,
            source_ac_device_id TEXT NOT NULL,
            site_id TEXT NOT NULL,
            ap_name TEXT NOT NULL,
            kind TEXT NOT NULL,
            status TEXT NOT NULL,
            captured_ms INTEGER NOT NULL,
            raw_relative_path TEXT NOT NULL,
            raw_sha256 TEXT NOT NULL,
            payload_json TEXT NOT NULL
        )";
        command.ExecuteNonQuery();
    }
}
