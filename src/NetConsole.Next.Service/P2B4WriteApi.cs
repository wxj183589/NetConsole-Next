using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed class P2B4WriteException(string code, string detail, int statusCode = 409, string? operationId = null) : Exception(detail)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string? OperationId { get; set; } = operationId;
}

internal sealed record P2B4WriteResult(string OperationId, string State, string? EntityId, string? Code, string? Detail);

internal sealed record P2B4AddressWriteRequest(
    string? IdempotencyKey,
    string NetworkDomainId,
    string AddressFamily,
    string NormalizedAddress,
    int PrefixLength,
    string AddressRole,
    string SourceKind,
    string SourceReference,
    string? InterfaceReference = null,
    string? GatewayAddress = null,
    string? VrfName = null,
    long? ValidFromMs = null,
    long? ValidToMs = null,
    long? ExpectedRevision = null);

internal sealed record P2B4MembershipWriteRequest(
    string? IdempotencyKey,
    string NetworkDomainId,
    string MembershipRole,
    string SourceKind,
    string SourceReference,
    string? SourceFingerprint = null,
    long? ValidFromMs = null,
    long? ValidToMs = null);

internal sealed record P2B4VlanPlanWriteRequest(
    string? IdempotencyKey,
    string NetworkDomainId,
    int VlanId,
    string? Name,
    string? Purpose,
    string TaggingMode,
    int? NativeVlanId,
    int? Pvid,
    int? QinQOuterId,
    int? QinQInnerId,
    string SourceKind,
    string SourceReference,
    string? SourceFingerprint = null,
    string? ScopeResolution = null,
    long? ValidFromMs = null,
    long? ValidToMs = null,
    long? ExpectedRevision = null);

internal sealed record P2B4ReviewRequest(
    string? IdempotencyKey,
    string TargetType,
    string TargetId,
    string SourceKind,
    string SourceReference,
    long TargetRevision,
    string Decision,
    string Reason);

internal sealed record P2B4ImportRequest(string? IdempotencyKey, string Csv, long? ExpiresMs = null);

internal sealed record P2B4ImportStatus(
    string ImportId,
    string OperationId,
    string State,
    int RowCount,
    int ValidCount,
    IReadOnlyList<string> Errors,
    long CreatedMs,
    long UpdatedMs);

internal sealed record P2B4HistoryRead(
    string TargetId,
    long Revision,
    string SnapshotJson,
    long ArchivedMs);

internal sealed record P2B4ReviewRead(
    string Id,
    string Actor,
    string TargetType,
    string TargetId,
    string SourceKind,
    string SourceReference,
    long TargetRevision,
    string Decision,
    string Reason,
    string ConflictState,
    long CreatedMs);

internal static class P2B4WriteFeatureGate
{
    public const string WriteEnvironmentVariable = "NETCONSOLE_NEXT_P2B_WRITE_ENABLED";
    public const string TestAuthEnvironmentVariable = "NETCONSOLE_NEXT_P2B4_TEST_AUTH";
    public const string MigrationAuthorizedEnvironmentVariable = "NETCONSOLE_NEXT_P2B4_MIGRATION_AUTHORIZED";

    public static bool EnvironmentEnabled() =>
        P2BFeatureGate.FromEnvironment().Enabled &&
        string.Equals(Environment.GetEnvironmentVariable(WriteEnvironmentVariable), "1", StringComparison.Ordinal) &&
        string.Equals(Environment.GetEnvironmentVariable(TestAuthEnvironmentVariable), "1", StringComparison.Ordinal) &&
        string.Equals(Environment.GetEnvironmentVariable(MigrationAuthorizedEnvironmentVariable), "1", StringComparison.Ordinal);
}

internal sealed class P2B4WriteRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RuntimeSettings _settings;
    private string DatabasePath => Path.Combine(_settings.DataRoot, "catalog.db");

    public P2B4WriteRepository(RuntimeSettings settings) => _settings = settings;

    public bool IsAuthorityAvailable()
    {
        if (!P2B4WriteFeatureGate.EnvironmentEnabled() || IsReservedRoot(_settings.DataRoot) || !IsTemporaryRoot(_settings.DataRoot) || !File.Exists(DatabasePath)) return false;
        try
        {
            using var connection = OpenWriteConnection();
            P2B4SchemaV14Migration.ValidateV14Schema(connection, _settings.DataRoot);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or SqliteException or IOException)
        {
            return false;
        }
    }

    public P2B4WriteResult CreateAddress(string actor, string deviceId, P2B4AddressWriteRequest request) =>
        ExecuteOperation(actor, "ADDRESS_CREATE", request.IdempotencyKey, request, (connection, transaction, operationId) =>
        {
            ValidateText(deviceId, "deviceId");
            ValidateAddressRequest(connection, transaction, deviceId, request, allowExpectedRevision: false);
            var addressId = Guid.NewGuid().ToString("N");
            var now = Now();
            InsertAddress(connection, transaction, addressId, deviceId, request, now, 1, "NONE", "ACTIVE");
            InsertClaim(connection, transaction, addressId, deviceId, request, now, "ACTIVE", "NONE");
            AddItem(connection, transaction, operationId, 0, "ADDRESS", addressId, null, "{}", Json(request), "CREATED", "");
            return new P2B4WriteResult(operationId, "SUCCEEDED", addressId, null, null);
        });

    public P2B4WriteResult UpdateAddress(string actor, string deviceId, string addressId, P2B4AddressWriteRequest request) =>
        ExecuteOperation(actor, "ADDRESS_UPDATE", request.IdempotencyKey, request, (connection, transaction, operationId) =>
        {
            ValidateText(deviceId, "deviceId");
            ValidateText(addressId, "addressId");
            if (request.ExpectedRevision is null) throw new P2B4WriteException("EXPECTED_REVISION_REQUIRED", "Address updates require expectedRevision.", 400);
            var current = ReadAddress(connection, transaction, addressId, deviceId);
            if (current is null) throw new P2B4WriteException("ADDRESS_NOT_FOUND", "The address does not exist.", 404);
            if (current.Value.Revision != request.ExpectedRevision) throw new P2B4WriteException("STALE_REVISION", "The address changed after the preview.");
            if (current.Value.SourceKind == "OBSERVED") throw new P2B4WriteException("OBSERVED_PROTECTED", "Observed address facts cannot be overwritten.");
            if (!string.Equals(request.SourceKind, current.Value.SourceKind, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.SourceReference, current.Value.SourceReference, StringComparison.Ordinal))
                throw new P2B4WriteException("SOURCE_IDENTITY_IMMUTABLE", "An update cannot replace the source fact identity.", 400);
            ValidateAddressRequest(connection, transaction, deviceId, request, allowExpectedRevision: true, excludeAddressId: addressId);

            var now = Now();
            AddAddressHistory(connection, transaction, current.Value.Id, current.Value.Revision, current.Value.Snapshot, now);
            UpdateAddressRow(connection, transaction, addressId, request, current.Value.Revision + 1, now);
            UpdateClaim(connection, transaction, addressId, request, now, "ACTIVE", "NONE");
            AddItem(connection, transaction, operationId, 0, "ADDRESS", addressId, current.Value.Revision, current.Value.Snapshot, Json(request), "UPDATED", "");
            return new P2B4WriteResult(operationId, "SUCCEEDED", addressId, null, null);
        });

    public P2B4WriteResult CreateMembership(string actor, string deviceId, P2B4MembershipWriteRequest request) =>
        ExecuteOperation(actor, "MEMBERSHIP_CREATE", request.IdempotencyKey, request, (connection, transaction, operationId) =>
        {
            ValidateDeviceAndDomain(connection, transaction, deviceId, request.NetworkDomainId);
            ValidateInterval(request.ValidFromMs, request.ValidToMs);
            ValidateEnum(request.MembershipRole, "membershipRole", "PRIMARY", "SECONDARY", "SHARED", "OTHER");
            ValidateSource(request.SourceKind, request.SourceReference);
            var id = Guid.NewGuid().ToString("N");
            var now = Now();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO device_domain_memberships
                (id,device_id,network_domain_id,membership_role,source_kind,source_reference,source_fingerprint,revision,lifecycle,valid_from_ms,valid_to_ms,created_ms,updated_ms)
                VALUES($id,$device,$domain,$role,$sourceKind,$sourceRef,$fingerprint,1,'ACTIVE',$from,$to,$now,$now)";
            Add(command, "$id", id); Add(command, "$device", deviceId); Add(command, "$domain", request.NetworkDomainId);
            Add(command, "$role", request.MembershipRole.ToUpperInvariant()); Add(command, "$sourceKind", request.SourceKind.ToUpperInvariant());
            Add(command, "$sourceRef", request.SourceReference); Add(command, "$fingerprint", request.SourceFingerprint ?? Fingerprint(request.SourceKind + "|" + request.SourceReference));
            Add(command, "$from", request.ValidFromMs); Add(command, "$to", request.ValidToMs); Add(command, "$now", now); command.ExecuteNonQuery();
            AddItem(connection, transaction, operationId, 0, "MEMBERSHIP", id, null, "{}", Json(request), "CREATED", "");
            return new P2B4WriteResult(operationId, "SUCCEEDED", id, null, null);
        });

    public P2B4WriteResult CreateVlanPlan(string actor, P2B4VlanPlanWriteRequest request) =>
        ExecuteOperation(actor, "VLAN_PLAN_CREATE", request.IdempotencyKey, request, (connection, transaction, operationId) =>
        {
            ValidateVlanRequest(connection, transaction, request, allowExpectedRevision: false);
            var id = Guid.NewGuid().ToString("N");
            var now = Now();
            InsertVlan(connection, transaction, id, request, now, 1, request.ScopeResolution ?? "RESOLVED", "ACTIVE");
            AddItem(connection, transaction, operationId, 0, "VLAN_PLAN", id, null, "{}", Json(request), "CREATED", "");
            return new P2B4WriteResult(operationId, "SUCCEEDED", id, null, null);
        });

    public P2B4WriteResult UpdateVlanPlan(string actor, string planId, P2B4VlanPlanWriteRequest request) =>
        ExecuteOperation(actor, "VLAN_PLAN_UPDATE", request.IdempotencyKey, request, (connection, transaction, operationId) =>
        {
            if (request.ExpectedRevision is null) throw new P2B4WriteException("EXPECTED_REVISION_REQUIRED", "VLAN plan updates require expectedRevision.", 400);
            var current = ReadVlan(connection, transaction, planId);
            if (current is null) throw new P2B4WriteException("VLAN_PLAN_NOT_FOUND", "The VLAN plan does not exist.", 404);
            if (current.Value.Revision != request.ExpectedRevision) throw new P2B4WriteException("STALE_REVISION", "The VLAN plan changed after the preview.");
            if (current.Value.SourceKind == "OBSERVED") throw new P2B4WriteException("OBSERVED_PROTECTED", "Observed VLAN facts cannot be overwritten.");
            if (!string.Equals(current.Value.SourceKind, request.SourceKind, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.Value.SourceReference, request.SourceReference, StringComparison.Ordinal))
                throw new P2B4WriteException("SOURCE_IDENTITY_IMMUTABLE", "An update cannot replace the source fact identity.", 400);
            if (!string.Equals(current.Value.NetworkDomainId, request.NetworkDomainId, StringComparison.Ordinal) || current.Value.VlanId != request.VlanId)
                throw new P2B4WriteException("PLAN_IDENTITY_IMMUTABLE", "An update cannot move a VLAN plan to another domain or VLAN.", 400);
            ValidateVlanRequest(connection, transaction, request, allowExpectedRevision: true, excludePlanId: planId);
            var now = Now();
            AddVlanHistory(connection, transaction, planId, current.Value.Revision, current.Value.Snapshot, now);
            UpdateVlan(connection, transaction, planId, request, current.Value.Revision + 1, now);
            AddItem(connection, transaction, operationId, 0, "VLAN_PLAN", planId, current.Value.Revision, current.Value.Snapshot, Json(request), "UPDATED", "");
            return new P2B4WriteResult(operationId, "SUCCEEDED", planId, null, null);
        });

    public P2B4WriteResult CreateReview(string actor, P2B4ReviewRequest request) =>
        ExecuteOperation(actor, "NETWORK_REVIEW", request.IdempotencyKey, request, (connection, transaction, operationId) =>
        {
            ValidateReview(connection, transaction, request);
            var id = Guid.NewGuid().ToString("N");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO network_reviews
                (id,actor,target_type,target_id,source_kind,source_reference,target_revision,decision,reason,conflict_state,created_ms)
                VALUES($id,$actor,$type,$target,$sourceKind,$sourceRef,$revision,$decision,$reason,'REVIEW_RECORDED',$now)";
            Add(command, "$id", id); Add(command, "$actor", actor); Add(command, "$type", request.TargetType.ToUpperInvariant());
            Add(command, "$target", request.TargetId); Add(command, "$sourceKind", request.SourceKind.ToUpperInvariant()); Add(command, "$sourceRef", request.SourceReference);
            Add(command, "$revision", request.TargetRevision); Add(command, "$decision", request.Decision.ToUpperInvariant()); Add(command, "$reason", request.Reason.Trim()); Add(command, "$now", Now()); command.ExecuteNonQuery();
            AddItem(connection, transaction, operationId, 0, request.TargetType.ToUpperInvariant(), request.TargetId, request.TargetRevision, "{}", Json(request), "CREATED", "source facts unchanged");
            return new P2B4WriteResult(operationId, "SUCCEEDED", id, null, null);
        });

    public P2B4ImportStatus CreateImport(string actor, P2B4ImportRequest request)
    {
        EnsureWriteBoundary();
        var key = RequireIdempotency(request.IdempotencyKey);
        var csv = request.Csv ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(csv) > 2 * 1024 * 1024) throw new P2B4WriteException("IMPORT_TOO_LARGE", "Import payload exceeds 2 MiB.", 400);
        using var connection = OpenWriteConnection();
        var fingerprint = Fingerprint(csv);
        var existing = FindImport(connection, actor, key);
        if (existing is not null)
        {
            if (!string.Equals(existing.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new P2B4WriteException("IDEMPOTENCY_KEY_REUSE", "The idempotency key was already used with a different import payload.");
            return ReadImportStatus(connection, existing.Value.Id);
        }

        var parsed = ParseImport(csv, connection);
        var now = Now();
        var importId = Guid.NewGuid().ToString("N");
        var operationId = Guid.NewGuid().ToString("N");
        var expires = request.ExpiresMs is > 0 ? request.ExpiresMs.Value : now + 15 * 60 * 1000;
        if (expires <= now || expires > now + 24 * 60 * 60 * 1000) throw new P2B4WriteException("INVALID_PREVIEW_EXPIRY", "Preview expiry must be within 24 hours.", 400);
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            InsertOperation(connection, transaction, operationId, actor, "IMPORT", key, Json(request), "PENDING_CONFIRM", now, expires);
            using (var item = connection.CreateCommand())
            {
                item.Transaction = transaction;
                item.CommandText = "INSERT INTO p2b4_operation_items(operation_id,item_index,entity_type,target_id,expected_revision,requested_json,outcome,detail) VALUES($op,0,'IMPORT',$target,NULL,$requested,'PENDING',$detail)";
                Add(item, "$op", operationId); Add(item, "$target", importId); Add(item, "$requested", Json(new { rowCount = parsed.RowCount })); Add(item, "$detail", string.Join("; ", parsed.Errors)); item.ExecuteNonQuery();
            }
            using (var import = connection.CreateCommand())
            {
                import.Transaction = transaction;
                import.CommandText = @"INSERT INTO network_import_runs
                    (id,actor,idempotency_key,payload_fingerprint,state,request_json,preview_json,operation_id,created_ms,updated_ms,expires_ms)
                    VALUES($id,$actor,$key,$fingerprint,'PREVIEWED',$request,$preview,$op,$now,$now,$expires)";
                Add(import, "$id", importId); Add(import, "$actor", actor); Add(import, "$key", key); Add(import, "$fingerprint", fingerprint);
                Add(import, "$request", Json(request)); Add(import, "$preview", Json(parsed)); Add(import, "$op", operationId); Add(import, "$now", now); Add(import, "$expires", expires); import.ExecuteNonQuery();
            }
            Audit(connection, transaction, actor, "P2B4_IMPORT_PREVIEW", "SUCCEEDED", new { operationId, importId, rowCount = parsed.RowCount, validCount = parsed.ValidCount, errorCount = parsed.Errors.Count });
            transaction.Commit();
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
        return ReadImportStatus(connection, importId);
    }

    public P2B4ImportStatus GetImport(string actor, string importId) { EnsureWriteBoundary(); using var connection = OpenWriteConnection(); return ReadImportStatus(connection, importId, actor); }

    public P2B4ImportStatus DryRunImport(string actor, string importId) => GetImport(actor, importId);

    public P2B4ImportStatus CancelImport(string actor, string importId) => ChangeImportState(actor, importId, "CANCELLED");

    public P2B4ImportStatus ConfirmImport(string actor, string importId)
    {
        EnsureWriteBoundary();
        using var connection = OpenWriteConnection();
        var status = ReadImportStatus(connection, importId, actor);
        if (status.State == "SUCCEEDED") return status;
        if (status.State == "CANCELLED") throw new P2B4WriteException("IMPORT_CANCELLED", "The import was cancelled.", 409);
        if (status.State != "PREVIEWED" && status.State != "PENDING_CONFIRM") throw new P2B4WriteException("IMPORT_NOT_CONFIRMABLE", "The import is not awaiting confirmation.", 409);
        var row = ReadImportRow(connection, importId, actor);
        if (row.ExpiresMs < Now()) throw new P2B4WriteException("PREVIEW_EXPIRED", "The import preview has expired.", 409);
        var request = JsonSerializer.Deserialize<P2B4ImportRequest>(row.RequestJson, JsonOptions) ?? throw new P2B4WriteException("IMPORT_INVALID", "The stored import request is invalid.", 500);
        var parsed = ParseImport(request.Csv, connection);
        if (parsed.Errors.Count > 0) throw new P2B4WriteException("IMPORT_VALIDATION_FAILED", "The import preview contains validation errors; no rows were written.", 409);
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            SetOperationState(connection, transaction, row.OperationId, "RUNNING", "{}", Now(), null);
            // Item 0 is reserved for the preview summary created by CreateImport.
            var index = 1;
            foreach (var importRow in parsed.Rows) ApplyImportRow(connection, transaction, importRow, actor, row.OperationId, index++);
            var now = Now();
            SetOperationState(connection, transaction, row.OperationId, "SUCCEEDED", Json(new { rowCount = parsed.RowCount, committed = parsed.ValidCount }), now, now);
            SetImportState(connection, transaction, importId, "SUCCEEDED", now);
            Audit(connection, transaction, actor, "P2B4_IMPORT_COMMIT", "SUCCEEDED", new { operationId = row.OperationId, importId, rowCount = parsed.RowCount });
            transaction.Commit();
        }
        catch (P2B4WriteException exception)
        {
            try { transaction.Rollback(); } catch { }
            using var failure = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var now = Now();
            SetOperationState(connection, failure, row.OperationId, "CONFLICT", Json(new { code = exception.Code, detail = exception.Message }), now, now);
            SetImportState(connection, failure, importId, "CONFLICT", now);
            Audit(connection, failure, actor, "P2B4_IMPORT_COMMIT", "CONFLICT", new { operationId = row.OperationId, importId, code = exception.Code });
            failure.Commit();
            exception.OperationId = row.OperationId;
            throw;
        }
        return ReadImportStatus(connection, importId, actor);
    }

    public object? GetOperation(string actor, string operationId)
    {
        EnsureWriteBoundary();
        using var connection = OpenWriteConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,actor,kind,state,request_json,progress_json,created_ms,updated_ms,started_ms,completed_ms FROM p2b4_operations WHERE id=$id AND actor=$actor";
        Add(command, "$id", operationId); Add(command, "$actor", actor);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new
        {
            id = reader.GetString(0), actor = reader.GetString(1), kind = reader.GetString(2), state = reader.GetString(3),
            request = JsonDocument.Parse(reader.GetString(4)).RootElement, progress = JsonDocument.Parse(reader.GetString(5)).RootElement,
            createdMs = reader.GetInt64(6), updatedMs = reader.GetInt64(7), startedMs = NullableLong(reader, 8), completedMs = NullableLong(reader, 9)
        };
    }

    public IReadOnlyList<object> ListOperations(string actor, int limit)
    {
        EnsureWriteBoundary();
        using var connection = OpenWriteConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,actor,kind,state,request_json,progress_json,created_ms,updated_ms,started_ms,completed_ms FROM p2b4_operations WHERE actor=$actor ORDER BY updated_ms DESC,id DESC LIMIT $limit";
        Add(command, "$actor", actor); Add(command, "$limit", Math.Clamp(limit, 1, 100));
        var rows = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new
            {
                id = reader.GetString(0), actor = reader.GetString(1), kind = reader.GetString(2), state = reader.GetString(3),
                // Do not expose request_json here: import operations can contain raw CSV payloads.
                progress = JsonDocument.Parse(reader.GetString(5)).RootElement,
                createdMs = reader.GetInt64(6), updatedMs = reader.GetInt64(7), startedMs = NullableLong(reader, 8), completedMs = NullableLong(reader, 9)
            });
        }
        return rows;
    }

    public IReadOnlyList<P2B4HistoryRead> ReadAddressHistory(string actor, string deviceId, string addressId)
    {
        EnsureWriteBoundary();
        using var connection = OpenWriteConnection();
        using (var target = connection.CreateCommand())
        {
            target.CommandText = "SELECT 1 FROM device_addresses WHERE id=$id AND device_id=$device";
            Add(target, "$id", addressId); Add(target, "$device", deviceId);
            if (target.ExecuteScalar() is null) throw new P2B4WriteException("ADDRESS_NOT_FOUND", "The address does not exist.", 404);
        }
        return ReadHistory(connection, "p2b4_address_history", "address_id", addressId);
    }

    public IReadOnlyList<P2B4HistoryRead> ReadVlanHistory(string actor, string planId)
    {
        EnsureWriteBoundary();
        using var connection = OpenWriteConnection();
        return ReadHistory(connection, "p2b4_vlan_history", "plan_id", planId);
    }

    public IReadOnlyList<P2B4ReviewRead> ListReviews(string actor, string? targetType, string? targetId, int limit)
    {
        EnsureWriteBoundary();
        using var connection = OpenWriteConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,actor,target_type,target_id,source_kind,source_reference,target_revision,decision,reason,conflict_state,created_ms FROM network_reviews WHERE actor=$actor AND ($type IS NULL OR target_type=$type) AND ($target IS NULL OR target_id=$target) ORDER BY created_ms DESC,id DESC LIMIT $limit";
        Add(command, "$actor", actor);
        Add(command, "$type", string.IsNullOrWhiteSpace(targetType) ? null : targetType.Trim().ToUpperInvariant());
        Add(command, "$target", string.IsNullOrWhiteSpace(targetId) ? null : targetId.Trim()); Add(command, "$limit", Math.Clamp(limit, 1, 100));
        var rows = new List<P2B4ReviewRead>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetInt64(10)));
        return rows;
    }

    private static IReadOnlyList<P2B4HistoryRead> ReadHistory(SqliteConnection connection, string table, string keyColumn, string targetId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {keyColumn},revision,snapshot_json,archived_ms FROM {table} WHERE {keyColumn}=$id ORDER BY revision DESC";
        Add(command, "$id", targetId);
        var rows = new List<P2B4HistoryRead>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3)));
        return rows;
    }

    public void RecoverInterruptedOperations()
    {
        if (!P2B4WriteFeatureGate.EnvironmentEnabled() || !File.Exists(DatabasePath)) return;
        using var connection = OpenWriteConnection();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,actor FROM p2b4_operations WHERE state IN ('QUEUED','RUNNING')";
        var rows = new List<(string Id, string Actor)>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        foreach (var row in rows)
        {
            SetOperationState(connection, transaction, row.Id, "INTERRUPTED", Json(new { code = "PROCESS_RESTART" }), Now(), Now());
            Audit(connection, transaction, row.Actor, "P2B4_OPERATION_RECOVERY", "INTERRUPTED", new { operationId = row.Id, reason = "PROCESS_RESTART" });
        }
        transaction.Commit();
    }

    private P2B4WriteResult ExecuteOperation<T>(string actor, string kind, string? rawKey, T request,
        Func<SqliteConnection, SqliteTransaction, string, P2B4WriteResult> body)
    {
        EnsureWriteBoundary();
        var key = RequireIdempotency(rawKey);
        using var connection = OpenWriteConnection();
        var requestJson = Json(request);
        var existing = FindOperation(connection, actor, key);
        if (existing is not null)
        {
            if (!string.Equals(Fingerprint(existing.Value.RequestJson), Fingerprint(requestJson), StringComparison.Ordinal))
                throw new P2B4WriteException("IDEMPOTENCY_KEY_REUSE", "The idempotency key was already used with a different request.");
            return ReadStoredResult(existing.Value.Id, existing.Value.State, existing.Value.ProgressJson);
        }
        var operationId = Guid.NewGuid().ToString("N");
        var now = Now();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            InsertOperation(connection, transaction, operationId, actor, kind, key, requestJson, "RUNNING", now, null);
            var result = body(connection, transaction, operationId);
            SetOperationState(connection, transaction, operationId, "SUCCEEDED", Json(result), now, Now());
            Audit(connection, transaction, actor, "P2B4_" + kind, "SUCCEEDED", new { operationId, entityId = result.EntityId });
            transaction.Commit();
            return result;
        }
        catch (P2B4WriteException exception)
        {
            var rolledBack = false;
            try { transaction.Rollback(); rolledBack = true; } catch { }
            if (rolledBack)
            {
                using var failure = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
                var state = exception.StatusCode == 409 ? "CONFLICT" : "FAILED";
                SetOperationState(connection, failure, operationId, state, Json(new { code = exception.Code, detail = exception.Message }), now, Now());
                Audit(connection, failure, actor, "P2B4_" + kind, state, new { operationId, code = exception.Code });
                failure.Commit();
            }
            exception.OperationId = operationId;
            throw;
        }
        catch (SqliteException)
        {
            try { transaction.Rollback(); } catch { }
            try
            {
                using var failure = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
                SetOperationState(connection, failure, operationId, "CONFLICT", Json(new { code = "WRITE_CONSTRAINT" }), now, Now());
                Audit(connection, failure, actor, "P2B4_" + kind, "CONFLICT", new { operationId, code = "WRITE_CONSTRAINT" });
                failure.Commit();
            }
            catch { }
            throw new P2B4WriteException("WRITE_CONSTRAINT", "The isolated write conflicts with an existing source or constraint.", 409, operationId);
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
    }

    private void EnsureWriteBoundary()
    {
        if (!P2B4WriteFeatureGate.EnvironmentEnabled()) throw new P2B4WriteException("FEATURE_WRITE_DISABLED", "P2B-4 write capability is disabled.", 503);
        if (IsReservedRoot(_settings.DataRoot) || !IsTemporaryRoot(_settings.DataRoot)) throw new P2B4WriteException("ISOLATED_AUTHORITY_REQUIRED", "P2B-4 writes are restricted to a temporary isolated data root.", 503);
        if (!File.Exists(DatabasePath)) throw new P2B4WriteException("P2B4_SCHEMA_UNAVAILABLE", "The isolated v14 database is not available.", 503);
    }

    private SqliteConnection OpenWriteConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
            P2B4SchemaV14Migration.ValidateV14Schema(connection, _settings.DataRoot);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static bool IsReservedRoot(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, Path.GetFullPath(@"D:\NetConsoleData").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full, Path.GetFullPath(@"D:\NetConsoleData-dev").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTemporaryRoot(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, temp, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateAddressRequest(SqliteConnection connection, SqliteTransaction transaction, string deviceId,
        P2B4AddressWriteRequest request, bool allowExpectedRevision, string? excludeAddressId = null)
    {
        ValidateDeviceAndDomain(connection, transaction, deviceId, request.NetworkDomainId);
        ValidateEnum(request.AddressFamily, "addressFamily", "IPV4", "IPV6");
        ValidateEnum(request.AddressRole, "addressRole", "MANAGEMENT", "SERVICE", "OUT_OF_BAND", "LOOPBACK", "OTHER");
        ValidateSource(request.SourceKind, request.SourceReference);
        ValidateInterval(request.ValidFromMs, request.ValidToMs);
        if (!IPAddress.TryParse(request.NormalizedAddress?.Trim(), out var address)) throw new P2B4WriteException("INVALID_ADDRESS", "normalizedAddress must be an IP address.", 400);
        var normalized = address.ToString();
        var expectedFamily = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "IPV4" : "IPV6";
        if (!string.Equals(expectedFamily, request.AddressFamily, StringComparison.OrdinalIgnoreCase)) throw new P2B4WriteException("ADDRESS_FAMILY_MISMATCH", "addressFamily does not match normalizedAddress.", 400);
        if ((expectedFamily == "IPV4" && request.PrefixLength is < 0 or > 32) || (expectedFamily == "IPV6" && request.PrefixLength is < 0 or > 128)) throw new P2B4WriteException("INVALID_PREFIX", "prefixLength is outside the family range.", 400);
        if (request.ExpectedRevision is not null && !allowExpectedRevision) throw new P2B4WriteException("UNEXPECTED_REVISION", "Create requests cannot include expectedRevision.", 400);
        if (request.ValidFromMs is null && request.ValidToMs is null) { }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT da.id FROM device_address_claims c JOIN device_addresses da ON da.id=c.device_address_id
            WHERE c.network_domain_id=$domain AND c.address_family=$family AND c.normalized_address=$address
              AND c.claim_state='ACTIVE' AND c.lifecycle='ACTIVE' AND c.device_id<>$device
              AND (c.valid_from_ms IS NULL OR $to IS NULL OR c.valid_from_ms<$to)
              AND ($from IS NULL OR c.valid_to_ms IS NULL OR $from<c.valid_to_ms)" +
            (excludeAddressId is null ? string.Empty : " AND da.id<>$exclude");
        Add(command, "$domain", request.NetworkDomainId); Add(command, "$family", expectedFamily); Add(command, "$address", normalized); Add(command, "$device", deviceId);
        Add(command, "$from", request.ValidFromMs); Add(command, "$to", request.ValidToMs); if (excludeAddressId is not null) Add(command, "$exclude", excludeAddressId);
        if (command.ExecuteScalar() is not null) throw new P2B4WriteException("ADDRESS_ALREADY_CLAIMED", "The address is already claimed by another device in the same domain and overlapping validity.");
    }

    private static void ValidateVlanRequest(SqliteConnection connection, SqliteTransaction transaction, P2B4VlanPlanWriteRequest request, bool allowExpectedRevision, string? excludePlanId = null)
    {
        ValidateDeviceDomainOnly(connection, transaction, request.NetworkDomainId);
        if (request.VlanId is < 1 or > 4094) throw new P2B4WriteException("INVALID_VLAN_ID", "vlanId must be between 1 and 4094.", 400);
        ValidateEnum(request.TaggingMode, "taggingMode", "ACCESS", "TRUNK", "HYBRID", "QINQ");
        ValidateSource(request.SourceKind, request.SourceReference);
        ValidateInterval(request.ValidFromMs, request.ValidToMs);
        if (request.ScopeResolution is not null && !string.Equals(request.ScopeResolution, "RESOLVED", StringComparison.OrdinalIgnoreCase)) throw new P2B4WriteException("DOMAIN_NOT_RESOLVED", "Writes require a resolved domain scope.");
        if (request.TaggingMode.Equals("ACCESS", StringComparison.OrdinalIgnoreCase) && (request.Pvid != request.VlanId || request.QinQOuterId is not null || request.QinQInnerId is not null)) throw new P2B4WriteException("INVALID_ACCESS_TAGGING", "ACCESS requires pvid=vlanId and no QinQ fields.", 400);
        if (request.TaggingMode.Equals("QINQ", StringComparison.OrdinalIgnoreCase) && (request.QinQOuterId != request.VlanId || request.QinQInnerId is null || request.NativeVlanId is not null || request.Pvid is not null)) throw new P2B4WriteException("INVALID_QINQ_TAGGING", "QINQ requires outer=vlanId, inner, and no native/pvid fields.", 400);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT id FROM vlan_plans
            WHERE network_domain_id=$domain AND vlan_id=$vlan AND lifecycle='ACTIVE' AND source_kind<>'OBSERVED'
              AND (valid_from_ms IS NULL OR $to IS NULL OR valid_from_ms<$to)
              AND ($from IS NULL OR valid_to_ms IS NULL OR $from<valid_to_ms)" + (excludePlanId is null ? string.Empty : " AND id<>$exclude");
        Add(command, "$domain", request.NetworkDomainId); Add(command, "$vlan", request.VlanId); Add(command, "$from", request.ValidFromMs); Add(command, "$to", request.ValidToMs); if (excludePlanId is not null) Add(command, "$exclude", excludePlanId);
        if (command.ExecuteScalar() is not null && !request.SourceKind.Equals("OBSERVED", StringComparison.OrdinalIgnoreCase)) throw new P2B4WriteException("VLAN_OVERLAP", "A non-observed VLAN plan overlaps in the same domain and validity interval.");
    }

    private static void ValidateReview(SqliteConnection connection, SqliteTransaction transaction, P2B4ReviewRequest request)
    {
        ValidateEnum(request.TargetType, "targetType", "ADDRESS", "MEMBERSHIP", "DOMAIN", "VLAN_PLAN");
        ValidateEnum(request.Decision, "decision", "ACCEPT_PLANNED", "ACCEPT_OBSERVED", "REJECT", "DEFER");
        ValidateSource(request.SourceKind, request.SourceReference);
        if (request.TargetRevision <= 0 || string.IsNullOrWhiteSpace(request.Reason)) throw new P2B4WriteException("INVALID_REVIEW", "A positive targetRevision and non-empty reason are required.", 400);
        var current = ReadTargetSource(connection, transaction, request.TargetType.ToUpperInvariant(), request.TargetId);
        if (current is null) throw new P2B4WriteException("REVIEW_TARGET_NOT_FOUND", "The review target does not exist.", 404);
        if (current.Value.Revision != request.TargetRevision || !string.Equals(current.Value.SourceKind, request.SourceKind, StringComparison.OrdinalIgnoreCase) || !string.Equals(current.Value.SourceReference, request.SourceReference, StringComparison.Ordinal))
            throw new P2B4WriteException("STALE_REVIEW", "The review target revision or source identity is stale.");
    }

    private static void ValidateDeviceAndDomain(SqliteConnection connection, SqliteTransaction transaction, string deviceId, string domainId)
    {
        ValidateText(domainId, "networkDomainId");
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT COUNT(*) FROM device_inventory WHERE id=$id"; Add(command, "$id", deviceId);
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) throw new P2B4WriteException("DEVICE_NOT_FOUND", "The device does not exist.", 404);
        ValidateDeviceDomainOnly(connection, transaction, domainId);
    }

    private static void ValidateDeviceDomainOnly(SqliteConnection connection, SqliteTransaction transaction, string domainId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT lifecycle,scope_resolution FROM network_domains WHERE id=$id"; Add(command, "$id", domainId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new P2B4WriteException("DOMAIN_NOT_FOUND", "The network domain does not exist.", 404);
        if (!string.Equals(reader.GetString(0), "ACTIVE", StringComparison.Ordinal) || !string.Equals(reader.GetString(1), "RESOLVED", StringComparison.Ordinal)) throw new P2B4WriteException("DOMAIN_NOT_RESOLVED", "Writes require an active, resolved network domain.");
    }

    private static void ValidateSource(string kind, string reference)
    {
        ValidateEnum(kind, "sourceKind", "PLANNED", "OBSERVED", "IMPORTED", "MANUAL"); ValidateText(reference, "sourceReference");
    }

    private static void ValidateEnum(string value, string name, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value) || !allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)) throw new P2B4WriteException("INVALID_" + name.ToUpperInvariant(), $"{name} is invalid.", 400);
    }

    private static void ValidateText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw new P2B4WriteException("INVALID_" + name.ToUpperInvariant(), $"{name} is required and bounded.", 400);
    }

    private static void ValidateInterval(long? from, long? to)
    {
        if (from is < 0 || to is < 0 || from is not null && to is not null && from >= to) throw new P2B4WriteException("INVALID_VALIDITY", "Validity intervals must be half-open and increasing.", 400);
    }

    private static void InsertAddress(SqliteConnection connection, SqliteTransaction transaction, string id, string deviceId, P2B4AddressWriteRequest request, long now, long revision, string conflict, string lifecycle)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = @"INSERT INTO device_addresses
            (id,device_id,network_domain_id,address_family,normalized_address,prefix_length,gateway_address,vrf_name,address_role,interface_reference,source_kind,source_reference,source_fingerprint,observed_at_ms,revision,domain_resolution_state,conflict_state,lifecycle,valid_from_ms,valid_to_ms,created_ms,updated_ms)
            VALUES($id,$device,$domain,$family,$address,$prefix,$gateway,$vrf,$role,$interface,$sourceKind,$sourceRef,$fingerprint,NULL,$revision,'RESOLVED',$conflict,$lifecycle,$from,$to,$now,$now)";
        Add(command, "$id", id); Add(command, "$device", deviceId); Add(command, "$domain", request.NetworkDomainId); Add(command, "$family", request.AddressFamily.ToUpperInvariant()); Add(command, "$address", IPAddress.Parse(request.NormalizedAddress.Trim()).ToString()); Add(command, "$prefix", request.PrefixLength); Add(command, "$gateway", request.GatewayAddress); Add(command, "$vrf", request.VrfName ?? string.Empty); Add(command, "$role", request.AddressRole.ToUpperInvariant()); Add(command, "$interface", request.InterfaceReference ?? string.Empty); Add(command, "$sourceKind", request.SourceKind.ToUpperInvariant()); Add(command, "$sourceRef", request.SourceReference); Add(command, "$fingerprint", Fingerprint(request.SourceKind + "|" + request.SourceReference)); Add(command, "$revision", revision); Add(command, "$conflict", conflict); Add(command, "$lifecycle", lifecycle); Add(command, "$from", request.ValidFromMs); Add(command, "$to", request.ValidToMs); Add(command, "$now", now); command.ExecuteNonQuery();
    }

    private static void InsertClaim(SqliteConnection connection, SqliteTransaction transaction, string addressId, string deviceId, P2B4AddressWriteRequest request, long now, string state, string conflict)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = @"INSERT INTO device_address_claims
            (id,device_address_id,device_id,network_domain_id,address_family,normalized_address,valid_from_ms,valid_to_ms,lifecycle,claim_state,conflict_code,created_ms,updated_ms)
            VALUES($id,$addressId,$device,$domain,$family,$address,$from,$to,'ACTIVE',$state,$conflict,$now,$now)";
        Add(command, "$id", Guid.NewGuid().ToString("N")); Add(command, "$addressId", addressId); Add(command, "$device", deviceId); Add(command, "$domain", request.NetworkDomainId); Add(command, "$family", request.AddressFamily.ToUpperInvariant()); Add(command, "$address", IPAddress.Parse(request.NormalizedAddress.Trim()).ToString()); Add(command, "$from", request.ValidFromMs); Add(command, "$to", request.ValidToMs); Add(command, "$state", state); Add(command, "$conflict", conflict); Add(command, "$now", now); command.ExecuteNonQuery();
    }

    private static void UpdateAddressRow(SqliteConnection connection, SqliteTransaction transaction, string id, P2B4AddressWriteRequest request, long revision, long now)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = @"UPDATE device_addresses SET network_domain_id=$domain,address_family=$family,normalized_address=$address,prefix_length=$prefix,gateway_address=$gateway,vrf_name=$vrf,address_role=$role,interface_reference=$interface,revision=$revision,valid_from_ms=$from,valid_to_ms=$to,updated_ms=$now,conflict_state='NONE',domain_resolution_state='RESOLVED' WHERE id=$id";
        Add(command, "$id", id); Add(command, "$domain", request.NetworkDomainId); Add(command, "$family", request.AddressFamily.ToUpperInvariant()); Add(command, "$address", IPAddress.Parse(request.NormalizedAddress.Trim()).ToString()); Add(command, "$prefix", request.PrefixLength); Add(command, "$gateway", request.GatewayAddress); Add(command, "$vrf", request.VrfName ?? string.Empty); Add(command, "$role", request.AddressRole.ToUpperInvariant()); Add(command, "$interface", request.InterfaceReference ?? string.Empty); Add(command, "$revision", revision); Add(command, "$from", request.ValidFromMs); Add(command, "$to", request.ValidToMs); Add(command, "$now", now); command.ExecuteNonQuery();
    }

    private static void UpdateClaim(SqliteConnection connection, SqliteTransaction transaction, string addressId, P2B4AddressWriteRequest request, long now, string state, string conflict)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE device_address_claims SET network_domain_id=$domain,address_family=$family,normalized_address=$address,valid_from_ms=$from,valid_to_ms=$to,claim_state=$state,conflict_code=$conflict,updated_ms=$now WHERE device_address_id=$id";
        Add(command, "$id", addressId); Add(command, "$domain", request.NetworkDomainId); Add(command, "$family", request.AddressFamily.ToUpperInvariant()); Add(command, "$address", IPAddress.Parse(request.NormalizedAddress.Trim()).ToString()); Add(command, "$from", request.ValidFromMs); Add(command, "$to", request.ValidToMs); Add(command, "$state", state); Add(command, "$conflict", conflict); Add(command, "$now", now); command.ExecuteNonQuery();
    }

    private static void InsertVlan(SqliteConnection connection, SqliteTransaction transaction, string id, P2B4VlanPlanWriteRequest request, long now, long revision, string scope, string lifecycle)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = @"INSERT INTO vlan_plans
            (id,network_domain_id,vlan_id,name,purpose,tagging_mode,native_vlan_id,pvid,qinq_outer_id,qinq_inner_id,scope_resolution,source_kind,source_reference,source_fingerprint,revision,lifecycle,valid_from_ms,valid_to_ms,created_ms,updated_ms)
            VALUES($id,$domain,$vlan,$name,$purpose,$tagging,$native,$pvid,$outer,$inner,$scope,$sourceKind,$sourceRef,$fingerprint,$revision,$lifecycle,$from,$to,$now,$now)";
        Add(command, "$id", id); Add(command, "$domain", request.NetworkDomainId); Add(command, "$vlan", request.VlanId); Add(command, "$name", request.Name ?? string.Empty); Add(command, "$purpose", request.Purpose ?? string.Empty); Add(command, "$tagging", request.TaggingMode.ToUpperInvariant()); Add(command, "$native", request.NativeVlanId); Add(command, "$pvid", request.Pvid); Add(command, "$outer", request.QinQOuterId); Add(command, "$inner", request.QinQInnerId); Add(command, "$scope", scope.ToUpperInvariant()); Add(command, "$sourceKind", request.SourceKind.ToUpperInvariant()); Add(command, "$sourceRef", request.SourceReference); Add(command, "$fingerprint", request.SourceFingerprint ?? Fingerprint(request.SourceKind + "|" + request.SourceReference)); Add(command, "$revision", revision); Add(command, "$lifecycle", lifecycle); Add(command, "$from", request.ValidFromMs); Add(command, "$to", request.ValidToMs); Add(command, "$now", now); command.ExecuteNonQuery();
    }

    private static void UpdateVlan(SqliteConnection connection, SqliteTransaction transaction, string id, P2B4VlanPlanWriteRequest request, long revision, long now)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = @"UPDATE vlan_plans SET name=$name,purpose=$purpose,tagging_mode=$tagging,native_vlan_id=$native,pvid=$pvid,qinq_outer_id=$outer,qinq_inner_id=$inner,scope_resolution='RESOLVED',revision=$revision,valid_from_ms=$from,valid_to_ms=$to,updated_ms=$now WHERE id=$id";
        Add(command, "$id", id); Add(command, "$name", request.Name ?? string.Empty); Add(command, "$purpose", request.Purpose ?? string.Empty); Add(command, "$tagging", request.TaggingMode.ToUpperInvariant()); Add(command, "$native", request.NativeVlanId); Add(command, "$pvid", request.Pvid); Add(command, "$outer", request.QinQOuterId); Add(command, "$inner", request.QinQInnerId); Add(command, "$revision", revision); Add(command, "$from", request.ValidFromMs); Add(command, "$to", request.ValidToMs); Add(command, "$now", now); command.ExecuteNonQuery();
    }

    private static void AddAddressHistory(SqliteConnection c, SqliteTransaction t, string id, long revision, string snapshot, long now)
    { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "INSERT INTO p2b4_address_history(address_id,revision,snapshot_json,archived_ms) VALUES($id,$revision,$snapshot,$now)"; Add(cmd,"$id",id); Add(cmd,"$revision",revision); Add(cmd,"$snapshot",snapshot); Add(cmd,"$now",now); cmd.ExecuteNonQuery(); }
    private static void AddVlanHistory(SqliteConnection c, SqliteTransaction t, string id, long revision, string snapshot, long now)
    { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "INSERT INTO p2b4_vlan_history(plan_id,revision,snapshot_json,archived_ms) VALUES($id,$revision,$snapshot,$now)"; Add(cmd,"$id",id); Add(cmd,"$revision",revision); Add(cmd,"$snapshot",snapshot); Add(cmd,"$now",now); cmd.ExecuteNonQuery(); }

    private static (string Id, long Revision, string SourceKind, string SourceReference, string NetworkDomainId, int VlanId, string Snapshot)? ReadVlan(SqliteConnection c, SqliteTransaction t, string id)
    { using var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText="SELECT id,revision,source_kind,source_reference,network_domain_id,vlan_id,json_object('id',id,'revision',revision,'name',name,'purpose',purpose,'taggingMode',tagging_mode) FROM vlan_plans WHERE id=$id AND lifecycle='ACTIVE'"; Add(cmd,"$id",id); using var r=cmd.ExecuteReader(); return r.Read() ? (r.GetString(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt32(5),r.GetString(6)) : null; }
    private static (string Id, long Revision, string SourceKind, string SourceReference, string Snapshot)? ReadAddress(SqliteConnection c, SqliteTransaction t, string id, string deviceId)
    { using var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText="SELECT id,revision,source_kind,source_reference,json_object('id',id,'revision',revision,'normalizedAddress',normalized_address,'prefixLength',prefix_length,'networkDomainId',network_domain_id) FROM device_addresses WHERE id=$id AND device_id=$device AND lifecycle='ACTIVE'"; Add(cmd,"$id",id); Add(cmd,"$device",deviceId); using var r=cmd.ExecuteReader(); return r.Read() ? (r.GetString(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetString(4)) : null; }
    private static (long Revision, string SourceKind, string SourceReference)? ReadTargetSource(SqliteConnection c, SqliteTransaction t, string type, string id)
    { var table = type switch { "ADDRESS" => "device_addresses", "MEMBERSHIP" => "device_domain_memberships", "DOMAIN" => "network_domains", "VLAN_PLAN" => "vlan_plans", _ => throw new P2B4WriteException("INVALID_REVIEW_TARGET", "Unknown review target.", 400) }; using var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText=$"SELECT revision,source_kind,source_reference FROM {table} WHERE id=$id AND lifecycle='ACTIVE'"; Add(cmd,"$id",id); using var r=cmd.ExecuteReader(); return r.Read() ? (r.GetInt64(0),r.GetString(1),r.GetString(2)) : null; }

    private static void ApplyImportRow(SqliteConnection c, SqliteTransaction t, P2B4ParsedRow row, string actor, string operationId, int index)
    {
        if (row.EntityType == "ADDRESS")
        {
            var req = new P2B4AddressWriteRequest(null, row.DomainId!, row.AddressFamily!, row.Address!, row.PrefixLength!.Value, row.AddressRole ?? "SERVICE", row.SourceKind!, row.SourceReference!, row.InterfaceReference, null, null, row.ValidFromMs, row.ValidToMs, row.ExpectedRevision);
            if (string.IsNullOrWhiteSpace(row.TargetId)) { ValidateAddressRequest(c,t,row.DeviceId!,req,false); var id=Guid.NewGuid().ToString("N"); var now=Now(); InsertAddress(c,t,id,row.DeviceId!,req,now,1,"NONE","ACTIVE"); InsertClaim(c,t,id,row.DeviceId!,req,now,"ACTIVE","NONE"); AddItem(c,t,operationId,index,"ADDRESS",id,null,"{}",Json(req),"CREATED",""); }
            else { var current=ReadAddress(c,t,row.TargetId!,row.DeviceId!); if(current is null) throw new P2B4WriteException("ADDRESS_NOT_FOUND","Import address target does not exist.",404); if(row.ExpectedRevision != current.Value.Revision) throw new P2B4WriteException("STALE_REVISION","Import address revision is stale."); if(current.Value.SourceKind=="OBSERVED") throw new P2B4WriteException("OBSERVED_PROTECTED","Observed address facts cannot be overwritten."); ValidateAddressRequest(c,t,row.DeviceId!,req,true,row.TargetId); var now=Now(); AddAddressHistory(c,t,current.Value.Id,current.Value.Revision,current.Value.Snapshot,now); UpdateAddressRow(c,t,row.TargetId!,req,current.Value.Revision+1,now); UpdateClaim(c,t,row.TargetId!,req,now,"ACTIVE","NONE"); AddItem(c,t,operationId,index,"ADDRESS",row.TargetId!,current.Value.Revision,current.Value.Snapshot,Json(req),"UPDATED",""); }
        }
        else if (row.EntityType == "MEMBERSHIP")
        {
            ValidateDeviceAndDomain(c, t, row.DeviceId!, row.DomainId!);
            ValidateEnum(row.MembershipRole!, "membershipRole", "PRIMARY", "SECONDARY", "SHARED", "OTHER");
            var id = Guid.NewGuid().ToString("N");
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = @"INSERT INTO device_domain_memberships
                (id,device_id,network_domain_id,membership_role,source_kind,source_reference,source_fingerprint,revision,lifecycle,valid_from_ms,valid_to_ms,created_ms,updated_ms)
                VALUES($id,$device,$domain,$role,$sourceKind,$sourceRef,$fingerprint,1,'ACTIVE',$from,$to,$now,$now)";
            Add(command, "$id", id); Add(command, "$device", row.DeviceId); Add(command, "$domain", row.DomainId); Add(command, "$role", row.MembershipRole); Add(command, "$sourceKind", row.SourceKind); Add(command, "$sourceRef", row.SourceReference); Add(command, "$fingerprint", Fingerprint(row.SourceKind + "|" + row.SourceReference)); Add(command, "$from", row.ValidFromMs); Add(command, "$to", row.ValidToMs); Add(command, "$now", Now()); command.ExecuteNonQuery();
            AddItem(c, t, operationId, index, "MEMBERSHIP", id, null, "{}", Json(row), "CREATED", "");
        }
        else if (row.EntityType == "VLAN_PLAN")
        {
            var req = new P2B4VlanPlanWriteRequest(null,row.DomainId!,row.VlanId!.Value,row.Name,row.Purpose,row.TaggingMode!,row.NativeVlanId,row.Pvid,row.QinQOuterId,row.QinQInnerId,row.SourceKind!,row.SourceReference!,null,"RESOLVED",row.ValidFromMs,row.ValidToMs,row.ExpectedRevision);
            if (string.IsNullOrWhiteSpace(row.TargetId)) { ValidateVlanRequest(c,t,req,false); var id=Guid.NewGuid().ToString("N"); InsertVlan(c,t,id,req,Now(),1,"RESOLVED","ACTIVE"); AddItem(c,t,operationId,index,"VLAN_PLAN",id,null,"{}",Json(req),"CREATED",""); }
            else { var current=ReadVlan(c,t,row.TargetId!); if(current is null) throw new P2B4WriteException("VLAN_PLAN_NOT_FOUND","Import VLAN plan target does not exist.",404); if(row.ExpectedRevision != current.Value.Revision) throw new P2B4WriteException("STALE_REVISION","Import VLAN plan revision is stale."); if(current.Value.SourceKind=="OBSERVED") throw new P2B4WriteException("OBSERVED_PROTECTED","Observed VLAN facts cannot be overwritten."); ValidateVlanRequest(c,t,req,true,row.TargetId); var now=Now(); AddVlanHistory(c,t,current.Value.Id,current.Value.Revision,current.Value.Snapshot,now); UpdateVlan(c,t,row.TargetId!,req,current.Value.Revision+1,now); AddItem(c,t,operationId,index,"VLAN_PLAN",row.TargetId!,current.Value.Revision,current.Value.Snapshot,Json(req),"UPDATED",""); }
        }
        else throw new P2B4WriteException("IMPORT_ENTITY_UNSUPPORTED", "Only ADDRESS, MEMBERSHIP and VLAN_PLAN import rows are supported by P2B-4.", 400);
    }

    private static P2B4ParsedImport ParseImport(string csv, SqliteConnection connection)
    {
        var rows = CsvRows(csv); if (rows.Count == 0) throw new P2B4WriteException("IMPORT_EMPTY", "Import CSV is empty.", 400); if (rows.Count > 10_001) throw new P2B4WriteException("IMPORT_TOO_MANY_ROWS", "Import CSV exceeds 10,000 data rows.", 400);
        var header = rows[0].Select((value,index)=>(value.Trim(),index)).ToDictionary(x=>x.Item1,StringComparer.OrdinalIgnoreCase); var required = new[]{"entityType","deviceId","networkDomainId","sourceKind","sourceReference"}; foreach(var name in required) if(!header.ContainsKey(name)) throw new P2B4WriteException("IMPORT_HEADER_INVALID", $"Missing CSV header: {name}.",400);
        var parsed = new List<P2B4ParsedRow>(); var errors = new List<string>();
        for(var i=1;i<rows.Count;i++) { var values=rows[i]; string Get(string n)=>header.TryGetValue(n,out var idx)&&idx.index<values.Count?values[idx.index].Trim():string.Empty; var row=new P2B4ParsedRow(i,Get("entityType").ToUpperInvariant(),Get("targetId"),Get("deviceId"),Get("networkDomainId"),Get("addressFamily").ToUpperInvariant(),Get("normalizedAddress"),Int(Get("prefixLength")),Get("addressRole").ToUpperInvariant(),Get("membershipRole").ToUpperInvariant(),Get("interfaceReference"),Get("sourceKind").ToUpperInvariant(),Get("sourceReference"),Int64(Get("expectedRevision")),Get("name"),Get("purpose"),Get("taggingMode").ToUpperInvariant(),Int(Get("nativeVlanId")),Int(Get("pvid")),Int(Get("qinqOuterId")),Int(Get("qinqInnerId")),Int(Get("vlanId")),Int64(Get("validFromMs")),Int64(Get("validToMs"))); try { ValidateImportRow(connection,row); parsed.Add(row); } catch(P2B4WriteException ex){ errors.Add($"row {i+1}: {ex.Code} {ex.Message}"); } }
        return new P2B4ParsedImport(rows.Count-1,parsed.Count,errors,parsed);
    }

    private static void ValidateImportRow(SqliteConnection c, P2B4ParsedRow row)
    { ValidateEnum(row.EntityType,"entityType","ADDRESS","MEMBERSHIP","VLAN_PLAN"); ValidateText(row.DeviceId!,"deviceId"); ValidateText(row.DomainId!,"networkDomainId"); ValidateSource(row.SourceKind!,row.SourceReference!); ValidateInterval(row.ValidFromMs,row.ValidToMs); if(row.EntityType=="ADDRESS"){if(row.AddressFamily is null||row.Address is null||row.PrefixLength is null)throw new P2B4WriteException("IMPORT_ADDRESS_INVALID","Address fields are required.",400); if(!IPAddress.TryParse(row.Address,out _))throw new P2B4WriteException("INVALID_ADDRESS","normalizedAddress must be an IP address.",400);} else if(row.EntityType=="MEMBERSHIP"){ValidateEnum(row.MembershipRole!,"membershipRole","PRIMARY","SECONDARY","SHARED","OTHER");} else {if(row.VlanId is null||row.TaggingMode is null)throw new P2B4WriteException("IMPORT_VLAN_INVALID","VLAN fields are required.",400); if(row.VlanId<1||row.VlanId>4094)throw new P2B4WriteException("INVALID_VLAN_ID","vlanId must be between 1 and 4094.",400);} }

    private static List<List<string>> CsvRows(string text)
    { var result=new List<List<string>>(); var row=new List<string>(); var cell=new StringBuilder(); var quoted=false; for(var i=0;i<text.Length;i++){var ch=text[i]; if(ch=='"'){if(quoted&&i+1<text.Length&&text[i+1]=='"'){cell.Append('"');i++;}else quoted=!quoted;}else if(ch==','&&!quoted){row.Add(cell.ToString());cell.Clear();}else if((ch=='\n'||ch=='\r')&&!quoted){if(ch=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;row.Add(cell.ToString());cell.Clear();if(row.Any(x=>x.Length>0))result.Add(row);row=new();}else cell.Append(ch);}if(quoted)throw new P2B4WriteException("IMPORT_CSV_INVALID","CSV contains an unterminated quote.",400);if(cell.Length>0||row.Count>0){row.Add(cell.ToString());result.Add(row);}return result; }

    private static (string Id,string Fingerprint)? FindImport(SqliteConnection c,string actor,string key){using var cmd=c.CreateCommand();cmd.CommandText="SELECT id,payload_fingerprint FROM network_import_runs WHERE actor=$actor AND idempotency_key=$key";Add(cmd,"$actor",actor);Add(cmd,"$key",key);using var r=cmd.ExecuteReader();return r.Read()?(r.GetString(0),r.GetString(1)):null;}
    private static (string Id,string State,string RequestJson,string ProgressJson)? FindOperation(SqliteConnection c,string actor,string key){using var cmd=c.CreateCommand();cmd.CommandText="SELECT id,state,request_json,progress_json FROM p2b4_operations WHERE actor=$actor AND idempotency_key=$key";Add(cmd,"$actor",actor);Add(cmd,"$key",key);using var r=cmd.ExecuteReader();return r.Read()?(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3)):null;}
    private static P2B4WriteResult ReadStoredResult(string id,string state,string progress){try{var result=JsonSerializer.Deserialize<P2B4WriteResult>(progress,JsonOptions);return result is null?new(id,state,null,null,null):result with{State=state};}catch{return new(id,state,null,null,null);}}
    private static void InsertOperation(SqliteConnection c,SqliteTransaction t,string id,string actor,string kind,string key,string request,string state,long now,long? expires){using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="INSERT INTO p2b4_operations(id,actor,kind,state,request_json,progress_json,created_ms,updated_ms,started_ms,idempotency_key,preview_expires_ms) VALUES($id,$actor,$kind,$state,$request,'{}',$now,$now,$now,$key,$expires)";Add(cmd,"$id",id);Add(cmd,"$actor",actor);Add(cmd,"$kind",kind);Add(cmd,"$state",state);Add(cmd,"$request",request);Add(cmd,"$now",now);Add(cmd,"$key",key);Add(cmd,"$expires",expires);cmd.ExecuteNonQuery();}
    private static void SetOperationState(SqliteConnection c,SqliteTransaction t,string id,string state,string progress,long updated,long? completed){using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="UPDATE p2b4_operations SET state=$state,progress_json=$progress,updated_ms=$updated,completed_ms=$completed WHERE id=$id";Add(cmd,"$id",id);Add(cmd,"$state",state);Add(cmd,"$progress",progress);Add(cmd,"$updated",updated);Add(cmd,"$completed",completed);cmd.ExecuteNonQuery();}
    private static void AddItem(SqliteConnection c,SqliteTransaction t,string op,int index,string entity,string? target,long? revision,string before,string requested,string outcome,string detail){using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="INSERT INTO p2b4_operation_items(operation_id,item_index,entity_type,target_id,expected_revision,before_json,requested_json,outcome,detail) VALUES($op,$index,$entity,$target,$revision,$before,$requested,$outcome,$detail)";Add(cmd,"$op",op);Add(cmd,"$index",index);Add(cmd,"$entity",entity);Add(cmd,"$target",target);Add(cmd,"$revision",revision);Add(cmd,"$before",before);Add(cmd,"$requested",requested);Add(cmd,"$outcome",outcome);Add(cmd,"$detail",detail);cmd.ExecuteNonQuery();}
    private static void Audit(SqliteConnection c,SqliteTransaction t,string actor,string action,string outcome,object detail){using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="INSERT INTO audit_events(id,at_ms,action,outcome,detail_json) VALUES($id,$now,$action,$outcome,$detail)";Add(cmd,"$id",Guid.NewGuid().ToString("N"));Add(cmd,"$now",Now());Add(cmd,"$action",action);Add(cmd,"$outcome",outcome);Add(cmd,"$detail",Json(new{actor,detail}));cmd.ExecuteNonQuery();}
    private static string RequireIdempotency(string? key){if(string.IsNullOrWhiteSpace(key)||key.Length>128)throw new P2B4WriteException("IDEMPOTENCY_KEY_REQUIRED","A bounded idempotencyKey is required.",400);return key.Trim();}
    private static string Json(object? value)=>JsonSerializer.Serialize(value,JsonOptions); private static string Fingerprint(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant(); private static long Now()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static void Add(SqliteCommand c,string name,object? value)=>c.Parameters.AddWithValue(name,value??DBNull.Value); private static long? NullableLong(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt64(i); private static int? Int(string value)=>int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var x)?x:null; private static long? Int64(string value)=>long.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var x)?x:null;

    private P2B4ImportStatus ChangeImportState(string actor,string id,string state)
    {
        EnsureWriteBoundary();
        using var connection = OpenWriteConnection();
        var row = ReadImportRow(connection, id, actor);
        if (row.State == "SUCCEEDED") return ReadImportStatus(connection, id, actor);
        if (row.State is "CANCELLED" or "CONFLICT") return ReadImportStatus(connection, id, actor);
        if (state != "CANCELLED") throw new P2B4WriteException("IMPORT_STATE_INVALID", "Unsupported import state transition.", 400);
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var now = Now();
        SetOperationState(connection, transaction, row.OperationId, "CANCELLED", Json(new { code = "CANCELLED" }), now, now);
        SetImportState(connection, transaction, id, "CANCELLED", now);
        Audit(connection, transaction, actor, "P2B4_IMPORT_CANCEL", "CANCELLED", new { operationId = row.OperationId, importId = id });
        transaction.Commit();
        return ReadImportStatus(connection, id, actor);
    }
    private P2B4ImportStatus ReadImportStatus(SqliteConnection c,string id,string? actor=null){var row=ReadImportRow(c,id,actor);var preview=JsonSerializer.Deserialize<P2B4ParsedImport>(row.PreviewJson,JsonOptions)??new(0,0,Array.Empty<string>(),Array.Empty<P2B4ParsedRow>());return new(id,row.OperationId,row.State,preview.RowCount,preview.ValidCount,preview.Errors,row.CreatedMs,row.UpdatedMs);}
    private static void SetImportState(SqliteConnection c,SqliteTransaction t,string id,string state,long updated){using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="UPDATE network_import_runs SET state=$state,updated_ms=$updated WHERE id=$id";Add(cmd,"$id",id);Add(cmd,"$state",state);Add(cmd,"$updated",updated);cmd.ExecuteNonQuery();}
    private static (string OperationId,string State,string RequestJson,string PreviewJson,long CreatedMs,long UpdatedMs,long ExpiresMs) ReadImportRow(SqliteConnection c,string id,string? actor=null){using var cmd=c.CreateCommand();cmd.CommandText="SELECT operation_id,state,request_json,preview_json,created_ms,updated_ms,expires_ms FROM network_import_runs WHERE id=$id"+(actor is null?string.Empty:" AND actor=$actor");Add(cmd,"$id",id);if(actor is not null)Add(cmd,"$actor",actor);using var r=cmd.ExecuteReader();if(!r.Read())throw new P2B4WriteException("IMPORT_NOT_FOUND","The import run does not exist.",404);return(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetInt64(5),r.GetInt64(6));}
    private sealed record P2B4ParsedImport(int RowCount,int ValidCount,IReadOnlyList<string> Errors,IReadOnlyList<P2B4ParsedRow> Rows);
    private sealed record P2B4ParsedRow(int Line,string EntityType,string? TargetId,string? DeviceId,string? DomainId,string? AddressFamily,string? Address,int? PrefixLength,string? AddressRole,string? MembershipRole,string? InterfaceReference,string? SourceKind,string? SourceReference,long? ExpectedRevision,string? Name,string? Purpose,string? TaggingMode,int? NativeVlanId,int? Pvid,int? QinQOuterId,int? QinQInnerId,int? VlanId,long? ValidFromMs,long? ValidToMs);
}
