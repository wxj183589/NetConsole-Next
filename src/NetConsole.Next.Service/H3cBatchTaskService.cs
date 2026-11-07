using System.Security.Cryptography;
using System.Text;

namespace NetConsole.Next.Service;

internal sealed record H3cTaskPlanCreateRequest(
    string? Id,
    IReadOnlyList<string> ProfileIds,
    string Command,
    long TimeoutMs,
    IReadOnlyList<string>? Commands = null);

internal sealed record H3cTaskPlanRetryRequest(int Ordinal, IReadOnlyList<int>? CommandIndexes = null);

internal sealed class H3cBatchTaskService(
    Catalog catalog,
    TaskSiteScopeAuthorizer scopes,
    P2B5SiteScopeStore siteScopes,
    BoundedCollectionScheduler scheduler)
{
    private readonly object _retryGate = new();
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
        var retries = catalog.GetTaskPlanRetries(planId).Select(retry =>
        {
            var run = catalog.GetTaskRun(retry.RetryRunId, currentScope);
            var commands = run is null ? Array.Empty<TaskCommandSnapshot>() : catalog.GetTaskRunCommands(run.Id);
            return new TaskPlanRetryReportSnapshot(retry, run, commands);
        }).ToArray();
        return new(plan, reports, retries);
    }

    public TaskPlanReportSnapshot Retry(string planId, H3cTaskPlanRetryRequest request, string actorUserId)
    {
        lock (_retryGate) return RetryCore(planId, request, actorUserId);
    }

    private TaskPlanReportSnapshot RetryCore(string planId, H3cTaskPlanRetryRequest request, string actorUserId)
    {
        if (request.Ordinal < 0) throw new ArgumentOutOfRangeException(nameof(request));
        var plan = GetVisible(planId, actorUserId);
        var item = plan.Items.SingleOrDefault(candidate => candidate.Ordinal == request.Ordinal)
            ?? throw new KeyNotFoundException("Task plan item was not found.");
        if (item.RunId is null || item.State is not ("FAILED" or "TIMED_OUT" or "CANCELLED" or "INTERRUPTED"))
            throw new InvalidOperationException("Only a failed task plan item can be retried.");
        var retryActor = catalog.GetActiveTaskActor(actorUserId)
            ?? throw new TaskScopeRevokedException("TASK_ACTOR_REVOKED_RETRY");
        var retryScope = siteScopes.Resolve(retryActor);
        if (!retryScope.IsAuthorized) throw new TaskScopeRevokedException("TASK_SCOPE_DENIED_RETRY");
        var original = catalog.GetTaskRun(item.RunId, retryScope)
            ?? throw new TaskScopeRevokedException("TASK_RETRY_ORIGINAL_RUN_UNAVAILABLE");
        if (original.State is not ("FAILED" or "TIMED_OUT" or "CANCELLED" or "INTERRUPTED"))
            throw new InvalidOperationException("Only a failed task run can be retried.");
        var originalCommands = catalog.GetTaskRunCommands(original.Id);
        var indexes = SelectRetryIndexes(originalCommands, request.CommandIndexes);
        var succeeded = new HashSet<int>();
        foreach (var retry in catalog.GetTaskPlanRetries(planId).Where(retry => retry.Ordinal == item.Ordinal))
        {
            var run = catalog.GetTaskRun(retry.RetryRunId, retryScope)
                ?? throw new TaskScopeRevokedException("TASK_RETRY_RUN_UNAVAILABLE");
            if (run.State is "QUEUED" or "CONNECTING" or "RUNNING")
                throw new InvalidOperationException("TASK_RETRY_ALREADY_ACTIVE");
            foreach (var command in catalog.GetTaskRunCommands(run.Id).Where(command => command.State == "SUCCEEDED"))
                if (command.Index >= 0 && command.Index < retry.CommandIndexes.Count)
                    succeeded.Add(retry.CommandIndexes[command.Index]);
        }
        if (request.CommandIndexes is not null && indexes.Any(succeeded.Contains))
            throw new InvalidOperationException("Successful retry commands cannot be selected again.");
        indexes = indexes.Where(index => !succeeded.Contains(index)).ToArray();
        if (indexes.Length == 0) throw new InvalidOperationException("TASK_RETRY_NOT_NEEDED");
        var definition = catalog.GetTaskDefinition(item.DefinitionId)
            ?? throw new TaskScopeRevokedException("TASK_PLAN_DEFINITION_MISSING");
        var selectedCommands = indexes.Select(index => originalCommands[index].Command).ToArray();
        var retryDefinitionId = "retry:" + Guid.NewGuid().ToString("N");
        catalog.CreateTaskDefinition(retryDefinitionId, "H3C_SSH_READONLY", definition.Target,
            selectedCommands[0], TimeSpan.FromMilliseconds(definition.TimeoutMs),
            scopeSnapshot: definition.ScopeSnapshot, commands: selectedCommands);
        if (!scheduler.TryScheduleDefinition(retryDefinitionId, out var retryRunId, actorUserId,
            admitted => catalog.CreateTaskPlanRetry(planId, item.Ordinal, original.Id, admitted, indexes, actorUserId)))
            throw new InvalidOperationException("COLLECTION_QUEUE_FULL");
        catalog.TryRecordDeviceAuditEvent(actorUserId, "WIRELESS_TASK_RETRY_REQUESTED", "ACCEPTED",
            planId, retryRunId, Array.Empty<string>(), $"ordinal={item.Ordinal};commands={string.Join(',', indexes)}");
        return GetReport(planId, actorUserId);
    }

    internal static int[] SelectRetryIndexes(IReadOnlyList<TaskCommandSnapshot> commands,
        IReadOnlyList<int>? requested)
    {
        var indexes = requested is null
            ? Enumerable.Range(0, commands.Count)
                .Where(index => !string.Equals(commands[index].State, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : requested.Distinct().Order().ToArray();
        if (indexes.Length is < 1 or > 32 || indexes.Any(index => index < 0 || index >= commands.Count))
            throw new ArgumentException("Retry command indexes are invalid.", nameof(requested));
        if (requested is not null && indexes.Any(index => string.Equals(commands[index].State, "SUCCEEDED", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Successful commands cannot be selected for retry.");
        return indexes;
    }

    public TaskPlanSnapshot Cancel(string planId, string actorUserId)
    {
        var plan = GetVisible(planId, actorUserId);
        foreach (var runId in plan.Items.Select(item => item.RunId).Where(item => item is not null).Cast<string>()
                     .Concat(catalog.GetTaskPlanRetries(planId).Select(retry => retry.RetryRunId)))
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
