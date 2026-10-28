using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record DeviceBatchOperationRequest(
    string Kind,
    string IdempotencyKey,
    IReadOnlyList<string> DeviceIds,
    string? SiteId = null,
    string? GroupId = null,
    string? Name = null,
    string? Vendor = null,
    string? Model = null,
    string? DeviceType = null,
    string? Description = null);

internal sealed record DeviceBatchOperationItemView(
    string DeviceId,
    long ExpectedRevision,
    string BeforeJson,
    string RequestedJson,
    string Outcome,
    string Detail);

internal sealed record DeviceBatchOperationView(
    string Id,
    string Actor,
    string Kind,
    string State,
    int TargetCount,
    int CompletedCount,
    int ConflictCount,
    int FailedCount,
    long CreatedMs,
    long UpdatedMs,
    long? StartedMs,
    long? CompletedMs,
    string RequestJson,
    string ProgressJson,
    bool CanConfirm,
    bool CanCancel,
    bool CanRetry,
    IReadOnlyList<DeviceBatchOperationItemView> Items);

internal sealed class DeviceBatchResourceLimitException(string message) : InvalidOperationException(message);
internal sealed class DeviceBatchConflictException(string operationId, string message) : InvalidOperationException(message)
{
    public string OperationId { get; } = operationId;
}

internal sealed partial class Catalog
{
    private const int MaximumDevicesPerBatchOperation = 100;
    private const int MaximumPendingBatchOperations = 64;
    private const int MaximumActiveBatchOperationsPerActor = 8;
    private const int MaximumConcurrentBatchOperations = 4;
    private const int MaximumBatchRequestBytes = 256 * 1024;
    private const int MaximumBatchOperationRetention = 1000;
    private const int MaximumBatchAuditPayloadBytes = 8 * 1024;
    private static readonly TimeSpan BatchOperationRetention = TimeSpan.FromDays(7);
    private static readonly HashSet<string> BatchKinds = new(StringComparer.Ordinal)
    {
        "BATCH_ENABLE", "BATCH_DISABLE", "BATCH_SET_GROUP", "BATCH_SET_SITE", "BATCH_UPDATE_METADATA"
    };

    private sealed record BatchInventoryState(
        string DeviceId,
        string Name,
        string ManagementIp,
        string Vendor,
        string Model,
        string DeviceType,
        string SiteId,
        string? GroupId,
        string Description,
        bool Enabled,
        long UpdatedMs);

    private sealed record BatchItemDraft(
        string DeviceId,
        long ExpectedRevision,
        string BeforeJson,
        string RequestedJson,
        string Outcome,
        string Detail);

    private sealed record StoredBatchOperation(
        string Id,
        string Actor,
        string Kind,
        string State,
        string RequestJson,
        string ProgressJson,
        long CreatedMs,
        long UpdatedMs,
        long? StartedMs,
        long? CompletedMs,
        string IdempotencyKey,
        IReadOnlyList<DeviceBatchOperationItemView> Items);

    public DeviceBatchOperationView CreateDeviceBatchOperation(DeviceBatchOperationRequest request, string actor)
    {
        actor = RequiredText(actor, "actor", 128);
        request = NormalizeBatchRequest(request);
        var requestJson = JsonSerializer.Serialize(request);
        if (Encoding.UTF8.GetByteCount(requestJson) > MaximumBatchRequestBytes)
            throw new DeviceBatchResourceLimitException("Batch operation request exceeds the 256 KiB limit.");

        using var db = Open();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        TrimCompletedBatchOperations(db, transaction, UtcNowMs());
        var existingId = FindBatchByIdempotency(db, transaction, actor, request.IdempotencyKey);
        if (existingId is not null)
        {
            var existing = ReadStoredBatchOperation(db, transaction, existingId)
                ?? throw new InvalidOperationException("Existing batch operation could not be loaded.");
            if (!string.Equals(existing.RequestJson, requestJson, StringComparison.Ordinal))
                throw new InvalidOperationException("The idempotency key is already used for a different request.");
            transaction.Commit();
            return ToBatchView(existing);
        }

        EnforceBatchResourceLimits(db, transaction, actor);
        var drafts = BuildBatchPreview(db, transaction, request);
        var operationId = "op-" + Guid.NewGuid().ToString("N");
        var now = UtcNowMs();
        var state = drafts.Any(item => item.Outcome == "CONFLICT") ? "CONFLICT" : "PENDING_CONFIRM";
        var progress = JsonSerializer.Serialize(new { total = drafts.Count, completed = 0, conflicts = drafts.Count(item => item.Outcome == "CONFLICT"), failures = 0 });
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO device_operations
                (id,actor,kind,state,request_json,progress_json,created_ms,updated_ms,idempotency_key)
                VALUES($id,$actor,$kind,$state,$request,$progress,$created,$updated,$idempotency)";
            insert.Parameters.AddWithValue("$id", operationId);
            insert.Parameters.AddWithValue("$actor", actor);
            insert.Parameters.AddWithValue("$kind", request.Kind);
            insert.Parameters.AddWithValue("$state", state);
            insert.Parameters.AddWithValue("$request", requestJson);
            insert.Parameters.AddWithValue("$progress", progress);
            insert.Parameters.AddWithValue("$created", now);
            insert.Parameters.AddWithValue("$updated", now);
            insert.Parameters.AddWithValue("$idempotency", request.IdempotencyKey);
            insert.ExecuteNonQuery();
        }
        foreach (var item in drafts)
        {
            using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO device_operation_items
                (operation_id,device_id,expected_revision,before_json,requested_json,outcome,detail)
                VALUES($operation,$device,$revision,$before,$requested,$outcome,$detail)";
            insert.Parameters.AddWithValue("$operation", operationId);
            insert.Parameters.AddWithValue("$device", item.DeviceId);
            insert.Parameters.AddWithValue("$revision", item.ExpectedRevision);
            insert.Parameters.AddWithValue("$before", item.BeforeJson);
            insert.Parameters.AddWithValue("$requested", item.RequestedJson);
            insert.Parameters.AddWithValue("$outcome", item.Outcome);
            insert.Parameters.AddWithValue("$detail", item.Detail);
            insert.ExecuteNonQuery();
        }
        InsertDeviceAudit(db, transaction, actor, "DEVICE_BATCH_PREVIEW", operationId,
            new { correlationId = operationId, kind = request.Kind, targetCount = drafts.Count, safeChangedFields = ChangedFields(request), safeFailureReason = state == "CONFLICT" ? "PREVIEW_CONFLICT" : (string?)null },
            state == "CONFLICT" ? "CONFLICT" : "SUCCESS");
        transaction.Commit();
        return GetDeviceBatchOperation(operationId, actor) ?? throw new InvalidOperationException("Created batch operation could not be reloaded.");
    }

    public DeviceBatchOperationView? GetDeviceBatchOperation(string operationId, string actor)
    {
        actor = RequiredText(actor, "actor", 128);
        using var db = Open();
        var stored = ReadStoredBatchOperation(db, null, operationId);
        if (stored is null) return null;
        if (!string.Equals(stored.Actor, actor, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Batch operation belongs to another administrator.");
        return ToBatchView(stored);
    }

    public DeviceBatchOperationView ConfirmDeviceBatchOperation(string operationId, string actor)
    {
        actor = RequiredText(actor, "actor", 128);
        var claimed = ClaimBatchOperation(operationId, actor);
        if (claimed.State == "SUCCEEDED") return ToBatchView(claimed);
        DeviceBatchCrashTestHooks.WaitFor("AFTER_CLAIM");

        try
        {
            ExecuteBatchOperation(claimed);
        }
        catch (DeviceBatchConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FinalizeBatchFailure(claimed.Id, actor, ex);
            throw;
        }
        return GetDeviceBatchOperation(operationId, actor) ?? throw new InvalidOperationException("Batch operation disappeared after commit.");
    }

    public DeviceBatchOperationView CancelDeviceBatchOperation(string operationId, string actor)
    {
        actor = RequiredText(actor, "actor", 128);
        using var db = Open();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var stored = ReadStoredBatchOperation(db, transaction, operationId)
            ?? throw new KeyNotFoundException("Batch operation was not found.");
        EnsureBatchOwner(stored, actor);
        if (stored.State == "SUCCEEDED" || stored.State == "CANCELLED")
        {
            transaction.Commit();
            return ToBatchView(stored);
        }
        if (stored.State == "RUNNING" || stored.State == "QUEUED")
            throw new InvalidOperationException("A running batch operation cannot be cancelled safely.");
        if (stored.State is not ("PREVIEWED" or "PENDING_CONFIRM" or "FAILED"))
            throw new InvalidOperationException("Only a pending or failed batch operation can be cancelled.");
        var now = UtcNowMs();
        UpdateBatchState(db, transaction, operationId, "CANCELLED", now, now,
            JsonSerializer.Serialize(new { total = stored.Items.Count, completed = 0, conflicts = 0, failures = 0, cancelled = true }));
        MarkBatchItems(db, transaction, operationId, "PENDING", "CANCELLED", "Cancelled before commit.");
        InsertDeviceAudit(db, transaction, actor, "DEVICE_BATCH_CANCEL", operationId,
            new { correlationId = operationId, kind = stored.Kind, targetCount = stored.Items.Count, safeChangedFields = Array.Empty<string>(), safeFailureReason = (string?)null });
        transaction.Commit();
        return GetDeviceBatchOperation(operationId, actor) ?? throw new InvalidOperationException("Cancelled batch operation could not be reloaded.");
    }

    public bool TryRecordDeviceAuditEvent(string actor, string action, string outcome, string subjectId,
        string? correlationId, IReadOnlyList<string>? safeChangedFields, string? safeFailureReason)
    {
        try
        {
            actor = RequiredText(actor, "actor", 128);
            action = RequiredText(action, "action", 128);
            subjectId = RequiredText(subjectId, "subjectId", 128);
            outcome = RequiredText(outcome, "outcome", 32);
            safeFailureReason = SafeAuditText(safeFailureReason, 512);
            var detail = new
            {
                actor,
                subjectId,
                correlationId = SafeAuditText(correlationId, 128),
                safeChangedFields = (safeChangedFields ?? Array.Empty<string>()).Take(32).ToArray(),
                safeFailureReason
            };
            var payload = JsonSerializer.Serialize(detail);
            if (Encoding.UTF8.GetByteCount(payload) > MaximumBatchAuditPayloadBytes)
                throw new InvalidOperationException("Audit payload exceeds the safe limit.");
            using var db = Open();
            using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
            InsertDeviceAudit(db, transaction, actor, action, subjectId, detail, outcome);
            transaction.Commit();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DEVICE_AUDIT_WRITE_FAILED:{action}:{ex.GetType().Name}");
            return false;
        }
    }

    private static void RecoverDeviceBatchOperations(SqliteConnection db)
    {
        var interrupted = new List<(string Id, string Actor, string Kind, int Total)>();
        using (var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable))
        {
            using var query = db.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = @"SELECT o.id,o.actor,o.kind,COUNT(i.device_id)
                FROM device_operations o LEFT JOIN device_operation_items i ON i.operation_id=o.id
                WHERE o.state IN ('QUEUED','RUNNING') GROUP BY o.id,o.actor,o.kind";
            using var reader = query.ExecuteReader();
            while (reader.Read()) interrupted.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
            reader.Close();
            foreach (var operation in interrupted)
            {
                var now = UtcNowMs();
                UpdateBatchState(db, transaction, operation.Id, "INTERRUPTED", now, now,
                    JsonSerializer.Serialize(new { total = operation.Total, completed = 0, conflicts = 0, failures = 1, reason = "SERVICE_RESTART" }));
            }
            transaction.Commit();
        }

        foreach (var operation in interrupted)
            TryWriteBatchAudit(db, operation.Actor, "DEVICE_BATCH_INTERRUPTED", operation.Id,
                new { correlationId = operation.Id, kind = operation.Kind, targetCount = operation.Total, safeChangedFields = Array.Empty<string>(), safeFailureReason = "SERVICE_RESTART" }, "FAILED");
    }

    private static DeviceBatchOperationRequest NormalizeBatchRequest(DeviceBatchOperationRequest request)
    {
        var kind = RequiredText(request.Kind, "kind", 64).ToUpperInvariant();
        if (!BatchKinds.Contains(kind)) throw new ArgumentException("Unsupported batch operation kind.", nameof(request));
        var key = RequiredText(request.IdempotencyKey, "idempotencyKey", 128);
        var ids = (request.DeviceIds ?? Array.Empty<string>()).Select(id => (id ?? string.Empty).Trim()).ToArray();
        if (ids.Length is < 1 or > MaximumDevicesPerBatchOperation)
            throw new DeviceBatchResourceLimitException($"A batch operation must contain 1-{MaximumDevicesPerBatchOperation} explicit device IDs.");
        if (ids.Any(id => !IsValidDeviceId(id)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("Device IDs must be unique valid identifiers.", nameof(request));
        var site = request.SiteId is null ? null : RequiredText(request.SiteId, "siteId", 80);
        var group = request.GroupId is null ? null : RequiredText(request.GroupId, "groupId", 96);
        if (kind == "BATCH_SET_SITE" && site is null) throw new ArgumentException("siteId is required for BATCH_SET_SITE.", nameof(request));
        if (kind == "BATCH_SET_GROUP" && group is null) throw new ArgumentException("groupId is required for BATCH_SET_GROUP.", nameof(request));
        if (kind == "BATCH_UPDATE_METADATA" && request.Name is null && request.Vendor is null && request.Model is null && request.DeviceType is null && request.Description is null)
            throw new ArgumentException("At least one metadata field is required.", nameof(request));
        var name = request.Name is null ? null : RequiredText(request.Name, "name", 120);
        var vendor = request.Vendor is null ? null : Clean(request.Vendor, 80);
        var model = request.Model is null ? null : Clean(request.Model, 80);
        var type = request.DeviceType is null ? null : request.DeviceType.Trim().ToUpperInvariant();
        if (type is not null && !IsValidToken(type, 48)) throw new ArgumentException("deviceType contains unsupported characters.", nameof(request));
        var description = request.Description is null ? null : Clean(request.Description, 500);
        return new DeviceBatchOperationRequest(kind, key, ids, site, group, name, vendor, model, type, description);
    }

    private static IReadOnlyList<BatchItemDraft> BuildBatchPreview(SqliteConnection db, SqliteTransaction transaction, DeviceBatchOperationRequest request)
    {
        var drafts = new List<BatchItemDraft>(request.DeviceIds.Count);
        foreach (var deviceId in request.DeviceIds)
        {
            var before = ReadBatchInventoryState(db, transaction, deviceId);
            if (before is null)
            {
                drafts.Add(new BatchItemDraft(deviceId, 0, "{}", JsonSerializer.Serialize(new { deviceId }), "CONFLICT", "DEVICE_NOT_FOUND"));
                continue;
            }
            var requested = ApplyBatchRequest(before, request);
            var outcome = "PENDING";
            var detail = "READY";
            if (request.Kind == "BATCH_SET_GROUP")
            {
                var groupSite = ReadGroupSite(db, transaction, requested.GroupId!);
                if (groupSite is null) { outcome = "CONFLICT"; detail = "GROUP_NOT_FOUND"; }
                else if (!string.Equals(groupSite, requested.SiteId, StringComparison.Ordinal)) { outcome = "CONFLICT"; detail = "GROUP_SITE_MISMATCH"; }
            }
            if (request.Kind == "BATCH_SET_SITE" && !SiteExists(db, requested.SiteId))
            {
                outcome = "CONFLICT"; detail = "SITE_NOT_FOUND";
            }
            drafts.Add(new BatchItemDraft(deviceId, before.UpdatedMs, JsonSerializer.Serialize(before), JsonSerializer.Serialize(requested), outcome, detail));
        }

        var targetIds = request.DeviceIds.ToHashSet(StringComparer.Ordinal);
        foreach (var group in drafts.Where(item => item.Outcome == "PENDING")
                     .Select(item => JsonSerializer.Deserialize<BatchInventoryState>(item.RequestedJson)!)
                     .GroupBy(item => $"{item.SiteId}\n{item.ManagementIp}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            foreach (var item in drafts.Where(item => item.Outcome == "PENDING" && string.Equals(
                         $"{JsonSerializer.Deserialize<BatchInventoryState>(item.RequestedJson)!.SiteId}\n{JsonSerializer.Deserialize<BatchInventoryState>(item.RequestedJson)!.ManagementIp}", group.Key, StringComparison.OrdinalIgnoreCase)))
                drafts[drafts.IndexOf(item)] = item with { Outcome = "CONFLICT", Detail = "DUPLICATE_TARGET_ADDRESS" };
        }
        foreach (var item in drafts.Where(item => item.Outcome == "PENDING"))
        {
            var requested = JsonSerializer.Deserialize<BatchInventoryState>(item.RequestedJson)!;
            using var conflict = db.CreateCommand();
            conflict.Transaction = transaction;
            conflict.CommandText = "SELECT id FROM device_inventory WHERE site_id=$site AND management_ip=$ip AND id<>$id LIMIT 1";
            conflict.Parameters.AddWithValue("$site", requested.SiteId); conflict.Parameters.AddWithValue("$ip", requested.ManagementIp); conflict.Parameters.AddWithValue("$id", item.DeviceId);
            if (conflict.ExecuteScalar() is string other && !targetIds.Contains(other))
                drafts[drafts.IndexOf(item)] = item with { Outcome = "CONFLICT", Detail = "MANAGEMENT_IP_CONFLICT" };
        }
        return drafts;
    }

    private static BatchInventoryState ApplyBatchRequest(BatchInventoryState before, DeviceBatchOperationRequest request) => before with
    {
        Enabled = request.Kind == "BATCH_ENABLE" ? true : request.Kind == "BATCH_DISABLE" ? false : before.Enabled,
        SiteId = request.Kind == "BATCH_SET_SITE" ? request.SiteId! : before.SiteId,
        GroupId = request.Kind == "BATCH_SET_SITE" ? null : request.Kind == "BATCH_SET_GROUP" ? request.GroupId : before.GroupId,
        Name = request.Name ?? before.Name,
        Vendor = request.Vendor ?? before.Vendor,
        Model = request.Model ?? before.Model,
        DeviceType = request.DeviceType ?? before.DeviceType,
        Description = request.Description ?? before.Description
    };

    private static BatchInventoryState? ReadBatchInventoryState(SqliteConnection db, SqliteTransaction? transaction, string id)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,name,management_ip,vendor,model,device_type,site_id,group_id,description,enabled,updated_ms FROM device_inventory WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new BatchInventoryState(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetInt32(9) != 0, reader.GetInt64(10));
    }

    private static string? ReadGroupSite(SqliteConnection db, SqliteTransaction transaction, string groupId)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT site_id FROM device_groups WHERE id=$id"; command.Parameters.AddWithValue("$id", groupId);
        return command.ExecuteScalar()?.ToString();
    }

    private StoredBatchOperation ClaimBatchOperation(string operationId, string actor)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var stored = ReadStoredBatchOperation(db, transaction, operationId) ?? throw new KeyNotFoundException("Batch operation was not found.");
        EnsureBatchOwner(stored, actor);
        if (stored.State == "SUCCEEDED") { transaction.Commit(); return stored; }
        if (stored.State is "RUNNING" or "QUEUED") throw new InvalidOperationException("Batch operation is already running.");
        if (stored.State is "CONFLICT" or "CANCELLED" or "INTERRUPTED") throw new InvalidOperationException("Batch operation must be previewed again before confirmation.");
        if (stored.State is not ("PENDING_CONFIRM" or "FAILED")) throw new InvalidOperationException("Batch operation is not confirmable.");
        if (stored.State == "FAILED")
        {
            MarkBatchItems(db, transaction, operationId, "FAILED", "PENDING", "RETRY_PENDING");
            stored = ReadStoredBatchOperation(db, transaction, operationId)
                ?? throw new InvalidOperationException("Failed batch operation could not be reloaded for retry.");
        }
        var active = CountBatchOperations(db, transaction, "state='RUNNING'");
        if (active >= MaximumConcurrentBatchOperations) throw new DeviceBatchResourceLimitException("Too many batch operations are running.");
        var now = UtcNowMs();
        UpdateBatchState(db, transaction, operationId, "RUNNING", now, null, JsonSerializer.Serialize(new { total = stored.Items.Count, completed = 0, conflicts = 0, failures = 0 }));
        InsertDeviceAudit(db, transaction, actor, "DEVICE_BATCH_CONFIRM", operationId,
            new { correlationId = operationId, kind = stored.Kind, targetCount = stored.Items.Count, safeChangedFields = ChangedFields(stored.RequestJson), safeFailureReason = (string?)null });
        InsertDeviceAudit(db, transaction, actor, "DEVICE_BATCH_START", operationId,
            new { correlationId = operationId, kind = stored.Kind, targetCount = stored.Items.Count, safeChangedFields = ChangedFields(stored.RequestJson), safeFailureReason = (string?)null });
        if (stored.State == "FAILED")
            InsertDeviceAudit(db, transaction, actor, "DEVICE_BATCH_RETRY", operationId,
                new { correlationId = operationId, kind = stored.Kind, targetCount = stored.Items.Count, safeChangedFields = ChangedFields(stored.RequestJson), safeFailureReason = (string?)null });
        transaction.Commit();
        return GetStoredBatchOperationForOwner(operationId, actor) ?? throw new InvalidOperationException("Batch operation could not be claimed.");
    }

    private void ExecuteBatchOperation(StoredBatchOperation claimed)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var conflicts = new List<(string DeviceId, string Detail)>();
        foreach (var item in claimed.Items)
        {
            if (item.Outcome != "PENDING") continue;
            var current = ReadBatchInventoryState(db, transaction, item.DeviceId);
            var requested = JsonSerializer.Deserialize<BatchInventoryState>(item.RequestedJson)
                ?? throw new InvalidOperationException("Batch request state is invalid.");
            if (current is null) conflicts.Add((item.DeviceId, "DEVICE_NOT_FOUND"));
            else if (current.UpdatedMs != item.ExpectedRevision) conflicts.Add((item.DeviceId, "DEVICE_VERSION_CONFLICT"));
            else if (ReadGroupSite(db, transaction, requested.GroupId ?? string.Empty) is null && requested.GroupId is not null) conflicts.Add((item.DeviceId, "GROUP_NOT_FOUND"));
            else if (requested.GroupId is not null && !string.Equals(ReadGroupSite(db, transaction, requested.GroupId), requested.SiteId, StringComparison.Ordinal)) conflicts.Add((item.DeviceId, "GROUP_SITE_MISMATCH"));
            else if (!SiteExists(db, requested.SiteId)) conflicts.Add((item.DeviceId, "SITE_NOT_FOUND"));
            else
            {
                using var address = db.CreateCommand(); address.Transaction = transaction;
                address.CommandText = "SELECT id FROM device_inventory WHERE site_id=$site AND management_ip=$ip AND id<>$id LIMIT 1";
                address.Parameters.AddWithValue("$site", requested.SiteId); address.Parameters.AddWithValue("$ip", requested.ManagementIp); address.Parameters.AddWithValue("$id", requested.DeviceId);
                if (address.ExecuteScalar() is not null) conflicts.Add((item.DeviceId, "MANAGEMENT_IP_CONFLICT"));
            }
        }
        if (conflicts.Count > 0)
        {
            transaction.Rollback();
            FinalizeBatchConflict(claimed, conflicts);
            throw new DeviceBatchConflictException(claimed.Id, "One or more devices changed after preview; the batch was not written.");
        }
        var now = UtcNowMs();
        var updateIndex = 0;
        foreach (var item in claimed.Items.Where(item => item.Outcome == "PENDING"))
        {
            var requested = JsonSerializer.Deserialize<BatchInventoryState>(item.RequestedJson)!;
            using var update = db.CreateCommand(); update.Transaction = transaction;
            update.CommandText = @"UPDATE device_inventory SET name=$name,management_ip=$ip,vendor=$vendor,model=$model,
                device_type=$type,site_id=$site,group_id=$group,description=$description,enabled=$enabled,
                updated_ms=CASE WHEN $updated<=updated_ms THEN updated_ms+1 ELSE $updated END
                WHERE id=$id AND updated_ms=$expected";
            update.Parameters.AddWithValue("$id", requested.DeviceId); update.Parameters.AddWithValue("$name", requested.Name); update.Parameters.AddWithValue("$ip", requested.ManagementIp);
            update.Parameters.AddWithValue("$vendor", requested.Vendor); update.Parameters.AddWithValue("$model", requested.Model); update.Parameters.AddWithValue("$type", requested.DeviceType);
            update.Parameters.AddWithValue("$site", requested.SiteId); update.Parameters.AddWithValue("$group", (object?)requested.GroupId ?? DBNull.Value); update.Parameters.AddWithValue("$description", requested.Description);
            update.Parameters.AddWithValue("$enabled", requested.Enabled ? 1 : 0); update.Parameters.AddWithValue("$updated", now); update.Parameters.AddWithValue("$expected", item.ExpectedRevision);
            if (update.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                FinalizeBatchConflict(claimed, new[] { (requested.DeviceId, "DEVICE_VERSION_CONFLICT") });
                throw new DeviceBatchConflictException(claimed.Id, "A device changed during batch commit.");
            }
            MarkBatchItem(db, transaction, claimed.Id, requested.DeviceId, "UPDATED", "UPDATED");
            updateIndex++;
            DeviceBatchCrashTestHooks.WaitFor($"AFTER_UPDATE_{updateIndex}");
        }
        var progress = JsonSerializer.Serialize(new { total = claimed.Items.Count, completed = claimed.Items.Count, conflicts = 0, failures = 0 });
        UpdateBatchState(db, transaction, claimed.Id, "SUCCEEDED", now, now, progress);
        InsertDeviceAudit(db, transaction, claimed.Actor, "DEVICE_BATCH_SUCCESS", claimed.Id,
            new { correlationId = claimed.Id, kind = claimed.Kind, targetCount = claimed.Items.Count, safeChangedFields = ChangedFields(claimed.RequestJson), safeFailureReason = (string?)null });
        transaction.Commit();
        DeviceBatchCrashTestHooks.WaitFor("AFTER_COMMIT");
    }

    private void FinalizeBatchConflict(StoredBatchOperation claimed, IReadOnlyList<(string DeviceId, string Detail)> conflicts)
    {
        try
        {
            using var db = Open();
            var detailById = conflicts.ToDictionary(item => item.DeviceId, item => item.Detail, StringComparer.Ordinal);
            using (var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable))
            {
                foreach (var item in claimed.Items.Where(item => item.Outcome == "PENDING"))
                    MarkBatchItem(db, transaction, claimed.Id, item.DeviceId, detailById.TryGetValue(item.DeviceId, out var detail) ? "CONFLICT" : "FAILED", detailById.TryGetValue(item.DeviceId, out detail) ? detail : "BATCH_CONFLICT");
                var now = UtcNowMs();
                UpdateBatchState(db, transaction, claimed.Id, "CONFLICT", now, now,
                    JsonSerializer.Serialize(new { total = claimed.Items.Count, completed = 0, conflicts = conflicts.Count, failures = 0 }));
                transaction.Commit();
            }
            TryWriteBatchAudit(db, claimed.Actor, "DEVICE_BATCH_CONFLICT", claimed.Id,
                new { correlationId = claimed.Id, kind = claimed.Kind, targetCount = claimed.Items.Count, safeChangedFields = ChangedFields(claimed.RequestJson), safeFailureReason = "DEVICE_VERSION_OR_SCOPE_CONFLICT" }, "CONFLICT");
        }
        catch (Exception ex) { Console.Error.WriteLine($"DEVICE_BATCH_CONFLICT_AUDIT_FAILED:{claimed.Id}:{ex.GetType().Name}"); }
    }

    private void FinalizeBatchFailure(string operationId, string actor, Exception exception)
    {
        try
        {
            using var db = Open();
            var stored = ReadStoredBatchOperation(db, null, operationId);
            if (stored is null) return;
            using (var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable))
            {
                stored = ReadStoredBatchOperation(db, transaction, operationId);
                if (stored is null) return;
                foreach (var item in stored.Items.Where(item => item.Outcome == "PENDING")) MarkBatchItem(db, transaction, operationId, item.DeviceId, "FAILED", "BATCH_FAILED");
                var now = UtcNowMs();
                UpdateBatchState(db, transaction, operationId, "FAILED", now, now,
                    JsonSerializer.Serialize(new { total = stored.Items.Count, completed = 0, conflicts = 0, failures = stored.Items.Count, reason = "BATCH_FAILED" }));
                transaction.Commit();
            }
            TryWriteBatchAudit(db, actor, "DEVICE_BATCH_FAILED", operationId,
                new { correlationId = operationId, kind = stored.Kind, targetCount = stored.Items.Count, safeChangedFields = ChangedFields(stored.RequestJson), safeFailureReason = SafeFailureCode(exception) }, "FAILED");
        }
        catch (Exception ex) { Console.Error.WriteLine($"DEVICE_BATCH_FAILURE_AUDIT_FAILED:{operationId}:{ex.GetType().Name}"); }
    }

    private static void TryWriteBatchAudit(SqliteConnection db, string actor, string action, string subjectId, object detail, string outcome)
    {
        try
        {
            if (action == "DEVICE_BATCH_FAILED") DeviceBatchCrashTestHooks.WaitFor("BEFORE_FAILURE_AUDIT");
            using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
            InsertDeviceAudit(db, transaction, actor, action, subjectId, detail, outcome);
            transaction.Commit();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DEVICE_BATCH_AUDIT_WRITE_FAILED:{action}:{subjectId}:{ex.GetType().Name}");
            if (action == "DEVICE_BATCH_FAILED") DeviceBatchCrashTestHooks.WaitFor("AFTER_FAILURE_AUDIT_ERROR");
        }
    }

    private static string SafeFailureCode(Exception exception) => exception switch
    {
        DeviceBatchConflictException => "DEVICE_VERSION_OR_SCOPE_CONFLICT",
        SqliteException => "SQLITE_WRITE_FAILURE",
        UnauthorizedAccessException => "AUTHORIZATION_FAILURE",
        _ => "BATCH_EXECUTION_FAILURE"
    };

    private StoredBatchOperation? GetStoredBatchOperationForOwner(string operationId, string actor)
    {
        using var db = Open();
        var stored = ReadStoredBatchOperation(db, null, operationId);
        if (stored is not null) EnsureBatchOwner(stored, actor);
        return stored;
    }

    private static StoredBatchOperation? ReadStoredBatchOperation(SqliteConnection db, SqliteTransaction? transaction, string operationId)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = @"SELECT id,actor,kind,state,request_json,progress_json,created_ms,updated_ms,started_ms,completed_ms,idempotency_key
            FROM device_operations WHERE id=$id";
        command.Parameters.AddWithValue("$id", operationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var stored = new StoredBatchOperation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.GetString(10), Array.Empty<DeviceBatchOperationItemView>());
        reader.Close();
        return stored with { Items = ReadBatchItems(db, transaction, operationId) };
    }

    private static IReadOnlyList<DeviceBatchOperationItemView> ReadBatchItems(SqliteConnection db, SqliteTransaction? transaction, string operationId)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT device_id,expected_revision,before_json,requested_json,outcome,detail FROM device_operation_items WHERE operation_id=$id ORDER BY rowid";
        command.Parameters.AddWithValue("$id", operationId);
        using var reader = command.ExecuteReader(); var result = new List<DeviceBatchOperationItemView>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return result;
    }

    private static DeviceBatchOperationView ToBatchView(StoredBatchOperation operation)
    {
        var completed = operation.Items.Count(item => item.Outcome == "UPDATED");
        var conflicts = operation.Items.Count(item => item.Outcome == "CONFLICT");
        var failed = operation.Items.Count(item => item.Outcome == "FAILED");
        return new DeviceBatchOperationView(operation.Id, operation.Actor, operation.Kind, operation.State, operation.Items.Count, completed, conflicts, failed,
            operation.CreatedMs, operation.UpdatedMs, operation.StartedMs, operation.CompletedMs, operation.RequestJson, operation.ProgressJson,
            operation.State is "PENDING_CONFIRM" or "FAILED", operation.State is "PREVIEWED" or "PENDING_CONFIRM" or "FAILED",
            operation.State == "FAILED", operation.Items);
    }

    private static void EnsureBatchOwner(StoredBatchOperation operation, string actor)
    {
        if (!string.Equals(operation.Actor, actor, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Batch operation belongs to another administrator.");
    }

    private static string? FindBatchByIdempotency(SqliteConnection db, SqliteTransaction transaction, string actor, string key)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT id FROM device_operations WHERE actor=$actor AND idempotency_key=$key";
        command.Parameters.AddWithValue("$actor", actor); command.Parameters.AddWithValue("$key", key); return command.ExecuteScalar()?.ToString();
    }

    private static int CountBatchOperations(SqliteConnection db, SqliteTransaction transaction, string predicate)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = $"SELECT COUNT(*) FROM device_operations WHERE {predicate}"; return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void EnforceBatchResourceLimits(SqliteConnection db, SqliteTransaction transaction, string actor)
    {
        if (CountBatchOperations(db, transaction, "state IN ('PREVIEWED','PENDING_CONFIRM','QUEUED','RUNNING')") >= MaximumPendingBatchOperations)
            throw new DeviceBatchResourceLimitException("Too many pending batch operations.");
        using var actorCount = db.CreateCommand(); actorCount.Transaction = transaction; actorCount.CommandText = "SELECT COUNT(*) FROM device_operations WHERE actor=$actor AND state IN ('PREVIEWED','PENDING_CONFIRM','QUEUED','RUNNING')"; actorCount.Parameters.AddWithValue("$actor", actor);
        if (Convert.ToInt32(actorCount.ExecuteScalar()) >= MaximumActiveBatchOperationsPerActor) throw new DeviceBatchResourceLimitException("Too many active batch operations for this administrator.");
    }

    private static void TrimCompletedBatchOperations(SqliteConnection db, SqliteTransaction transaction, long now)
    {
        using var count = db.CreateCommand(); count.Transaction = transaction; count.CommandText = "SELECT COUNT(*) FROM device_operations";
        if (Convert.ToInt32(count.ExecuteScalar()) <= MaximumBatchOperationRetention) return;
        using var delete = db.CreateCommand(); delete.Transaction = transaction;
        delete.CommandText = @"DELETE FROM device_operations WHERE id IN (
            SELECT id FROM device_operations WHERE state IN ('SUCCEEDED','FAILED','CONFLICT','CANCELLED','INTERRUPTED')
            AND COALESCE(completed_ms,updated_ms)<$cutoff ORDER BY COALESCE(completed_ms,updated_ms) LIMIT 100)";
        delete.Parameters.AddWithValue("$cutoff", now - (long)BatchOperationRetention.TotalMilliseconds); delete.ExecuteNonQuery();
    }

    private static void UpdateBatchState(SqliteConnection db, SqliteTransaction transaction, string id, string state, long updatedMs, long? completedMs, string progress)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE device_operations SET state=$state,updated_ms=$updated,completed_ms=$completed,progress_json=$progress WHERE id=$id";
        command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$updated", updatedMs); command.Parameters.AddWithValue("$completed", (object?)completedMs ?? DBNull.Value); command.Parameters.AddWithValue("$progress", progress); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }

    private static void MarkBatchItem(SqliteConnection db, SqliteTransaction transaction, string operationId, string deviceId, string outcome, string detail)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE device_operation_items SET outcome=$outcome,detail=$detail WHERE operation_id=$operation AND device_id=$device";
        command.Parameters.AddWithValue("$outcome", outcome); command.Parameters.AddWithValue("$detail", detail); command.Parameters.AddWithValue("$operation", operationId); command.Parameters.AddWithValue("$device", deviceId); command.ExecuteNonQuery();
    }

    private static void MarkBatchItems(SqliteConnection db, SqliteTransaction transaction, string operationId, string fromOutcome, string outcome, string detail)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE device_operation_items SET outcome=$outcome,detail=$detail WHERE operation_id=$operation AND outcome=$from";
        command.Parameters.AddWithValue("$outcome", outcome); command.Parameters.AddWithValue("$detail", detail); command.Parameters.AddWithValue("$operation", operationId); command.Parameters.AddWithValue("$from", fromOutcome); command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ChangedFields(DeviceBatchOperationRequest request) => request.Kind switch
    {
        "BATCH_ENABLE" or "BATCH_DISABLE" => new[] { "enabled" },
        "BATCH_SET_GROUP" => new[] { "groupId" },
        "BATCH_SET_SITE" => new[] { "siteId", "groupId" },
        _ => new[] { "name", "vendor", "model", "deviceType", "description" }
    };

    private static IReadOnlyList<string> ChangedFields(string requestJson)
    {
        try { return ChangedFields(JsonSerializer.Deserialize<DeviceBatchOperationRequest>(requestJson)!); }
        catch { return Array.Empty<string>(); }
    }

    private static string? SafeAuditText(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length > max || text.Any(char.IsControl) ? null : text;
    }
}
