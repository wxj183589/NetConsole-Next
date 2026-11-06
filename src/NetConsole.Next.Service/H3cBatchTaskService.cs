using System.Security.Cryptography;
using System.Text;

namespace NetConsole.Next.Service;

internal sealed record H3cTaskPlanCreateRequest(
    string? Id,
    IReadOnlyList<string> ProfileIds,
    string Command,
    long TimeoutMs,
    IReadOnlyList<string>? Commands = null);

internal sealed class H3cBatchTaskService(
    Catalog catalog,
    TaskSiteScopeAuthorizer scopes,
    P2B5SiteScopeStore siteScopes,
    BoundedCollectionScheduler scheduler)
{
    public TaskPlanSnapshot CreateAndSchedule(H3cTaskPlanCreateRequest request, string actorUserId,
        IReadOnlyDictionary<string, string>? expectedDeviceIds = null)
    {
        if (request.ProfileIds is null || request.ProfileIds.Count is < 1 or > 64)
            throw new ArgumentException("A task plan must target between 1 and 64 devices.", nameof(request));
        var targets = request.ProfileIds.Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length != request.ProfileIds.Count)
            throw new ArgumentException("Task plan targets must be non-empty and unique.", nameof(request));
        if (request.TimeoutMs is < 100 or > 600000)
            throw new ArgumentException("timeoutMs must be between 100 and 600000.", nameof(request));
        var commands = request.Commands is { Count: > 0 } ? request.Commands.ToArray() : [request.Command];
        if (commands.Length is < 1 or > 32 || string.IsNullOrWhiteSpace(request.Command) ||
            !string.Equals(commands[0], request.Command, StringComparison.Ordinal) ||
            commands.Distinct(StringComparer.Ordinal).Count() != commands.Length ||
            commands.Any(item => !H3cCommandPolicy.IsAllowed(item)))
            throw new ArgumentException("Task plan commands must be a unique allowlisted read-only plan.", nameof(request));

        var planId = string.IsNullOrWhiteSpace(request.Id) ? "h3c-plan-" + Guid.NewGuid().ToString("N") : request.Id.Trim();
        if (planId.Length > 96 || planId.Any(char.IsControl)) throw new ArgumentException("Task plan id is invalid.", nameof(request));
        var definitions = new List<(int Ordinal, string Target, string DefinitionId, TaskScopeSnapshot Scope)>();
        for (var index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            var definitionId = $"{planId}:d:{index}";
            definitions.Add((index, target, definitionId, scopes.CaptureDefinition(target, actorUserId)));
        }

        // Inventory callers freeze the user's selected device identities before
        // resolving profiles. A concurrent rebind must never change that set.
        if (expectedDeviceIds is not null && (expectedDeviceIds.Count != targets.Length ||
            definitions.Any(item => !expectedDeviceIds.TryGetValue(item.Target, out var expected) ||
                !string.Equals(item.Scope.DeviceId, expected, StringComparison.Ordinal))))
            throw new InvalidOperationException("设备连接档案关联已变化，请刷新设备列表后重试。");

        foreach (var definition in definitions)
            catalog.CreateTaskDefinition(definition.DefinitionId, "H3C_SSH_READONLY", definition.Target,
                commands[0], TimeSpan.FromMilliseconds(request.TimeoutMs), scopeSnapshot: definition.Scope, commands: commands);

        var claimsRevision = claimsFingerprint(definitions.Select(item => item.Scope.ClaimsRevision));
        catalog.CreateTaskPlan(planId, "H3C_SSH_READONLY", actorUserId, commands, claimsRevision,
            definitions.Select(item => (item.Ordinal, item.Target, item.DefinitionId)).ToArray());

        foreach (var definition in definitions)
        {
            try
            {
                if (scheduler.TryScheduleDefinition(definition.DefinitionId, out var runId, actorUserId))
                    catalog.SetTaskPlanItemRun(planId, definition.Ordinal, runId);
                else
                    catalog.FailTaskPlanItem(planId, definition.Ordinal, "COLLECTION_QUEUE_FULL");
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or TaskScopeRevokedException)
            {
                catalog.FailTaskPlanItem(planId, definition.Ordinal, ex is TaskScopeRevokedException revoked ? revoked.Reason : ex.GetType().Name);
            }
        }
        return GetVisible(planId, actorUserId);
    }

    public TaskPlanSnapshot GetVisible(string planId, string actorUserId)
    {
        var plan = catalog.GetTaskPlan(planId) ?? throw new KeyNotFoundException("Task plan was not found.");
        foreach (var item in plan.Items)
        {
            var definition = catalog.GetTaskDefinition(item.DefinitionId)
                ?? throw new TaskScopeRevokedException("TASK_PLAN_DEFINITION_MISSING");
            _ = scopes.CaptureExecution(definition, actorUserId);
        }
        return catalog.GetTaskPlan(planId) ?? throw new KeyNotFoundException("Task plan was not found.");
    }

    public TaskPlanReportSnapshot GetReport(string planId, string actorUserId)
    {
        var plan = GetVisible(planId, actorUserId);
        var actor = catalog.GetActiveTaskActor(actorUserId)
            ?? throw new TaskScopeRevokedException("TASK_ACTOR_REVOKED_REPORT");
        var currentScope = siteScopes.Resolve(actor);
        if (!currentScope.IsAuthorized) throw new TaskScopeRevokedException("TASK_SCOPE_DENIED_REPORT");
        var reports = plan.Items.Select(item =>
        {
            var run = item.RunId is null ? null : catalog.GetTaskRun(item.RunId, currentScope);
            var commands = run is null ? Array.Empty<TaskCommandSnapshot>() : catalog.GetTaskRunCommands(run.Id);
            return new TaskPlanReportItemSnapshot(item, run, commands);
        }).ToArray();
        return new(plan, reports);
    }

    public TaskPlanSnapshot Cancel(string planId, string actorUserId)
    {
        var plan = GetVisible(planId, actorUserId);
        foreach (var runId in plan.Items.Select(item => item.RunId).Where(item => item is not null).Cast<string>())
            scheduler.Cancel(runId);
        return GetVisible(planId, actorUserId);
    }

    private static string claimsFingerprint(IEnumerable<string> revisions)
    {
        var normalized = string.Join("|", revisions.Order(StringComparer.Ordinal));
        if (normalized == "GLOBAL") return "GLOBAL";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
