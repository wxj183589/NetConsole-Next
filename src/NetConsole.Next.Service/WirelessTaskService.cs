namespace NetConsole.Next.Service;

internal sealed record WirelessTaskCreateRequest(
    IReadOnlyList<string>? DomainIds,
    IReadOnlyList<string>? Commands,
    long TimeoutMs = 60_000,
    IReadOnlyList<string>? AcDeviceIds = null);

internal sealed record WirelessTaskRetryRequest(int Ordinal, IReadOnlyList<int>? CommandIndexes = null);

internal sealed record WirelessTaskPlanSnapshot(
    string Id,
    string State,
    string ScopeStatus,
    string ClaimsRevision,
    IReadOnlyList<string> DomainIds,
    IReadOnlyList<string> AcDeviceIds,
    IReadOnlyList<string> Commands,
    TaskPlanSnapshot Plan,
    long CreatedMs);

internal sealed class WirelessTaskException(string code, string message, int statusCode) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

// Wireless tasks are a thin business adapter over the existing bounded H3C
// task engine. They never create FIT-AP targets and never accept write commands.
internal sealed class WirelessTaskService(
    WirelessReadModelService wireless,
    Catalog catalog,
    H3cBatchTaskService plans,
    P2B5SiteScopeStore siteScopes)
{
    private static readonly string[] DefaultCommands =
    [
        H3cCommandPolicy.DisplayVersion,
        H3cCommandPolicy.DisplayWlanApAll,
        H3cCommandPolicy.DisplayWlanApAllAddress,
        H3cCommandPolicy.DisplayWlanApAllRadio
    ];

    public WirelessTaskPlanSnapshot Create(WirelessTaskCreateRequest request,
        WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        if (!string.Equals(actor.Role, "SUPERADMIN", StringComparison.OrdinalIgnoreCase))
            throw new WirelessTaskException("WIRELESS_TASK_PERMISSION_DENIED", "无线采集任务需要 SUPERADMIN 权限。", 403);
        if (!scope.IsAuthorized)
            throw new WirelessTaskException("WIRELESS_TASK_SCOPE_DENIED", "当前账户没有可用的站点权限。", 403);

        var domainIds = request.DomainIds?.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        if (domainIds.Length is < 1 or > 16)
            throw new WirelessTaskException("WIRELESS_TASK_INVALID_TARGETS", "请选择 1 至 16 个无线 AC Domain。", 400);

        var snapshot = wireless.GetSnapshot(scope);
        var domains = domainIds.Select(id => snapshot.AcDomains.FirstOrDefault(domain => domain.Id == id)
            ?? throw new WirelessTaskException("WIRELESS_TASK_TARGET_NOT_VISIBLE", "无线 AC Domain 不存在或不在当前权限范围内。", 404)).ToArray();
        var members = domains.SelectMany(domain => domain.Members)
            .GroupBy(member => member.DeviceId, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        if (request.AcDeviceIds is { Count: > 0 })
        {
            var requested = request.AcDeviceIds.Select(id => id?.Trim() ?? "").ToArray();
            if (requested.Any(string.IsNullOrWhiteSpace) || requested.Distinct(StringComparer.Ordinal).Count() != requested.Length ||
                requested.Any(id => !members.Any(member => member.DeviceId == id)))
                throw new WirelessTaskException("WIRELESS_TASK_INVALID_AC_TARGETS", "所选 AC 不属于可见的目标 Domain。", 400);
            members = members.Where(member => requested.Contains(member.DeviceId, StringComparer.Ordinal)).ToArray();
        }
        if (members.Length is < 1 or > 64)
            throw new WirelessTaskException("WIRELESS_TASK_TARGET_LIMIT", "无线采集任务最多包含 64 台 AC。", 413);

        var commands = request.Commands is { Count: > 0 }
            ? request.Commands.Where(command => !string.IsNullOrWhiteSpace(command)).Select(command => command.Trim()).ToArray()
            : DefaultCommands;
        if (commands.Length is < 1 or > 16 || commands.Distinct(StringComparer.Ordinal).Count() != commands.Length ||
            commands.Any(command => !H3cCommandPolicy.IsAllowed(command)))
            throw new WirelessTaskException("WIRELESS_TASK_COMMANDS_INVALID", "无线任务命令必须是唯一的只读白名单命令。", 400);
        if (request.TimeoutMs is < 100 or > 600_000)
            throw new WirelessTaskException("WIRELESS_TASK_TIMEOUT_INVALID", "任务超时必须在 100 到 600000 毫秒之间。", 400);

        var profiles = new List<string>(members.Length);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var detail = catalog.GetDeviceInventoryDetail(member.DeviceId, scope);
            if (detail is null || !detail.Item.Enabled ||
                !string.Equals(detail.Item.Vendor.Trim(), "H3C", StringComparison.OrdinalIgnoreCase) ||
                !IsSupportedDeviceType(detail.Item.DeviceType) ||
                detail.TerminalProfile is not { Enabled: true } || string.IsNullOrWhiteSpace(detail.TerminalProfileId))
                throw new WirelessTaskException("WIRELESS_TASK_TARGET_UNSUPPORTED", "无线任务仅支持已关联有效连接档案的 H3C AC。", 409);
            profiles.Add(detail.TerminalProfileId);
            if (!expected.TryAdd(detail.TerminalProfileId, detail.Item.Id))
                throw new WirelessTaskException("WIRELESS_TASK_PROFILE_CONFLICT", "多个 AC 关联同一个连接档案。", 409);
        }

        var planId = "wireless-plan-" + Guid.NewGuid().ToString("N");
        var plan = plans.CreateAndSchedule(new(planId, profiles, commands[0], request.TimeoutMs, commands),
            actor.UserId, expected);
        return new(plan.Id, plan.State, snapshot.Scope.Status, snapshot.Scope.ClaimsRevision,
            domainIds, members.Select(member => member.DeviceId).ToArray(), commands, plan,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal static bool IsSupportedDeviceType(string deviceType) =>
        string.Equals(deviceType.Trim(), "AC", StringComparison.OrdinalIgnoreCase);

    // Filtering covers this actor's latest 200 task plans, not an unbounded archive.
    public IReadOnlyList<WirelessTaskSummary> List(string actorUserId, int limit = 50,
        string? siteId = null, string? acDeviceId = null, string? state = null)
    {
        var results = new List<WirelessTaskSummary>();
        var take = Math.Clamp(limit, 1, 200);
        foreach (var id in catalog.GetWirelessTaskPlanIds(actorUserId, 200))
        {
            if (!WirelessTaskReportBuilder.IsWirelessId(id)) continue;
            try
            {
                // Recheck current actor/target/endpoint/Site authority before filtering.
                var summary = GetReport(id, actorUserId).Summary;
                if (!string.IsNullOrWhiteSpace(siteId) && !(summary.SiteIds?.Contains(siteId.Trim(), StringComparer.Ordinal) ?? false) ||
                    !string.IsNullOrWhiteSpace(acDeviceId) && !(summary.AcDeviceIds?.Contains(acDeviceId.Trim(), StringComparer.Ordinal) ?? false) ||
                    !string.IsNullOrWhiteSpace(state) && !string.Equals(summary.State, state.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                results.Add(summary);
                if (results.Count == take) break;
            }
            catch (TaskScopeRevokedException) { }
            catch (KeyNotFoundException) { }
        }
        return results;
    }

    public WirelessTaskReport GetReport(string id, string actorUserId)
    {
        if (!WirelessTaskReportBuilder.IsWirelessId(id)) throw new KeyNotFoundException();
        var report = plans.GetReport(id, actorUserId);
        var creationScopes = report.Plan.Items.ToDictionary(item => item.Ordinal,
            item => catalog.GetTaskDefinition(item.DefinitionId)?.ScopeSnapshot
                ?? throw new TaskScopeRevokedException("TASK_SCOPE_SNAPSHOT_REQUIRED"));
        var result = WirelessTaskReportBuilder.Build(report, creationScopes);
        var actor = catalog.GetActiveTaskActor(actorUserId) ?? throw new TaskScopeRevokedException("TASK_ACTOR_REVOKED_REPORT");
        var scope = siteScopes.Resolve(actor);
        if (!scope.IsAuthorized) throw new TaskScopeRevokedException("TASK_SCOPE_DENIED_REPORT");
        var projection = catalog.ReadWirelessTaskEvidence(result, scope).Projection;
        var observations = projection.ApResources.SelectMany(resource => resource.Observations).ToArray();
        _ = plans.GetVisible(id, actorUserId);
        return result with { ObservationStatus = observations.Length > 0 ? "PUBLISHED" : "NOT_PUBLISHED",
            Resources = projection.ApResources, Observations = observations };
    }

    public WirelessTaskReport Retry(string id, WirelessTaskRetryRequest request,
        WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        if (!string.Equals(actor.Role, "SUPERADMIN", StringComparison.OrdinalIgnoreCase))
            throw new WirelessTaskException("WIRELESS_TASK_PERMISSION_DENIED", "无线采集任务需要 SUPERADMIN 权限。", 403);
        if (!scope.IsAuthorized)
            throw new WirelessTaskException("WIRELESS_TASK_SCOPE_DENIED", "当前账户没有可用的站点权限。", 403);
        if (!WirelessTaskReportBuilder.IsWirelessId(id)) throw new KeyNotFoundException();
        _ = plans.Retry(id, new H3cTaskPlanRetryRequest(request.Ordinal, request.CommandIndexes), actor.UserId);
        return GetReport(id, actor.UserId);
    }

    public byte[] Export(string id, string actorUserId)
    {
        var report = GetReport(id, actorUserId);
        var actor = catalog.GetActiveTaskActor(actorUserId) ?? throw new TaskScopeRevokedException("TASK_ACTOR_REVOKED_REPORT");
        var scope = siteScopes.Resolve(actor);
        if (!scope.IsAuthorized) throw new TaskScopeRevokedException("TASK_SCOPE_DENIED_REPORT");
        var evidence = catalog.ReadWirelessTaskEvidence(report, scope).Evidence;
        var bytes = WirelessTaskReportBuilder.Export(report, evidence);
        _ = plans.GetVisible(id, actorUserId);
        return bytes;
    }
}
