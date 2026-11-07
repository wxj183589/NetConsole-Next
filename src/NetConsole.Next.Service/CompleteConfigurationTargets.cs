namespace NetConsole.Next.Service;

internal sealed record CompleteConfigurationTargetRequest(IReadOnlyList<string>? ProfileIds = null,
    string? SiteId = null, string? DomainId = null);
internal sealed record CompleteConfigurationTarget(string ProfileId, string DeviceId, string Name,
    string SiteId, string Site, string Model, string Address);
internal sealed record CompleteConfigurationTargetPreview(string SelectionKind, string? SelectionId,
    IReadOnlyList<string> ProfileIds, IReadOnlyList<CompleteConfigurationTarget> Items, long ResolvedMs);

// Site/Domain selection is resolved once. Batch and schedule admission then use
// exactly these profiles; future inventory additions never expand the selection.
internal sealed class CompleteConfigurationTargets(Catalog catalog, WirelessReadModelService wireless,
    ConfigBackupService backups)
{
    public CompleteConfigurationTargetPreview Resolve(CompleteConfigurationTargetRequest request,
        WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        if (actor.Role != "SUPERADMIN" || !scope.IsAuthorized)
            throw new ConfigBackupException("CONFIG_BACKUP_PERMISSION_DENIED", "需要有效的 SUPERADMIN Site 权限。", 403);
        var explicitProfiles = request.ProfileIds is { Count: > 0 };
        var site = !string.IsNullOrWhiteSpace(request.SiteId);
        var domain = !string.IsNullOrWhiteSpace(request.DomainId);
        if ((explicitProfiles ? 1 : 0) + (site ? 1 : 0) + (domain ? 1 : 0) != 1)
            throw new ConfigBackupException("CONFIG_COMPLETE_SELECTION_INVALID", "请只选择设备、Site 或 AC Domain 中的一种范围。", 400);
        IReadOnlyList<string> profiles;
        if (explicitProfiles) profiles = request.ProfileIds!;
        else if (site)
        {
            if (!DeviceSiteScope.Allows(scope, request.SiteId!))
                throw new ConfigBackupException("CONFIG_BACKUP_TARGET_NOT_VISIBLE", "Site 不在当前权限范围内。", 404);
            profiles = catalog.CompleteConfigurationSiteProfiles(request.SiteId!, scope);
        }
        else
        {
            var selected = wireless.GetSnapshot(scope).AcDomains.SingleOrDefault(item => item.Id == request.DomainId)
                ?? throw new ConfigBackupException("CONFIG_BACKUP_TARGET_NOT_VISIBLE", "AC Domain 不存在或不可见。", 404);
            profiles = selected.MemberDeviceIds.Select(id => catalog.GetDeviceInventoryDetail(id, scope)?.TerminalProfileId
                ?? throw new ConfigBackupException("CONFIG_BACKUP_TARGET_UNSUPPORTED", "Domain 成员缺少有效连接档案。", 409)).ToArray();
        }
        var normalized = profiles.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal).ToArray();
        if (normalized.Length is < 1 or > 32)
            throw new ConfigBackupException("CONFIG_BACKUP_RESOURCE_LIMIT", "所选范围必须包含 1 至 32 台设备。", 413);
        var items = normalized.Select(profile =>
        {
            backups.ValidateCompleteTarget(profile, actor, scope, ConfigBackupKinds.CompleteRunning);
            var device = catalog.GetTaskScopeTarget(profile, scope)
                ?? throw new ConfigBackupException("CONFIG_BACKUP_TARGET_NOT_VISIBLE", "设备已停用或关联不唯一。", 404);
            if (device.DeviceType.Equals("FIT_AP", StringComparison.OrdinalIgnoreCase))
                throw new ConfigBackupException("CONFIG_BACKUP_TARGET_UNSUPPORTED", "完整批量备份仅支持独立管理设备。", 409);
            return new CompleteConfigurationTarget(profile, device.Id, device.Name, device.SiteId,
                device.Site, device.Model, device.ManagementAddress);
        }).ToArray();
        return new(site ? "SITE" : domain ? "DOMAIN" : "PROFILES", site ? request.SiteId : request.DomainId,
            normalized, items, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}

internal sealed partial class Catalog
{
    internal IReadOnlyList<string> CompleteConfigurationSiteProfiles(string siteId, P2B5SiteScopeDecision scope)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        var parameters = new List<(string Name, object? Value)>();
        var filter = DeviceSiteScope.BuildSqlFilter(scope, parameters, "d.site_id");
        command.CommandText = $@"SELECT DISTINCT d.terminal_profile_id FROM device_inventory d
            JOIN h3c_device_profiles p ON p.id=d.terminal_profile_id
            JOIN collection_sources s ON s.id=p.source_id
            WHERE d.site_id=$site AND d.site_id=s.site_id AND d.enabled=1 AND p.enabled=1
            AND {filter} ORDER BY d.terminal_profile_id LIMIT 33";
        command.Parameters.AddWithValue("$site", siteId);
        DeviceSiteScope.AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
}
