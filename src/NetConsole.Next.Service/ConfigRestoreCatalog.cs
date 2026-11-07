using System.Security.Cryptography;
using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record ConfigRestoreOperationSnapshot(
    string Id,
    string ActorUserId,
    string SourceSnapshotId,
    string TargetProfileId,
    string TargetDeviceId,
    string TargetSiteId,
    string SourceSha256,
    string? ExpectedCurrentSnapshotId,
    string State,
    string? AttemptId,
    string? Detail,
    long ExpiresMs,
    long CreatedMs,
    long UpdatedMs);

internal sealed partial class Catalog
{
    public ConfigRestoreOperationSnapshot CreateConfigRestoreOperation(
        string id, string actorUserId, string sourceSnapshotId, string targetProfileId,
        string targetDeviceId, string targetSiteId, string sourceSha256,
        string? expectedCurrentSnapshotId, string token, long expiresMs)
    {
        var now = UtcNowMs();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        using var db = Open();
        using var transaction = db.BeginTransaction(IsolationLevel.Serializable);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO config_restore_operations
            (id,actor_user_id,source_snapshot_id,target_profile_id,target_device_id,target_site_id,
             source_sha256,expected_current_snapshot_id,confirmation_token_sha256,expires_ms,state,
             created_ms,updated_ms)
            VALUES($id,$actor,$source,$profile,$device,$site,$sha,$current,$token,$expires,'PREVIEWED',$now,$now)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$actor", actorUserId);
        command.Parameters.AddWithValue("$source", sourceSnapshotId);
        command.Parameters.AddWithValue("$profile", targetProfileId);
        command.Parameters.AddWithValue("$device", targetDeviceId);
        command.Parameters.AddWithValue("$site", targetSiteId);
        command.Parameters.AddWithValue("$sha", sourceSha256);
        command.Parameters.AddWithValue("$current", (object?)expectedCurrentSnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("$token", hash);
        command.Parameters.AddWithValue("$expires", expiresMs);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        InsertDeviceAudit(db, transaction, actorUserId, "CONFIG_RESTORE_PREVIEW", id,
            new { sourceSnapshotId, targetProfileId, targetSiteId, expiresMs }, "SUCCEEDED");
        transaction.Commit();
        return GetConfigRestoreOperation(id) ?? throw new InvalidOperationException("Restore operation could not be loaded.");
    }

    public ConfigRestoreOperationSnapshot? GetConfigRestoreOperation(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT id,actor_user_id,source_snapshot_id,target_profile_id,target_device_id,
            target_site_id,source_sha256,expected_current_snapshot_id,state,attempt_id,detail,expires_ms,created_ms,updated_ms
            FROM config_restore_operations WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadConfigRestoreOperation(reader) : null;
    }

    public ConfigRestoreOperationSnapshot? TryClaimConfigRestoreOperation(string id, string actorUserId,
        string token, out string? failure)
    {
        failure = null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        using var db = Open();
        using var transaction = db.BeginTransaction(IsolationLevel.Serializable);
        var operation = ReadConfigRestoreOperation(db, transaction, id);
        if (operation is null || operation.ActorUserId != actorUserId) { failure = "CONFIG_RESTORE_PREVIEW_NOT_FOUND"; return null; }
        if (operation.State != "PREVIEWED") { failure = "CONFIG_RESTORE_PREVIEW_ALREADY_USED"; return null; }
        if (operation.ExpiresMs < UtcNowMs()) { failure = "CONFIG_RESTORE_PREVIEW_EXPIRED"; return null; }
        using (var verify = db.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "SELECT confirmation_token_sha256 FROM config_restore_operations WHERE id=$id";
            verify.Parameters.AddWithValue("$id", id);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(hash), Encoding.UTF8.GetBytes(Convert.ToString(verify.ExecuteScalar()) ?? string.Empty)))
            { failure = "CONFIG_RESTORE_CONFIRMATION_INVALID"; return null; }
        }
        using (var active = db.CreateCommand())
        {
            active.Transaction = transaction;
            active.CommandText = @"SELECT COUNT(*) FROM config_restore_operations
                WHERE target_profile_id=$profile AND state IN ('CLAIMED','EXECUTING','VERIFYING','ROLLBACK_PENDING','ROLLING_BACK','REQUIRES_REVIEW') AND id<>$id";
            active.Parameters.AddWithValue("$profile", operation.TargetProfileId);
            active.Parameters.AddWithValue("$id", id);
            if (Convert.ToInt32(active.ExecuteScalar()) != 0)
            { failure = "CONFIG_RESTORE_TARGET_BUSY"; return null; }
        }
        var now = UtcNowMs();
        var attempt = Guid.NewGuid().ToString("N");
        using (var update = db.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE config_restore_operations SET state='CLAIMED',attempt_id=$attempt,updated_ms=$now WHERE id=$id AND state='PREVIEWED'";
            update.Parameters.AddWithValue("$attempt", attempt); update.Parameters.AddWithValue("$now", now); update.Parameters.AddWithValue("$id", id);
            if (update.ExecuteNonQuery() != 1) { failure = "CONFIG_RESTORE_PREVIEW_ALREADY_USED"; return null; }
        }
        InsertDeviceAudit(db, transaction, actorUserId, "CONFIG_RESTORE_CLAIM", id,
            new { targetProfileId = operation.TargetProfileId, attemptId = attempt }, "ACCEPTED");
        transaction.Commit();
        return GetConfigRestoreOperation(id);
    }

    public bool ValidateConfigRestoreToken(ConfigRestoreOperationSnapshot operation, string actorUserId,
        string token, out string failure)
    {
        failure = string.Empty;
        if (operation.ActorUserId != actorUserId) { failure = "CONFIG_RESTORE_PREVIEW_NOT_FOUND"; return false; }
        if (operation.State != "PREVIEWED") { failure = "CONFIG_RESTORE_PREVIEW_ALREADY_USED"; return false; }
        if (operation.ExpiresMs < UtcNowMs()) { failure = "CONFIG_RESTORE_PREVIEW_EXPIRED"; return false; }
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT confirmation_token_sha256 FROM config_restore_operations WHERE id=$id";
        command.Parameters.AddWithValue("$id", operation.Id);
        var actual = Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual)))
        { failure = "CONFIG_RESTORE_CONFIRMATION_INVALID"; return false; }
        return true;
    }

    public ConfigRestoreOperationSnapshot SetConfigRestoreOperationState(string id, string state, string? detail)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(IsolationLevel.Serializable);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE config_restore_operations SET state=$state,detail=$detail,updated_ms=$now WHERE id=$id";
        command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", UtcNowMs()); command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("Restore operation was not found.");
        transaction.Commit();
        return GetConfigRestoreOperation(id)!;
    }

    private static void RecoverConfigRestoreOperations(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction(IsolationLevel.Serializable);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"UPDATE config_restore_operations SET state='REQUIRES_REVIEW',
            detail=COALESCE(detail,'Service restarted while device-side restore outcome was unknown.'),updated_ms=$now
            WHERE state IN ('CLAIMED','EXECUTING','VERIFYING','ROLLBACK_PENDING','ROLLING_BACK')";
        command.Parameters.AddWithValue("$now", UtcNowMs());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static ConfigRestoreOperationSnapshot ReadConfigRestoreOperation(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13));

    private static ConfigRestoreOperationSnapshot? ReadConfigRestoreOperation(SqliteConnection db, SqliteTransaction transaction, string id)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = @"SELECT id,actor_user_id,source_snapshot_id,target_profile_id,target_device_id,target_site_id,
            source_sha256,expected_current_snapshot_id,state,attempt_id,detail,expires_ms,created_ms,updated_ms
            FROM config_restore_operations WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadConfigRestoreOperation(reader) : null;
    }
}
