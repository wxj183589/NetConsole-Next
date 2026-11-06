using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record LinuxServerRequest(string Name, string Host, string Username, int Port = 22,
    string? Password = null, bool ClearPassword = false, string GroupName = "", string SiteId = "local", bool Enabled = true);

internal sealed record LinuxServerView(string Id, string Name, string Host, int Port, string Username,
    string GroupName, string SiteId, bool Enabled, bool CredentialConfigured, long? LastConnectedMs,
    long? LastUsedMs, string? LastResult, string HostKeySha256);

internal sealed record LinuxServerSnapshot(LinuxServerView View, long UpdatedMs);

internal static class LinuxServerSchema
{
    public static void Apply(SqliteConnection db)
    {
        using (var create = db.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS linux_servers(
                    id TEXT PRIMARY KEY,name TEXT NOT NULL,host TEXT NOT NULL,port INTEGER NOT NULL,
                    username TEXT NOT NULL,group_name TEXT NOT NULL,site_id TEXT NOT NULL REFERENCES sites(id),
                    enabled INTEGER NOT NULL,credential_password TEXT,host_key_sha256 TEXT NOT NULL DEFAULT '',
                    last_connected_ms INTEGER,last_used_ms INTEGER,last_result TEXT,created_ms INTEGER NOT NULL,updated_ms INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS linux_host_key_events(
                    id TEXT PRIMARY KEY,server_id TEXT NOT NULL,old_sha256 TEXT NOT NULL,new_sha256 TEXT NOT NULL,
                    key_type TEXT NOT NULL,observed_ms INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_linux_server_site ON linux_servers(site_id,group_name,name);
                """;
            create.ExecuteNonQuery();
        }
        using var schema = db.CreateCommand();
        schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='terminal_sessions'";
        var sql = (string?)schema.ExecuteScalar() ?? throw new InvalidDataException("Terminal schema is unavailable.");
        if (!sql.Contains("LINUX_INTERACTIVE", StringComparison.Ordinal))
        {
            // SQLite cannot alter CHECK constraints. Rebuild with all original columns and
            // indexes in one transaction; child recording/event foreign keys keep their names.
            const string originalConstraint = "CHECK(mode IN ('RESTRICTED_READONLY'))";
            if (!sql.Contains(originalConstraint, StringComparison.Ordinal))
                throw new InvalidDataException("Terminal mode constraint is not recognized; preserving the catalog.");
            using var indexes = db.CreateCommand();
            indexes.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name='terminal_sessions' AND sql IS NOT NULL";
            var indexSql = new List<string>();
            using (var reader = indexes.ExecuteReader()) while (reader.Read()) indexSql.Add(reader.GetString(0));
            using var foreignKeys = db.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys=OFF";
            foreignKeys.ExecuteNonQuery();
            try
            {
                using var transaction = db.BeginTransaction();
                using var upgrade = db.CreateCommand();
                upgrade.Transaction = transaction;
                upgrade.CommandText = sql.Replace("terminal_sessions", "terminal_sessions_linux_upgrade", StringComparison.Ordinal)
                    .Replace(originalConstraint, "CHECK(mode IN ('RESTRICTED_READONLY','LINUX_INTERACTIVE'))", StringComparison.Ordinal);
                upgrade.ExecuteNonQuery();
                upgrade.CommandText = """
                    INSERT INTO terminal_sessions_linux_upgrade SELECT * FROM terminal_sessions;
                    DROP TABLE terminal_sessions;
                    ALTER TABLE terminal_sessions_linux_upgrade RENAME TO terminal_sessions;
                    """;
                upgrade.ExecuteNonQuery();
                foreach (var index in indexSql) { upgrade.CommandText = index; upgrade.ExecuteNonQuery(); }
                upgrade.CommandText = "PRAGMA foreign_key_check";
                using (var reader = upgrade.ExecuteReader())
                    if (reader.Read()) throw new InvalidDataException("Linux terminal migration failed foreign-key verification.");
                transaction.Commit();
            }
            finally { foreignKeys.CommandText = "PRAGMA foreign_keys=ON"; foreignKeys.ExecuteNonQuery(); }
        }
        using var version = db.CreateCommand();
        version.CommandText = "INSERT OR IGNORE INTO schema_versions(version,applied_ms) VALUES(23,$now)";
        version.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        version.ExecuteNonQuery();
    }
}

internal sealed partial class Catalog
{
    private const string LinuxServerSelect = """
        SELECT id,name,host,port,username,group_name,site_id,enabled,
          CASE WHEN COALESCE(credential_password,'')<>'' THEN 1 ELSE 0 END,
          last_connected_ms,last_used_ms,last_result,host_key_sha256,updated_ms FROM linux_servers
        """;

    private static LinuxServerSnapshot ReadLinuxServer(SqliteDataReader reader) => new(new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetBoolean(7), reader.GetBoolean(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetInt64(10),
        reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetString(12)), reader.GetInt64(13));

    public IReadOnlyList<LinuxServerView> QueryLinuxServers(P2B5SiteScopeDecision scope)
    {
        using var db = Open(); using var command = db.CreateCommand();
        var parameters = new List<(string Name, object? Value)>();
        command.CommandText = LinuxServerSelect + " WHERE " + DeviceSiteScope.BuildSqlFilter(scope, parameters, "site_id") + " ORDER BY group_name,name,id";
        DeviceSiteScope.AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        var servers = new List<LinuxServerView>();
        while (reader.Read()) servers.Add(ReadLinuxServer(reader).View);
        return servers;
    }

    public LinuxServerSnapshot? GetLinuxServer(string id)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = LinuxServerSelect + " WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadLinuxServer(reader) : null;
    }

    public bool LinuxServerIsInScope(string id, P2B5SiteScopeDecision scope) =>
        GetLinuxServer(id) is { } server && DeviceSiteScope.Allows(scope, server.View.SiteId);

    internal string? GetLinuxServerPassword(string id)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT credential_password FROM linux_servers WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string password && password.Length > 0 ? password : null;
    }

    public LinuxServerView SaveLinuxServer(string? id, LinuxServerRequest request)
    {
        static string Required(string? value, string field, int length)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > length || value.Any(char.IsControl))
                throw new ArgumentException($"{field} is invalid.");
            return value.Trim();
        }
        var name = Required(request.Name, "Server name", 256);
        var host = Required(request.Host, "SSH host", 253);
        var username = Required(request.Username, "SSH username", 256);
        var site = Required(request.SiteId, "Site", 128);
        var group = request.GroupName?.Trim() ?? "";
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || request.Port is < 1 or > 65535 ||
            group.Length > 128 || group.Any(char.IsControl) || request.Password is { Length: > 4096 })
            throw new ArgumentException("Server connection settings are invalid.");
        var existing = id is null ? null : GetLinuxServer(id) ?? throw new KeyNotFoundException("Linux server was not found.");
        id ??= Guid.NewGuid().ToString("N");
        var identityChanged = existing is not null && (existing.View.Host != host || existing.View.Port != request.Port || existing.View.Username != username);
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var checkSite = db.CreateCommand(); checkSite.Transaction = transaction;
        checkSite.CommandText = "SELECT COUNT(*) FROM sites WHERE id=$site";
        checkSite.Parameters.AddWithValue("$site", site);
        if (Convert.ToInt64(checkSite.ExecuteScalar()) != 1) throw new KeyNotFoundException("Site was not found.");
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO linux_servers(id,name,host,port,username,group_name,site_id,enabled,credential_password,created_ms,updated_ms)
            VALUES($id,$name,$host,$port,$user,$group,$site,$enabled,$password,$now,$now)
            ON CONFLICT(id) DO UPDATE SET name=$name,host=$host,port=$port,username=$user,group_name=$group,
              site_id=$site,enabled=$enabled,
              credential_password=CASE WHEN $clear=1 THEN NULL WHEN $password IS NULL OR $password='' THEN credential_password ELSE $password END,
              host_key_sha256=CASE WHEN $identityChanged=1 THEN '' ELSE host_key_sha256 END,
              last_connected_ms=CASE WHEN $identityChanged=1 THEN NULL ELSE last_connected_ms END,
              last_result=CASE WHEN $identityChanged=1 THEN NULL ELSE last_result END,
              updated_ms=CASE WHEN $now<=updated_ms THEN updated_ms+1 ELSE $now END
            """;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$host", host); command.Parameters.AddWithValue("$port", request.Port);
        command.Parameters.AddWithValue("$user", username); command.Parameters.AddWithValue("$group", group);
        command.Parameters.AddWithValue("$site", site); command.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$password", request.ClearPassword ? DBNull.Value : (object?)request.Password ?? DBNull.Value);
        command.Parameters.AddWithValue("$clear", request.ClearPassword ? 1 : 0);
        command.Parameters.AddWithValue("$identityChanged", identityChanged ? 1 : 0);
        command.Parameters.AddWithValue("$now", UtcNowMs());
        command.ExecuteNonQuery(); transaction.Commit();
        return GetLinuxServer(id)!.View;
    }

    public bool DeleteLinuxServer(string id)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM linux_servers WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    internal bool CommitLinuxHostKey(LinuxServerSnapshot server, string keyType, string fingerprint)
    {
        fingerprint = H3cHostKeyFingerprint.Normalize(fingerprint);
        using var db = Open(); using var transaction = db.BeginTransaction();
        using (var current = db.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT host_key_sha256 FROM linux_servers WHERE id=$id AND updated_ms=$revision";
            current.Parameters.AddWithValue("$id", server.View.Id);
            current.Parameters.AddWithValue("$revision", server.UpdatedMs);
            var savedKey = current.ExecuteScalar() as string;
            if (savedKey is null) return false;
            // Parallel first connections may observe the same authenticated key.
            // Keep their configuration revision check without recording duplicate trust events.
            if (savedKey == fingerprint) return true;
            if (savedKey != server.View.HostKeySha256) return false;
        }
        using var update = db.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE linux_servers SET host_key_sha256=$key WHERE id=$id AND updated_ms=$revision AND host_key_sha256=$old";
        update.Parameters.AddWithValue("$key", fingerprint); update.Parameters.AddWithValue("$id", server.View.Id);
        update.Parameters.AddWithValue("$revision", server.UpdatedMs); update.Parameters.AddWithValue("$old", server.View.HostKeySha256);
        if (update.ExecuteNonQuery() != 1)
        {
            // Concurrent sessions may observe and trust the same first key. The
            // first transaction records it; a later transaction is successful
            // only when the saved server revision is still the one we connected
            // to and its current key exactly matches this observation.
            using var current = db.CreateCommand(); current.Transaction = transaction;
            current.CommandText = "SELECT host_key_sha256,updated_ms FROM linux_servers WHERE id=$id";
            current.Parameters.AddWithValue("$id", server.View.Id);
            using var reader = current.ExecuteReader();
            var alreadyCommitted = reader.Read() && reader.GetString(0) == fingerprint &&
                reader.GetInt64(1) == server.UpdatedMs;
            if (!alreadyCommitted) return false;
            transaction.Commit();
            return true;
        }
        if (server.View.HostKeySha256 != fingerprint)
        {
            using var history = db.CreateCommand(); history.Transaction = transaction;
            history.CommandText = "INSERT INTO linux_host_key_events(id,server_id,old_sha256,new_sha256,key_type,observed_ms) VALUES($id,$server,$old,$new,$type,$now)";
            history.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); history.Parameters.AddWithValue("$server", server.View.Id);
            history.Parameters.AddWithValue("$old", server.View.HostKeySha256); history.Parameters.AddWithValue("$new", fingerprint);
            history.Parameters.AddWithValue("$type", keyType); history.Parameters.AddWithValue("$now", UtcNowMs());
            history.ExecuteNonQuery();
        }
        transaction.Commit(); return true;
    }

    internal void RecordLinuxConnectionResult(string id, string result, bool connected = false, bool used = false)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE linux_servers SET last_result=$result,
                last_connected_ms=CASE WHEN $connected=1 THEN $now ELSE last_connected_ms END,
                last_used_ms=CASE WHEN $used=1 THEN $now ELSE last_used_ms END WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$connected", connected ? 1 : 0); command.Parameters.AddWithValue("$used", used ? 1 : 0);
        command.Parameters.AddWithValue("$now", UtcNowMs()); command.ExecuteNonQuery();
    }
}
