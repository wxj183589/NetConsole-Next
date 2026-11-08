using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed partial class Catalog
{
    // A task run is a revision of the collector, not a new authority. The
    // captured sourceId and frozen endpoint further separate collectors.
    internal static string H3cFactAuthority(string source) =>
        source.StartsWith("H3C_TASK_RAW:", StringComparison.Ordinal) ? "H3C_TASK_RAW" : source;

    private static string FactScopeKeyExpression(string payload) => $@"CASE
        WHEN json_valid({payload}) AND json_type({payload},'$.capturedScope')='object'
        THEN json_array(json_extract({payload},'$.capturedScope.version'),
            json_extract({payload},'$.capturedScope.deviceId'),json_extract({payload},'$.capturedScope.profileId'),
            json_extract({payload},'$.capturedScope.siteId'),json_extract({payload},'$.capturedScope.sourceId'),
            json_extract({payload},'$.capturedScope.host'),json_extract({payload},'$.capturedScope.port'))
        ELSE 'UNSCOPED' END";

    private static string ReadFactScopeKey(SqliteConnection db, SqliteTransaction transaction, string payload)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + FactScopeKeyExpression("$payload");
        command.Parameters.AddWithValue("$payload", payload);
        return (string)command.ExecuteScalar()!;
    }

    private static void ApplyV27Migration(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        using var version = db.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "SELECT 1 FROM schema_versions WHERE version=27";
        if (version.ExecuteScalar() is not null) { transaction.Commit(); return; }
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        // Generated columns keep legacy explicit-column inserts compatible and
        // cannot drift from immutable source/payload, including migrated rows.
        command.CommandText = $@"CREATE TABLE h3c_facts_v27(
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL REFERENCES h3c_device_profiles(id) ON DELETE CASCADE,
            kind TEXT NOT NULL, identity_key TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('STALE','EMPTY','UNSUPPORTED','PARTIAL','FAILED')),
            source TEXT NOT NULL, captured_ms INTEGER NOT NULL,
            is_current INTEGER NOT NULL DEFAULT 1 CHECK(is_current IN (0,1)), payload_json TEXT NOT NULL,
            authority_key TEXT GENERATED ALWAYS AS
                (CASE WHEN substr(source,1,13)='H3C_TASK_RAW:' THEN 'H3C_TASK_RAW' ELSE source END) VIRTUAL,
            scope_key TEXT GENERATED ALWAYS AS ({FactScopeKeyExpression("payload_json")}) VIRTUAL,
            UNIQUE(profile_id,kind,identity_key,source,scope_key,captured_ms));
            INSERT INTO h3c_facts_v27(id,profile_id,kind,identity_key,status,source,captured_ms,is_current,payload_json)
                SELECT id,profile_id,kind,identity_key,status,source,captured_ms,is_current,payload_json FROM h3c_facts;
            DROP TABLE h3c_facts;
            ALTER TABLE h3c_facts_v27 RENAME TO h3c_facts;
            CREATE INDEX idx_h3c_facts_current ON h3c_facts(profile_id,is_current,kind,captured_ms DESC);
            CREATE INDEX idx_h3c_facts_history ON h3c_facts(profile_id,kind,captured_ms DESC);
            CREATE INDEX idx_h3c_facts_authority_revision ON h3c_facts
                (profile_id,kind,identity_key,authority_key,scope_key,captured_ms DESC);
            CREATE INDEX idx_h3c_facts_authority_table ON h3c_facts
                (profile_id,kind,authority_key,scope_key,captured_ms DESC);
            UPDATE h3c_facts AS fact SET is_current=CASE WHEN
                NOT EXISTS(SELECT 1 FROM h3c_facts newer WHERE newer.profile_id=fact.profile_id
                    AND newer.kind=fact.kind AND newer.identity_key=fact.identity_key
                    AND newer.authority_key=fact.authority_key AND newer.scope_key=fact.scope_key
                    AND (newer.captured_ms>fact.captured_ms OR (newer.captured_ms=fact.captured_ms AND newer.id>fact.id)))
                AND NOT (fact.kind='lldp.neighbor' AND fact.authority_key='H3C_TASK_RAW' AND
                    EXISTS(SELECT 1 FROM h3c_facts tab WHERE tab.profile_id=fact.profile_id AND tab.kind='lldp.table'
                        AND tab.authority_key=fact.authority_key AND tab.scope_key=fact.scope_key
                        AND tab.captured_ms>fact.captured_ms)) THEN 1 ELSE 0 END;
            CREATE UNIQUE INDEX idx_h3c_facts_authority_current ON h3c_facts
                (profile_id,kind,identity_key,authority_key,scope_key) WHERE is_current=1;
            INSERT INTO schema_versions(version,applied_ms) VALUES(27,$now);";
        command.Parameters.AddWithValue("$now", UtcNowMs());
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
