namespace NetConsole.Next.Service;

internal sealed partial class Catalog
{
    // A busy neighbor must not evict a different neighbor's last observation
    // simply by contributing many revisions to a bounded history page.
    internal IReadOnlyList<H3cFactSnapshot> QueryLatestLldpNeighborHistory(string profileId,
        P2B5SiteScopeDecision scope, int limit = 1000)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        var parameters = new List<(string Name, object? Value)>();
        var scopeFilter = H3cFactScopePredicate(scope, parameters, "h3c_facts", true);
        command.CommandText = $@"SELECT id,profile_id,kind,identity_key,status,source,captured_ms,is_current,payload_json,
            {H3cFactRawVerificationPredicate("latest")},scope_key FROM
            (SELECT h3c_facts.*,ROW_NUMBER() OVER
                (PARTITION BY profile_id,identity_key,authority_key,scope_key ORDER BY captured_ms DESC,id DESC) AS revision_rank
             FROM h3c_facts WHERE profile_id=$profile AND kind='lldp.neighbor' AND {scopeFilter}) latest
            WHERE revision_rank=1 ORDER BY captured_ms DESC,identity_key,source,id LIMIT $limit";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        DeviceSiteScope.AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<H3cFactSnapshot>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt32(7) != 0, reader.GetString(8))
                { CollectedRawVerified = reader.GetInt64(9) != 0, CapturedScopeKey = reader.GetString(10) });
        return rows;
    }
}
