namespace NetConsole.Next.Service;

internal sealed record WirelessTaskCreateRequest(
    IReadOnlyList<string>? DomainIds,
    IReadOnlyList<string>? Commands,
    long TimeoutMs = 60_000);

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
    H3cBatchTaskService plans)
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
                string.Equals(detail.Item.DeviceType.Trim(), "FIT_AP", StringComparison.OrdinalIgnoreCase) ||
                detail.TerminalProfile is not { Enabled: true } || string.IsNullOrWhiteSpace(detail.TerminalProfileId))
                throw new WirelessTaskException("WIRELESS_TASK_TARGET_UNSUPPORTED", "无线任务仅支持已关联有效连接档案的 H3C AC。", 409);
            profiles.Add(detail.TerminalProfileId);
            expected[detail.TerminalProfileId] = detail.Item.Id;
        }

        var planId = "wireless-plan-" + Guid.NewGuid().ToString("N");
        var plan = plans.CreateAndSchedule(new(planId, profiles, commands[0], request.TimeoutMs, commands),
            actor.UserId, expected);
        return new(plan.Id, plan.State, snapshot.Scope.Status, snapshot.Scope.ClaimsRevision,
            domainIds, members.Select(member => member.DeviceId).ToArray(), commands, plan,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
