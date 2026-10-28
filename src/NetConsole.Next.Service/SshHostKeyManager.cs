using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record SshHostKeyCheckResult(
    bool Accepted,
    string Decision,
    string? CorrelationId,
    string? KeyType,
    string? Sha256,
    string Reason);

internal sealed record SshHostKeyHistorySnapshot(
    string Id,
    string DeviceId,
    string Host,
    int Port,
    string KeyType,
    string Sha256,
    long ObservedMs,
    string State,
    string? Reason,
    string CorrelationId);

internal sealed partial class Catalog
{
    public SshHostKeyCheckResult CheckSshHostKey(H3cDeviceProfileSnapshot profile, string? keyType,
        string? presentedFingerprint)
    {
        if (string.IsNullOrWhiteSpace(keyType) || keyType.Length > 128 || keyType.Any(char.IsControl) ||
            keyType.Any(char.IsWhiteSpace))
            return new(false, "REJECTED", null, null, null, "HOST_KEY_TYPE_INVALID");
        string fingerprint;
        try { fingerprint = H3cHostKeyFingerprint.Normalize(presentedFingerprint ?? string.Empty); }
        catch (ArgumentException) { return new(false, "REJECTED", null, keyType, null, "HOST_KEY_FINGERPRINT_INVALID"); }

        using var db = Open();
        // Serialize the read/decide/insert sequence.  Without an immediate
        // write transaction, two first observations could both see no pending
        // row and create independent activation records.
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        using var lookup = db.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = @"SELECT host,port,update_policy,current_key_type,current_sha256,revision
            FROM ssh_host_keys WHERE device_id=$device";
        lookup.Parameters.AddWithValue("$device", profile.Id);
        using var reader = lookup.ExecuteReader();
        if (!reader.Read() || !string.Equals(reader.GetString(0), profile.Host, StringComparison.OrdinalIgnoreCase) ||
            reader.GetInt32(1) != profile.Port)
            return new(false, "REJECTED", null, keyType, fingerprint, "UNAUTHORIZED_DEVICE");
        var policy = reader.GetString(2);
        var currentType = reader.IsDBNull(3) ? null : reader.GetString(3);
        var currentFingerprint = reader.IsDBNull(4) ? null : reader.GetString(4);
        var revision = reader.GetInt64(5);
        reader.Close();

        if (!string.IsNullOrEmpty(currentFingerprint) &&
            H3cHostKeyFingerprint.Matches(currentFingerprint, fingerprint))
        {
            using var verified = db.CreateCommand();
            verified.Transaction = transaction;
            verified.CommandText = "UPDATE ssh_host_keys SET last_verified_ms=$now WHERE device_id=$device";
            verified.Parameters.AddWithValue("$now", UtcNowMs());
            verified.Parameters.AddWithValue("$device", profile.Id);
            verified.ExecuteNonQuery();
            transaction.Commit();
            return new(true, "MATCH", null, keyType, fingerprint, "HOST_KEY_MATCH");
        }

        if (!string.IsNullOrEmpty(currentFingerprint) && policy == "STRICT")
        {
            var correlation = RecordHostKeyEvent(db, transaction, profile, keyType, fingerprint, "REJECTED",
                "HOST_KEY_CHANGED_STRICT");
            transaction.Commit();
            return new(false, "REJECTED", correlation, keyType, fingerprint, "HOST_KEY_CHANGED_STRICT");
        }

        using var existing = db.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = @"SELECT correlation_id FROM ssh_host_key_pending
            WHERE device_id=$device AND key_type=$type AND sha256=$sha AND state='PENDING'
            ORDER BY observed_ms LIMIT 1";
        existing.Parameters.AddWithValue("$device", profile.Id);
        existing.Parameters.AddWithValue("$type", keyType);
        existing.Parameters.AddWithValue("$sha", fingerprint);
        var existingCorrelation = existing.ExecuteScalar()?.ToString();
        if (existingCorrelation is not null)
        {
            transaction.Commit();
            return new(true, "PENDING", existingCorrelation, keyType, fingerprint, "HOST_KEY_PENDING_VERIFICATION");
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var now = UtcNowMs();
        using (var pending = db.CreateCommand())
        {
            pending.Transaction = transaction;
            pending.CommandText = @"INSERT INTO ssh_host_key_pending
                (correlation_id,device_id,host,port,key_type,sha256,observed_ms,prior_revision,state)
                VALUES($correlation,$device,$host,$port,$type,$sha,$now,$revision,'PENDING')";
            pending.Parameters.AddWithValue("$correlation", correlationId);
            pending.Parameters.AddWithValue("$device", profile.Id);
            pending.Parameters.AddWithValue("$host", profile.Host);
            pending.Parameters.AddWithValue("$port", profile.Port);
            pending.Parameters.AddWithValue("$type", keyType);
            pending.Parameters.AddWithValue("$sha", fingerprint);
            pending.Parameters.AddWithValue("$now", now);
            pending.Parameters.AddWithValue("$revision", revision);
            pending.ExecuteNonQuery();
        }
        InsertHostKeyEvent(db, transaction, correlationId, profile, keyType, fingerprint, now, "PENDING",
            string.IsNullOrEmpty(currentFingerprint) ? "FIRST_TRUST_PENDING" : "HOST_KEY_CHANGED_AUTO_UPDATE");
        transaction.Commit();
        return new(true, string.IsNullOrEmpty(currentFingerprint) ? "FIRST_TRUST" : "AUTO_UPDATE",
            correlationId, keyType, fingerprint, string.IsNullOrEmpty(currentFingerprint)
                ? "FIRST_TRUST_PENDING" : "HOST_KEY_CHANGED_AUTO_UPDATE");
    }

    public bool CommitSshHostKey(string correlationId)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var pending = db.CreateCommand();
        pending.Transaction = transaction;
        pending.CommandText = @"SELECT device_id,host,port,key_type,sha256,prior_revision,state
            FROM ssh_host_key_pending WHERE correlation_id=$correlation";
        pending.Parameters.AddWithValue("$correlation", correlationId);
        using var reader = pending.ExecuteReader();
        if (!reader.Read()) return false;
        var device = reader.GetString(0);
        var host = reader.GetString(1);
        var port = reader.GetInt32(2);
        var type = reader.GetString(3);
        var sha = reader.GetString(4);
        var priorRevision = reader.GetInt64(5);
        var state = reader.GetString(6);
        reader.Close();
        if (state == "COMMITTED") { transaction.Commit(); return true; }
        if (state != "PENDING") { transaction.Commit(); return false; }

        using var current = db.CreateCommand();
        current.Transaction = transaction;
        current.CommandText = "SELECT revision,current_sha256 FROM ssh_host_keys WHERE device_id=$device";
        current.Parameters.AddWithValue("$device", device);
        using var currentReader = current.ExecuteReader();
        if (!currentReader.Read()) { transaction.Commit(); return false; }
        var revision = currentReader.GetInt64(0);
        var currentSha = currentReader.IsDBNull(1) ? null : currentReader.GetString(1);
        currentReader.Close();
        if (revision != priorRevision && !H3cHostKeyFingerprint.Matches(currentSha ?? string.Empty, sha))
        {
            MarkHostKeyPendingFailed(db, transaction, correlationId, device, host, port, type, sha,
                "CONCURRENT_HOST_KEY_UPDATE");
            transaction.Commit();
            return false;
        }

        var now = UtcNowMs();
        using (var update = db.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = @"UPDATE ssh_host_keys SET host=$host,port=$port,current_key_type=$type,
                current_sha256=$sha,first_seen_ms=COALESCE(first_seen_ms,$now),last_verified_ms=$now,
                last_changed_ms=CASE WHEN current_sha256 IS NULL OR current_sha256<>$sha THEN $now ELSE last_changed_ms END,
                revision=revision+CASE WHEN current_sha256 IS NULL OR current_sha256<>$sha THEN 1 ELSE 0 END
                WHERE device_id=$device";
            update.Parameters.AddWithValue("$host", host);
            update.Parameters.AddWithValue("$port", port);
            update.Parameters.AddWithValue("$type", type);
            update.Parameters.AddWithValue("$sha", sha);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$device", device);
            update.ExecuteNonQuery();
        }
        using (var profile = db.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText = "UPDATE h3c_device_profiles SET host_key_sha256=$sha,updated_ms=$now WHERE id=$device";
            profile.Parameters.AddWithValue("$sha", sha);
            profile.Parameters.AddWithValue("$now", now);
            profile.Parameters.AddWithValue("$device", device);
            profile.ExecuteNonQuery();
        }
        using (var stateUpdate = db.CreateCommand())
        {
            stateUpdate.Transaction = transaction;
            stateUpdate.CommandText = "UPDATE ssh_host_key_pending SET state='COMMITTED',detail='Connection authenticated; new host key is effective.' WHERE correlation_id=$correlation";
            stateUpdate.Parameters.AddWithValue("$correlation", correlationId);
            stateUpdate.ExecuteNonQuery();
        }
        InsertHostKeyEvent(db, transaction, correlationId,
            new H3cDeviceProfileSnapshot(device, host, port, string.Empty, string.Empty, sha, true, 0, 0),
            type, sha, now, "EFFECTIVE", "HOST_KEY_EFFECTIVE");
        transaction.Commit();
        return true;
    }

    public void FailSshHostKey(string correlationId, string reason)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var pending = db.CreateCommand();
        pending.Transaction = transaction;
        pending.CommandText = "SELECT device_id,host,port,key_type,sha256 FROM ssh_host_key_pending WHERE correlation_id=$correlation AND state='PENDING'";
        pending.Parameters.AddWithValue("$correlation", correlationId);
        using var reader = pending.ExecuteReader();
        if (!reader.Read()) { transaction.Commit(); return; }
        var device = reader.GetString(0); var host = reader.GetString(1); var port = reader.GetInt32(2);
        var type = reader.GetString(3); var sha = reader.GetString(4); reader.Close();
        MarkHostKeyPendingFailed(db, transaction, correlationId, device, host, port, type, sha, reason);
        transaction.Commit();
    }

    public IReadOnlyList<SshHostKeyHistorySnapshot> SshHostKeyHistory(string deviceId, int limit = 100)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT id,device_id,host,port,key_type,sha256,observed_ms,state,reason,correlation_id
            FROM ssh_host_key_events WHERE device_id=$device ORDER BY observed_ms DESC,id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var result = new List<SshHostKeyHistorySnapshot>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9)));
        return result;
    }

    private static string RecordHostKeyEvent(SqliteConnection db, SqliteTransaction transaction,
        H3cDeviceProfileSnapshot profile, string keyType, string sha, string state, string reason)
    {
        var correlation = Guid.NewGuid().ToString("N");
        InsertHostKeyEvent(db, transaction, correlation, profile, keyType, sha, UtcNowMs(), state, reason);
        return correlation;
    }

    private static void InsertHostKeyEvent(SqliteConnection db, SqliteTransaction transaction, string correlation,
        H3cDeviceProfileSnapshot profile, string keyType, string sha, long now, string state, string reason)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT OR IGNORE INTO ssh_host_key_events
            (id,device_id,host,port,key_type,sha256,observed_ms,state,reason,correlation_id)
            VALUES($id,$device,$host,$port,$type,$sha,$now,$state,$reason,$correlation)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$device", profile.Id); command.Parameters.AddWithValue("$host", profile.Host);
        command.Parameters.AddWithValue("$port", profile.Port); command.Parameters.AddWithValue("$type", keyType);
        command.Parameters.AddWithValue("$sha", sha); command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$correlation", correlation); command.ExecuteNonQuery();
    }

    private static void MarkHostKeyPendingFailed(SqliteConnection db, SqliteTransaction transaction, string correlation,
        string device, string host, int port, string type, string sha, string reason)
    {
        using var update = db.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE ssh_host_key_pending SET state='FAILED',detail=$reason WHERE correlation_id=$correlation";
        update.Parameters.AddWithValue("$reason", reason); update.Parameters.AddWithValue("$correlation", correlation);
        update.ExecuteNonQuery();
        InsertHostKeyEvent(db, transaction, correlation,
            new H3cDeviceProfileSnapshot(device, host, port, string.Empty, string.Empty, sha, true, 0, 0),
            type, sha, UtcNowMs(), "FAILED", reason);
    }
}
