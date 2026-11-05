using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

// Capture once at publication. Inventory edits never relabel an existing fact.
internal sealed record H3cFactScope(int Version, string DeviceId, string ProfileId, string SiteId,
    string SourceId, string Host, int Port);

internal sealed class H3cFactRevisionConflictException() : Exception("H3C_FACT_REVISION_CONFLICT");

internal sealed partial class Catalog
{
    private static H3cFactScope? CaptureH3cFactScope(SqliteConnection db, SqliteTransaction transaction, string profileId)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT d.id,d.site_id,p.source_id,p.host,p.port FROM h3c_device_profiles p
            JOIN collection_sources s ON s.id=p.source_id
            JOIN device_inventory d ON d.terminal_profile_id=p.id AND d.site_id=s.site_id
            WHERE p.id=$profile LIMIT 2";
        command.Parameters.AddWithValue("$profile", profileId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var scope = new H3cFactScope(1, reader.GetString(0), profileId, reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt32(4));
        return reader.Read() ? null : scope;
    }

    private static string H3cFactScopePredicate(P2B5SiteScopeDecision? scope,
        List<(string Name, object? Value)> parameters, string alias, bool requireProof = false)
    {
        if (scope is { IsAuthorized: false }) return "0=1";
        if (!requireProof && (scope is null || scope.IsGlobal)) return "1=1";
        string Field(string name) => $"CASE WHEN json_valid({alias}.payload_json) THEN json_extract({alias}.payload_json,'$.capturedScope.{name}') END";
        var siteFilter = scope is null ? "1=1" : DeviceSiteScope.BuildSqlFilter(scope, parameters, "proof_d.site_id", "$factScope" + alias);
        return $@"EXISTS(SELECT 1 FROM device_inventory proof_d
            JOIN h3c_device_profiles proof_p ON proof_p.id=proof_d.terminal_profile_id
            JOIN collection_sources proof_s ON proof_s.id=proof_p.source_id
            WHERE proof_p.id={alias}.profile_id AND {Field("version")}=1
                AND proof_d.id={Field("deviceId")} AND proof_p.id={Field("profileId")}
                AND proof_d.site_id={Field("siteId")} AND proof_s.site_id=proof_d.site_id
                AND proof_p.source_id={Field("sourceId")} AND proof_p.host={Field("host")} AND proof_p.port={Field("port")}
                AND {siteFilter})";
    }
}
