using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record ConfigBackupScheduleRequest(
    IReadOnlyList<string>? ProfileIds, string? ConfigurationKind, int IntervalMinutes);

internal sealed record ConfigBackupScheduleEnabledRequest(bool Enabled);

internal sealed record ConfigBackupScheduleSnapshot(
    string Id,
    string OwnerUserId,
    IReadOnlyList<string> ProfileIds,
    string ConfigurationKind,
    int IntervalMinutes,
    bool Enabled,
    long CreatedMs,
    long? NextRunMs,
    long? LastRunMs,
    long? LastCompletedMs,
    string LastState,
    string? LastError,
    string? LastBatchId);

// This service only admits existing backup batches. Device connections, current
// credentials, Raw, version files and command policy remain in their existing owners.
internal sealed class ConfigBackupScheduleService : BackgroundService
{
    internal const int MaximumSchedules = 64;
    internal const int MinimumIntervalMinutes = 60;
    internal const int MaximumIntervalMinutes = 30 * 24 * 60;
    private const int MaximumProfiles = 32;
    private const int MaximumActiveSchedules = 4;
    private const int MaximumStoreBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Catalog _catalog;
    private readonly P2B5SiteScopeStore _scopes;
    private readonly TaskSiteScopeAuthorizer _taskScopes;
    private readonly ConfigBackupService _backups;
    private readonly ILogger<ConfigBackupScheduleService> _logger;
    private readonly string _storePath;
    private bool _storeUnavailable;
    private Dictionary<string, ConfigBackupScheduleSnapshot> _schedules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunningBatch> _running = new(StringComparer.Ordinal);

    public ConfigBackupScheduleService(Catalog catalog, P2B5SiteScopeStore scopes,
        TaskSiteScopeAuthorizer taskScopes, ConfigBackupService backups, RuntimeSettings settings,
        ILogger<ConfigBackupScheduleService> logger)
    {
        (_catalog, _scopes, _taskScopes, _backups, _logger) = (catalog, scopes, taskScopes, backups, logger);
        _storePath = Path.Combine(settings.DataRoot, "backup", "config-schedules.json");
        try { Load(); }
        catch (Exception ex) when (ex is ConfigBackupException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            // A damaged optional schedule file must not stop syslog or other
            // collection services. Keep the original file and reject only plans.
            _storeUnavailable = true;
            _logger.LogWarning("Configuration backup schedule storage is unavailable; plans are disabled until the file is repaired and the service restarted.");
        }
    }

    public ConfigBackupScheduleSnapshot Create(ConfigBackupScheduleRequest request, WebUserIdentity actor,
        P2B5SiteScopeDecision scope)
    {
        var current = RequireActor(actor, scope, requireModule: true);
        if (request.IntervalMinutes is < MinimumIntervalMinutes or > MaximumIntervalMinutes)
            throw new ConfigBackupException("CONFIG_BACKUP_SCHEDULE_INTERVAL_INVALID", "备份间隔应为 60 至 43200 分钟。", 400);
        var kind = request.ConfigurationKind ?? ConfigBackupKinds.Running;
        _ = ConfigBackupKinds.CommandFor(kind);
        var profiles = (request.ProfileIds ?? Array.Empty<string>()).Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (profiles.Length is < 1 or > MaximumProfiles || profiles.Any(item => !IsSafeId(item)))
            throw new ConfigBackupException("CONFIG_BACKUP_SCHEDULE_TARGET_LIMIT", "每个计划请选择 1 至 32 台设备。", 400);
        RequireTargets(profiles, current, scope);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var item = new ConfigBackupScheduleSnapshot(Guid.NewGuid().ToString("N"), current.UserId, profiles,
            kind, request.IntervalMinutes, true, now, NextDue(now, request.IntervalMinutes), null, null,
            "NEVER_RUN", null, null);
        lock (_gate)
        {
            if (_schedules.Count >= MaximumSchedules)
                throw new ConfigBackupException("CONFIG_BACKUP_SCHEDULE_LIMIT", "最多保留 64 个定时备份计划，请先删除不再使用的计划。", 409);
            Commit(item);
        }
        return Copy(item);
    }

    public IReadOnlyList<ConfigBackupScheduleSnapshot> List(WebUserIdentity actor, P2B5SiteScopeDecision scope,
        string? profileId = null)
    {
        var current = RequireActor(actor, scope);
        lock (_gate)
            return _schedules.Values.Where(item => IsVisible(item, current, scope) &&
                    (profileId is null || item.ProfileIds.Contains(profileId, StringComparer.Ordinal)))
                .OrderByDescending(item => item.CreatedMs).ThenBy(item => item.Id, StringComparer.Ordinal)
                .Select(Copy).ToArray();
    }

    public ConfigBackupScheduleSnapshot? SetEnabled(string id, bool enabled, WebUserIdentity actor,
        P2B5SiteScopeDecision scope)
    {
        var current = RequireActor(actor, scope, requireModule: enabled);
        lock (_gate)
        {
            if (!_schedules.TryGetValue(id, out var item) || !IsVisible(item, current, scope)) return null;
            if (item.Enabled == enabled) return Copy(item);
            if (enabled) RequireTargets(item.ProfileIds, current, scope);
            item = item with { Enabled = enabled,
                NextRunMs = enabled ? NextDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), item.IntervalMinutes) : null };
            Commit(item);
            // Disabling stops future admissions. An already admitted read-only
            // batch keeps its own existing task lifetime and authorization checks.
            return Copy(item);
        }
    }

    public bool Delete(string id, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        var current = RequireActor(actor, scope);
        lock (_gate)
        {
            if (!_schedules.TryGetValue(id, out var item) || !IsVisible(item, current, scope)) return false;
            var next = new Dictionary<string, ConfigBackupScheduleSnapshot>(_schedules, StringComparer.Ordinal);
            next.Remove(id);
            Store(next);
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { RunDueSchedules(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
            catch (Exception ex) when (ex is ConfigBackupException or SqliteException or IOException or UnauthorizedAccessException)
            {
                // Never log arbitrary storage/transport exception messages or
                // serialize them into a schedule, where they could contain secrets.
                _logger.LogWarning("Configuration backup schedule tick failed ({FailureType}).", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal void RunDueSchedules(long nowMs)
    {
        lock (_gate)
        {
            if (_storeUnavailable) return;
            foreach (var (id, running) in _running.ToArray())
            {
                // Keep observing the real batch even when current profile scope
                // hides its public view. Never free a slot while it is executing.
                var batch = _backups.GetScheduledBatchStatus(running.BatchId, running.OwnerUserId);
                if (batch is null || batch.State == "RUNNING") continue;
                if (_schedules.TryGetValue(id, out var item))
                    Commit(item with { LastState = batch.State, LastCompletedMs = batch.CompletedMs ?? nowMs,
                        LastBatchId = running.BatchId,
                        LastError = batch.Failed == 0 ? null : SafeError(batch.Items.FirstOrDefault(value => value.Error is not null)?.Error) });
                _backups.AcknowledgeScheduledBatch(running.BatchId, running.OwnerUserId);
                _running.Remove(id);
            }

            if (!_catalog.IsModuleEnabled("config-backups")) return;

            foreach (var item in _schedules.Values.Where(item => item.Enabled && item.NextRunMs <= nowMs)
                         .OrderBy(item => item.NextRunMs).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray())
            {
                if (_running.Count >= MaximumActiveSchedules) break;
                if (_running.ContainsKey(item.Id)) continue;
                // Advance from this actual admission attempt, not the old due
                // time: a week offline causes at most one run, never a catch-up loop.
                var attempted = item with { NextRunMs = NextDue(nowMs, item.IntervalMinutes), LastRunMs = nowMs,
                    LastCompletedMs = null, LastState = "RUNNING", LastError = null, LastBatchId = null };
                Commit(attempted); // Durably claim the period before starting a batch.
                ConfigBackupBatch batch;
                try
                {
                    var actor = CurrentOwner(item.OwnerUserId);
                    var scope = _scopes.Resolve(actor);
                    RequireTargets(item.ProfileIds, actor, scope);
                    // Start captures a new endpoint/scope now; its scheduler
                    // resolves the profile's current credential for this run.
                    batch = _backups.Start(item.ProfileIds, actor, scope, item.ConfigurationKind, retainUntilObserved: true);
                }
                catch (Exception ex) when (ex is ConfigBackupException or TaskScopeRevokedException or SqliteException or IOException or UnauthorizedAccessException)
                {
                    var error = ex switch
                    {
                        ConfigBackupException backup => backup.Code,
                        TaskScopeRevokedException revoked => revoked.Reason,
                        _ => "CONFIG_BACKUP_SCHEDULE_AUTHORITY_UNAVAILABLE"
                    };
                    Commit(attempted with { LastState = "BLOCKED", LastCompletedMs = nowMs, LastError = SafeError(error) });
                    continue;
                }
                _running[item.Id] = new(item.OwnerUserId, batch.Id);
                Commit(attempted with { LastBatchId = batch.Id });
            }
        }
    }

    private WebUserIdentity RequireActor(WebUserIdentity actor, P2B5SiteScopeDecision scope, bool requireModule = false)
    {
        if (actor.Role != "SUPERADMIN")
            throw new ConfigBackupException("CONFIG_BACKUP_PERMISSION_DENIED", "只有 SUPERADMIN 可以管理定时配置备份。", 403);
        if (_storeUnavailable)
            throw new ConfigBackupException("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", "定时备份计划文件无效或不可读，请修复后重启服务；原文件已保留。", 503);
        if (requireModule && !_catalog.IsModuleEnabled("config-backups"))
            throw new ConfigBackupException("CONFIG_BACKUP_MODULE_DISABLED", "配置备份模块已关闭。", 409);
        if (!scope.IsAuthorized)
            throw new ConfigBackupException("CONFIG_BACKUP_SCOPE_DENIED", "当前 Site 权限不可用。", 403);
        var current = CurrentOwner(actor.UserId);
        if (!_scopes.Resolve(current).IsAuthorized)
            throw new ConfigBackupException("CONFIG_BACKUP_SCOPE_DENIED", "当前 Site 权限不可用。", 403);
        return current;
    }

    private WebUserIdentity CurrentOwner(string userId)
    {
        var actor = _catalog.GetActiveTaskActor(userId);
        return actor is { Role: "SUPERADMIN" } ? actor :
            throw new ConfigBackupException("CONFIG_BACKUP_SCHEDULE_OWNER_REVOKED", "计划所有者已停用或不再具备执行权限。", 403);
    }

    private void RequireTargets(IReadOnlyList<string> profiles, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        if (!scope.IsAuthorized)
            throw new ConfigBackupException("CONFIG_BACKUP_SCOPE_DENIED", "当前 Site 权限不可用。", 403);
        foreach (var profile in profiles)
        {
            if (!_catalog.H3cProfileIsInScope(profile, scope))
                throw new ConfigBackupException("CONFIG_BACKUP_TARGET_NOT_VISIBLE", "所选设备不在当前 Site 权限范围内。", 404);
            var target = _catalog.GetTaskScopeTarget(profile, scope);
            if (target is null)
                throw new ConfigBackupException("CONFIG_BACKUP_TARGET_NOT_VISIBLE", "设备已停用或关联关系不再有效。", 404);
            if (_catalog.GetNetmikoVendor(profile) != "h3c" ||
                string.Equals(target.DeviceType, "FIT_AP", StringComparison.OrdinalIgnoreCase))
                throw new ConfigBackupException("CONFIG_BACKUP_TARGET_UNSUPPORTED", "当前配置备份仅支持独立管理的 H3C 设备。", 409);
            _ = _taskScopes.CaptureDefinition(profile, actor.UserId);
        }
    }

    private bool IsVisible(ConfigBackupScheduleSnapshot item, WebUserIdentity actor, P2B5SiteScopeDecision scope) =>
        item.OwnerUserId == actor.UserId && scope.IsAuthorized &&
        (scope.IsGlobal || item.ProfileIds.All(profile => _catalog.H3cProfileIsInScope(profile, scope)));

    private static ConfigBackupScheduleSnapshot Copy(ConfigBackupScheduleSnapshot item) =>
        item with { ProfileIds = item.ProfileIds.ToArray() };

    private static long NextDue(long nowMs, int intervalMinutes) => checked(nowMs + intervalMinutes * 60_000L);
    private static string SafeError(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') ? value : "CONFIG_BACKUP_SCHEDULE_RUN_FAILED";
    private static bool IsSafeId(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

    private void Commit(ConfigBackupScheduleSnapshot item)
    {
        var next = new Dictionary<string, ConfigBackupScheduleSnapshot>(_schedules, StringComparer.Ordinal) { [item.Id] = item };
        Store(next);
    }

    private void Store(Dictionary<string, ConfigBackupScheduleSnapshot> next)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoreDocument(1, next.Values.ToArray()), JsonOptions);
            if (bytes.Length > MaximumStoreBytes) throw new InvalidDataException("Schedule store exceeds limit.");
            CheckStorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var part = _storePath + ".part";
            if (File.Exists(part) && IsReparse(part)) throw new InvalidDataException("Schedule part is a reparse point.");
            using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(part, _storePath, overwrite: true);
            _schedules = next;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new ConfigBackupException("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", "定时备份计划无法安全保存，请检查数据目录。", 503); }
    }

    private void Load()
    {
        try
        {
            CheckStorePath();
            if (!File.Exists(_storePath)) return;
            using var stream = new FileStream(_storePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > MaximumStoreBytes) throw new InvalidDataException("Schedule store exceeds limit.");
            var document = JsonSerializer.Deserialize<StoreDocument>(stream, JsonOptions);
            if (document is not { Version: 1, Schedules: not null } || document.Schedules.Count > MaximumSchedules)
                throw new InvalidDataException("Invalid schedule document.");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var recovered = false;
            foreach (var stored in document.Schedules)
            {
                if (!IsValid(stored) || _schedules.ContainsKey(stored.Id)) throw new InvalidDataException("Invalid schedule record.");
                var item = stored;
                if (item.LastState == "RUNNING")
                {
                    recovered = true;
                    item = item with { LastState = "INTERRUPTED", LastError = "CONFIG_BACKUP_SCHEDULE_RESTARTED",
                        LastCompletedMs = now,
                        NextRunMs = item.Enabled && item.NextRunMs <= now ? NextDue(now, item.IntervalMinutes) : item.NextRunMs };
                }
                _schedules.Add(item.Id, item);
            }
            stream.Dispose();
            if (recovered) Store(new(_schedules, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { throw new ConfigBackupException("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", "定时备份计划文件无效或不可读；原文件已保留。", 503); }
    }

    private static bool IsValid(ConfigBackupScheduleSnapshot? item) => item is not null && IsSafeId(item.Id) &&
        IsSafeId(item.OwnerUserId) && item.ProfileIds is { Count: >= 1 and <= MaximumProfiles } &&
        item.ProfileIds.All(IsSafeId) && item.ProfileIds.Distinct(StringComparer.Ordinal).Count() == item.ProfileIds.Count &&
        ConfigBackupKinds.IsScheduled(item.ConfigurationKind) &&
        item.IntervalMinutes is >= MinimumIntervalMinutes and <= MaximumIntervalMinutes && item.CreatedMs > 0 &&
        (item.Enabled ? item.NextRunMs > 0 : item.NextRunMs is null) &&
        item.LastState is "NEVER_RUN" or "RUNNING" or "SUCCEEDED" or "PARTIAL" or "BLOCKED" or "INTERRUPTED" &&
        (item.LastError is null || SafeError(item.LastError) == item.LastError) &&
        (item.LastBatchId is null || IsSafeId(item.LastBatchId));

    private void CheckStorePath()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(_storePath)!);
        if ((directory.Exists && IsReparse(directory.FullName)) ||
            (directory.Parent is { Exists: true } parent && IsReparse(parent.FullName)) ||
            (File.Exists(_storePath) && IsReparse(_storePath)))
            throw new InvalidDataException("Schedule storage cannot be a reparse point.");
    }

    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private sealed record RunningBatch(string OwnerUserId, string BatchId);
    private sealed record StoreDocument(int Version, IReadOnlyList<ConfigBackupScheduleSnapshot> Schedules);
}
