using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

// The existing collector has one immutable target per admitted run. Its scope
// fits the existing task-definition JSON; no schema or audit-as-authority is needed.
internal sealed record TaskScopeSnapshot(int Version, string ActorUserId, string DeviceId,
    string ProfileId, string SiteId, long CreatedMs, string ClaimsRevision, H3cTaskEndpoint? Endpoint = null);
internal sealed record H3cTaskEndpoint(string Host, int Port);
internal sealed record TaskExecutionScope(TaskScopeSnapshot CreationScope, string ActorUserId, string DefinitionId,
    string RequiredRole = "SUPERADMIN");
internal sealed class TaskScopeRevokedException(string reason) : InvalidOperationException(reason)
{
    public string Reason { get; } = reason;
}

internal interface ITaskScopeAuthorizer
{
    TaskExecutionScope CaptureExecution(TaskDefinitionSnapshot definition, string? actorUserId);
    void Require(TaskExecutionScope scope, string target, string boundary);
}

internal sealed class TaskSiteScopeAuthorizer(Catalog catalog, P2B5SiteScopeStore scopes) : ITaskScopeAuthorizer
{
    public TaskScopeSnapshot CaptureDefinition(string profileId, string actorUserId, string requiredRole = "SUPERADMIN")
    {
        var actor = catalog.GetActiveTaskActor(actorUserId) ?? throw new TaskScopeRevokedException("TASK_ACTOR_REVOKED");
        if (!HasRole(actor.Role, requiredRole)) throw new TaskScopeRevokedException("TASK_ACTOR_ROLE_REVOKED");
        var current = scopes.Resolve(actor);
        if (!current.IsAuthorized) throw new TaskScopeRevokedException("TASK_SCOPE_DENIED");
        var device = catalog.GetTaskScopeTarget(profileId, current)
            ?? throw new TaskScopeRevokedException("TASK_TARGET_NOT_VISIBLE");
        var profile = catalog.GetH3cDeviceProfile(profileId) ?? throw new TaskScopeRevokedException("TASK_TARGET_NOT_VISIBLE");
        return new(1, actorUserId, device.Id, profileId, device.SiteId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            current.IsGlobal ? "GLOBAL" : current.ClaimsRevision ?? throw new TaskScopeRevokedException("TASK_CLAIMS_REVISION_UNAVAILABLE"),
            new(profile.Host, profile.Port));
    }

    public TaskExecutionScope CaptureExecution(TaskDefinitionSnapshot definition, string? actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId) || definition.ScopeSnapshot is not { Version: 1 } snapshot)
            throw new TaskScopeRevokedException("TASK_SCOPE_SNAPSHOT_REQUIRED");
        var execution = new TaskExecutionScope(snapshot, actorUserId, definition.Id);
        Require(execution, definition.Target, "ADMISSION");
        return execution;
    }

    public void Require(TaskExecutionScope scope, string target, string boundary)
    {
        try
        {
            var actor = catalog.GetActiveTaskActor(scope.ActorUserId);
            if (actor is null) throw new TaskScopeRevokedException("TASK_ACTOR_REVOKED_" + boundary);
            if (!HasRole(actor.Role, scope.RequiredRole))
                throw new TaskScopeRevokedException("TASK_ACTOR_ROLE_REVOKED_" + boundary);
            var current = scopes.Resolve(actor);
            var snapshot = scope.CreationScope;
            if (snapshot.Version != 1 || target != snapshot.ProfileId || !DeviceSiteScope.Allows(current, snapshot.SiteId))
                throw new TaskScopeRevokedException("TASK_SCOPE_REVOKED_" + boundary);
            var device = catalog.GetTaskScopeTarget(target, current);
            if (device is null || device.Id != snapshot.DeviceId || device.SiteId != snapshot.SiteId || !device.Enabled)
                throw new TaskScopeRevokedException("TASK_TARGET_REVOKED_" + boundary);
            var profile = catalog.GetH3cDeviceProfile(target);
            if (snapshot.Endpoint is null || profile is null || profile.Host != snapshot.Endpoint.Host || profile.Port != snapshot.Endpoint.Port)
                throw new TaskScopeRevokedException("TASK_ENDPOINT_REVOKED_" + boundary);
            var currentRevision = current.IsGlobal ? "GLOBAL" : current.ClaimsRevision;
            if (currentRevision != snapshot.ClaimsRevision)
                catalog.TryRecordDeviceAuditEvent(scope.ActorUserId, "TASK_SCOPE_REVISION_CHECK", "AUTHORIZED", scope.DefinitionId,
                    null, new[] { "claimsRevision" }, $"creation={snapshot.ClaimsRevision};current={currentRevision};boundary={boundary}");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException)
        {
            throw new TaskScopeRevokedException("TASK_SCOPE_AUTHORITY_UNAVAILABLE_" + boundary);
        }
    }

    private static bool HasRole(string actual, string required) => required == "SUPERADMIN" ? actual == "SUPERADMIN" :
        required == "ADMIN" && actual is "ADMIN" or "SUPERADMIN";
}

internal sealed partial class Catalog
{
    public WebUserIdentity? GetActiveTaskActor(string userId)
    {
        // The local bearer is a built-in machine authority, never a stored token.
        if (userId == "local-api-token") return new(userId, userId, "SUPERADMIN", "task-execution");
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,username,role FROM web_users WHERE id=$id AND disabled=0";
        command.Parameters.AddWithValue("$id", userId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), "task-execution") : null;
    }

    public DeviceInventoryItem? GetTaskScopeTarget(string profileId, P2B5SiteScopeDecision scope)
    {
        if (!scope.IsAuthorized) return null;
        using var db = Open();
        using var command = db.CreateCommand();
        var parameters = new List<(string Name, object? Value)>();
        var filter = DeviceSiteScope.BuildSqlFilter(scope, parameters, "d.site_id");
        command.CommandText = $@"SELECT d.id FROM device_inventory d
            JOIN h3c_device_profiles p ON p.id=d.terminal_profile_id
            JOIN collection_sources source ON source.id=p.source_id
            WHERE p.id=$profile AND p.enabled=1 AND d.enabled=1 AND d.site_id=source.site_id AND {filter} LIMIT 2";
        command.Parameters.AddWithValue("$profile", profileId);
        DeviceSiteScope.AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var id = reader.GetString(0);
        if (reader.Read()) return null; // Ambiguity cannot expand the frozen target set.
        reader.Close();
        return GetDeviceInventoryDetail(id, scope)?.Item;
    }

    private static TaskScopeSnapshot? ReadTaskScope(JsonElement request) =>
        request.TryGetProperty("scopeSnapshot", out var snapshot) && snapshot.ValueKind != JsonValueKind.Null
            ? JsonSerializer.Deserialize<TaskScopeSnapshot>(snapshot.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : null;

    private static bool SameTaskScope(TaskScopeSnapshot? first, TaskScopeSnapshot? second) => first == second;
}
