namespace NetConsole.Next.Service;

internal sealed record DeviceConnectionTestRequest(IReadOnlyList<string>? DeviceIds);

internal sealed class DeviceConnectionTestException(string code, string message, int statusCode)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

// Inventory owns identity and profile association. The existing task service
// owns admission, bounded execution, persistence, reports and cancellation.
internal sealed class DeviceConnectionTestService(Catalog catalog, H3cBatchTaskService plans)
{
    public TaskPlanSnapshot Start(DeviceConnectionTestRequest request, WebUserIdentity actor,
        P2B5SiteScopeDecision scope)
    {
        if (actor.Role != "SUPERADMIN")
            throw new DeviceConnectionTestException("DEVICE_CONNECTION_TEST_PERMISSION_DENIED", "当前账户不能执行批量连接测试。", 403);
        if (!scope.IsAuthorized)
            throw new DeviceConnectionTestException("DEVICE_CONNECTION_TEST_PERMISSION_DENIED", "当前账户没有可用的站点权限。", 403);

        var requested = request.DeviceIds;
        if (requested is null || requested.Count is < 1 or > 64 ||
            requested.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)))
            throw InvalidTargets();
        var deviceIds = requested.Select(id => id.Trim()).ToArray();
        if (deviceIds.Distinct(StringComparer.Ordinal).Count() != deviceIds.Length)
            throw InvalidTargets();

        // Finish visibility checks for the entire batch before revealing any
        // compatibility or association information, and before creating tasks.
        var devices = deviceIds.Select(id => catalog.GetDeviceInventoryDetail(id, scope)
            ?? throw new DeviceConnectionTestException("DEVICE_CONNECTION_TEST_TARGET_NOT_VISIBLE", "设备不存在或不在当前站点权限范围内。", 404)).ToArray();
        var profiles = new List<string>(devices.Length);
        var expectedDeviceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            var profileId = device.TerminalProfileId;
            if (!device.Item.Enabled || !string.Equals(device.Item.Vendor.Trim(), "H3C", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(device.Item.DeviceType.Trim(), "FIT_AP", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(profileId) || device.TerminalProfile is not { Enabled: true } ||
                catalog.GetTaskScopeTarget(profileId, scope)?.Id != device.Item.Id)
                throw new DeviceConnectionTestException("DEVICE_CONNECTION_TEST_TARGET_UNSUPPORTED",
                    "仅支持已启用且精确关联有效连接档案的 H3C 独立设备；FIT-AP 由 AC 管理。", 409);
            profiles.Add(profileId);
            expectedDeviceIds.Add(profileId, device.Item.Id);
        }

        // No credentials, user commands or endpoint overrides cross this API.
        return plans.CreateAndSchedule(new(null, profiles, H3cCommandPolicy.DisplayVersion, 30_000), actor.UserId,
            expectedDeviceIds);
    }

    private static DeviceConnectionTestException InvalidTargets() => new("DEVICE_CONNECTION_TEST_INVALID_TARGETS",
        "请选择 1 至 64 个不重复的设备 UUID。", 400);
}
