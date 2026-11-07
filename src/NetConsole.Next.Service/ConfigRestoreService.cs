using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NetConsole.Next.Service;

internal sealed record ConfigRestorePreview(
    string Id,
    string SnapshotId,
    string TargetProfileId,
    string TargetDeviceId,
    string TargetSiteId,
    string ConfigurationKind,
    string SourceSha256,
    string? CurrentSnapshotId,
    int AddedLines,
    int RemovedLines,
    int ChangedLines,
    string Compatibility,
    string ConfirmationToken,
    long ExpiresMs,
    string State = "PREVIEW");

internal sealed record ConfigRestoreExecutionResult(
    string PreviewId,
    string State,
    string? Detail,
    long CompletedMs);

internal sealed record ConfigRestoreWriteResult(string State, string? Detail = null);

internal interface IConfigRestoreWriter
{
    Task<ConfigRestoreWriteResult> ApplyAsync(H3cDeviceProfileSnapshot target, string configuration,
        CancellationToken cancellationToken);
}

internal sealed class DisabledConfigRestoreWriter : IConfigRestoreWriter
{
    public Task<ConfigRestoreWriteResult> ApplyAsync(H3cDeviceProfileSnapshot target, string configuration,
        CancellationToken cancellationToken) =>
        throw new ConfigRestoreException("CONFIG_RESTORE_WRITE_ADAPTER_UNAVAILABLE",
            "当前恢复写入适配器未启用；预览和 dry-run 已完成，未向设备发送写命令。", 409);
}

internal sealed class ConfigRestoreException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal sealed class ConfigRestoreService(
    Catalog catalog,
    ConfigBackupService backups,
    IConfigRestoreWriter writer)
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, RestorePlan> _plans = new(StringComparer.Ordinal);

    public ConfigRestorePreview Preview(ConfigRestorePreviewRequest request, WebUserIdentity actor,
        P2B5SiteScopeDecision scope)
    {
        RequireSuperAdmin(actor);
        if (!scope.IsAuthorized) throw new ConfigRestoreException("CONFIG_RESTORE_SCOPE_DENIED", scope.Detail, 403);
        if (request is null || string.IsNullOrWhiteSpace(request.SnapshotId) ||
            string.IsNullOrWhiteSpace(request.TargetProfileId))
            throw new ConfigRestoreException("CONFIG_RESTORE_REQUEST_INVALID", "必须选择备份和目标设备。", 400);

        var source = backups.Get(request.SnapshotId.Trim(), scope)
            ?? throw new ConfigRestoreException("CONFIG_RESTORE_SOURCE_NOT_FOUND", "备份不存在或当前 Site 不可见。", 404);
        var target = catalog.GetH3cDeviceProfile(request.TargetProfileId.Trim());
        if (target is null || !target.Enabled || !catalog.H3cProfileIsInScope(target.Id, scope))
            throw new ConfigRestoreException("CONFIG_RESTORE_TARGET_NOT_VISIBLE", "目标设备不存在、已停用或不在当前 Site 范围内。", 404);
        if (catalog.GetNetmikoVendor(target.Id) != "h3c" ||
            string.Equals(target.DeviceType, "FIT_AP", StringComparison.OrdinalIgnoreCase))
            throw new ConfigRestoreException("CONFIG_RESTORE_TARGET_UNSUPPORTED", "恢复仅支持独立管理的 H3C 设备。", 409);
        if (!string.Equals(source.ProfileId, target.Id, StringComparison.Ordinal))
            throw new ConfigRestoreException("CONFIG_RESTORE_TARGET_MISMATCH", "备份和目标必须属于同一设备，禁止跨设备恢复。", 409);
        if (source.ConfigurationKind is not (ConfigBackupKinds.Running or ConfigBackupKinds.Saved or ConfigBackupKinds.StartupBody))
            throw new ConfigRestoreException("CONFIG_RESTORE_KIND_UNSUPPORTED", "当前备份类别没有安全恢复合同。", 409);

        var current = backups.List(target.Id, scope, ConfigBackupKinds.Running).FirstOrDefault();
        if (request.ExpectedCurrentSnapshotId is { Length: > 0 } expected &&
            !string.Equals(expected, current?.Id, StringComparison.Ordinal))
            throw new ConfigRestoreException("CONFIG_RESTORE_CURRENT_CHANGED", "目标当前配置已变化，请重新预览。", 409);

        var sourceText = ReadSnapshot(source.Id, scope);
        var currentText = current is null ? string.Empty : ReadSnapshot(current.Id, scope);
        var diff = Summarize(currentText, sourceText);
        var id = Guid.NewGuid().ToString("N");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var expires = DateTimeOffset.UtcNow.Add(PreviewLifetime).ToUnixTimeMilliseconds();
        var plan = new RestorePlan(id, actor.UserId, source.Id, target.Id, source.Sha256!, current?.Id,
            token, expires, sourceText, target);
        _plans[id] = plan;
        catalog.TryRecordDeviceAuditEvent(actor.UserId, "CONFIG_RESTORE_PREVIEW", "SUCCEEDED", target.Id,
            id, Array.Empty<string>(), $"snapshot={source.Id};kind={source.ConfigurationKind};compatibility=PROFILE_EXACT_H3C");
        return new(id, source.Id, target.Id, source.DeviceId, source.SiteId, source.ConfigurationKind,
            source.Sha256!, current?.Id, diff.Added, diff.Removed, diff.Changed, "PROFILE_EXACT_H3C",
            token, expires);
    }

    public async Task<ConfigRestoreExecutionResult> ExecuteAsync(ConfigRestoreExecutionRequest request,
        WebUserIdentity actor, P2B5SiteScopeDecision scope, CancellationToken cancellationToken)
    {
        RequireSuperAdmin(actor);
        if (!scope.IsAuthorized) throw new ConfigRestoreException("CONFIG_RESTORE_SCOPE_DENIED", scope.Detail, 403);
        if (request is null || string.IsNullOrWhiteSpace(request.PreviewId) ||
            string.IsNullOrWhiteSpace(request.ConfirmationToken))
            throw new ConfigRestoreException("CONFIG_RESTORE_CONFIRMATION_REQUIRED", "恢复必须使用有效的预览确认 token。", 400);
        if (!_plans.TryGetValue(request.PreviewId, out var plan) || plan.ActorUserId != actor.UserId)
            throw new ConfigRestoreException("CONFIG_RESTORE_PREVIEW_NOT_FOUND", "预览不存在、已过期或不属于当前账户。", 404);
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > plan.ExpiresMs)
        {
            _plans.TryRemove(plan.Id, out _);
            throw new ConfigRestoreException("CONFIG_RESTORE_PREVIEW_EXPIRED", "恢复预览已过期，请重新预览。", 409);
        }
        if (!CryptographicEquals(plan.Token, request.ConfirmationToken))
            throw new ConfigRestoreException("CONFIG_RESTORE_CONFIRMATION_INVALID", "恢复确认 token 无效。", 409);
        if (!catalog.H3cProfileIsInScope(plan.TargetProfileId, scope))
            throw new ConfigRestoreException("CONFIG_RESTORE_TARGET_NOT_VISIBLE", "目标设备已不在当前 Site 范围内。", 404);
        var current = backups.List(plan.TargetProfileId, scope, ConfigBackupKinds.Running).FirstOrDefault();
        if (!string.Equals(current?.Id, plan.ExpectedCurrentSnapshotId, StringComparison.Ordinal))
            throw new ConfigRestoreException("CONFIG_RESTORE_CURRENT_CHANGED", "目标当前配置已变化，请重新预览。", 409);
        if (!request.Apply)
        {
            catalog.TryRecordDeviceAuditEvent(actor.UserId, "CONFIG_RESTORE", "DRY_RUN", plan.TargetProfileId,
                plan.Id, Array.Empty<string>(), "no_device_write");
            return new(plan.Id, "DRY_RUN", "已完成恢复预览，未向设备发送写命令。", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        // A restore write may have reached the device even when its response is
        // lost. Consume the confirmation before invoking the writer so concurrent
        // requests cannot submit the same configuration twice.
        if (!_plans.TryRemove(plan.Id, out var claimed) || !ReferenceEquals(claimed, plan))
            throw new ConfigRestoreException("CONFIG_RESTORE_PREVIEW_ALREADY_USED", "该恢复预览已用于提交，请重新生成预览。", 409);

        try
        {
            var result = await writer.ApplyAsync(plan.Target, plan.Configuration, cancellationToken).ConfigureAwait(false);
            catalog.TryRecordDeviceAuditEvent(actor.UserId, "CONFIG_RESTORE", result.State, plan.TargetProfileId,
                plan.Id, Array.Empty<string>(), result.Detail ?? "write_adapter_result");
            return new(plan.Id, result.State, result.Detail, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (ConfigRestoreException ex)
        {
            catalog.TryRecordDeviceAuditEvent(actor.UserId, "CONFIG_RESTORE", "BLOCKED", plan.TargetProfileId,
                plan.Id, Array.Empty<string>(), ex.Code);
            throw;
        }
    }

    private string ReadSnapshot(string id, P2B5SiteScopeDecision scope)
    {
        var opened = backups.OpenDownload(id, scope)
            ?? throw new ConfigRestoreException("CONFIG_RESTORE_SOURCE_UNAVAILABLE", "备份文件不可读或完整性校验失败。", 409);
        using var stream = opened.Stream;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static (int Added, int Removed, int Changed) Summarize(string before, string after)
    {
        var oldLines = before.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var newLines = after.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
        var oldEnd = oldLines.Length; var newEnd = newLines.Length;
        while (oldEnd > prefix && newEnd > prefix && oldLines[oldEnd - 1] == newLines[newEnd - 1]) { oldEnd--; newEnd--; }
        var removed = oldEnd - prefix; var added = newEnd - prefix;
        return (added, removed, Math.Min(added, removed));
    }

    private static void RequireSuperAdmin(WebUserIdentity actor)
    {
        if (actor.Role != "SUPERADMIN")
            throw new ConfigRestoreException("CONFIG_RESTORE_PERMISSION_DENIED", "只有 SUPERADMIN 可以执行配置恢复。", 403);
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left); var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private sealed record RestorePlan(string Id, string ActorUserId, string SourceSnapshotId,
        string TargetProfileId, string SourceSha256, string? ExpectedCurrentSnapshotId, string Token,
        long ExpiresMs, string Configuration, H3cDeviceProfileSnapshot Target);
}
