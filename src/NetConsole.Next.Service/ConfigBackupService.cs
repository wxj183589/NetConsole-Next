using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record ConfigBackupRaw(
    string RunId,
    string ProfileId,
    string Command,
    string Output,
    string OutputSha256,
    long ByteCount,
    long CapturedMs);

internal sealed record ConfigBackupSnapshot(
    string Id,
    string ProfileId,
    string DeviceId,
    string SiteId,
    int Version,
    string State,
    long CreatedMs,
    long? CapturedMs,
    string? Sha256,
    long ByteCount,
    string? TaskRunId,
    string? Error,
    bool CanDownload,
    string? DownloadName,
    string ConfigurationKind = ConfigBackupKinds.Running,
    string? Command = null)
{
    // Existing snapshots retain only sanitized output, including old manifests.
    public string ContentClass => ConfigurationKind == ConfigBackupKinds.StartupInfo
        ? "CONFIGURATION_REFERENCE" : "REDACTED_CONFIGURATION";
    public bool Restorable => false;
}

internal sealed record ConfigBackupBatchItem(
    string ProfileId,
    string State,
    string? SnapshotId,
    string? Error);

internal sealed record ConfigBackupBatch(
    string Id,
    string ActorUserId,
    string State,
    long CreatedMs,
    long? CompletedMs,
    int Total,
    int Completed,
    int Succeeded,
    int Failed,
    IReadOnlyList<ConfigBackupBatchItem> Items,
    string ConfigurationKind = ConfigBackupKinds.Running);

internal sealed record ConfigBackupDiff(
    string BaseId,
    string TargetId,
    string ProfileId,
    bool Identical,
    int AddedLines,
    int RemovedLines,
    int ChangedLines,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    string ConfigurationKind = ConfigBackupKinds.Running);

internal static class ConfigBackupKinds
{
    public const string Running = "RUNNING";
    public const string CompleteRunning = "COMPLETE_RUNNING";
    public const string Saved = "SAVED";
    public const string StartupInfo = "STARTUP_INFO";
    public const string StartupBody = "STARTUP_BODY";

    public static bool IsSupported(string? kind) => kind is Running or Saved or StartupInfo or StartupBody;
    public static bool IsScheduled(string? kind) => kind is Running or Saved or StartupInfo or CompleteRunning;
    public static string CommandFor(string kind) => kind switch
    {
        Running or CompleteRunning => H3cCommandPolicy.DisplayCurrentConfiguration,
        Saved => H3cCommandPolicy.DisplaySavedConfiguration,
        StartupInfo => H3cCommandPolicy.DisplayStartup,
        _ => throw new ConfigBackupException("CONFIG_BACKUP_KIND_UNSUPPORTED", "请选择运行配置、保存配置或启动配置指向。", 400)
    };

}

internal sealed record StartupConfigurationReference(string FileReference, string ReadCommand,
    bool BodyReadSupported, string? RestoreError);

internal static partial class StartupConfigurationSupport
{
    [GeneratedRegex(@"\bstartup\s+saved-configuration\s+file\s*:\s*(?<path>[^\s\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileReferencePattern();

    public static StartupConfigurationReference Inspect(string? output)
    {
        foreach (Match match in FileReferencePattern().Matches(output ?? string.Empty))
        {
            var reference = match.Groups["path"].Value.Trim().TrimEnd(',', ';', '.');
            if (reference.Equals("NULL", StringComparison.OrdinalIgnoreCase)) continue;
            if (H3cCommandPolicy.TryCreateStartupFileReadCommand(reference, out var command))
                return new(reference, command, true, "CONFIG_RESTORE_UNSUPPORTED");
        }
        return new(string.Empty, string.Empty, false, "CONFIG_BACKUP_STARTUP_FILE_UNAVAILABLE");
    }
}

internal static class ConfigBackupSanitizer
{
    private static readonly Regex SecretLine = new(
        @"^\s*(?<prefix>.*?\b(?:password|passwd|passphrase|pass-phrase|pre-shared-key|community|private-key|secret|credential|authorization|token|psk)\b).*?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex PrivateKeyBlock = new(
        @"-----BEGIN [^\r\n]*PRIVATE KEY-----[\s\S]*?(?:-----END [^\r\n]*PRIVATE KEY-----|\z)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string Sanitize(string command, string output) =>
        H3cCommandPolicy.IsConfigurationCommand(command)
            ? string.Join('\n', PrivateKeyBlock.Replace(output, "<REDACTED>")
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n').Select(line => SecretLine.IsMatch(line)
                    ? SecretLine.Replace(line, "${prefix} <REDACTED>")
                    : line))
            : output;
}

internal sealed class ConfigBackupService(
    Catalog catalog,
    BoundedCollectionScheduler scheduler,
    TaskSiteScopeAuthorizer taskScopes,
    RuntimeSettings settings,
    SimulatorConfigurationService? completeConfigurations = null)
{
    private const int MaxProfilesPerBatch = 32;
    private const int MaxHistory = 100;
    private const int MaxDiffLines = 256;
    private const int MaxDiffCells = 1_048_576;
    internal const int MaximumCompletedBatchMetadata = 128;
    private readonly object _batchGate = new();
    private readonly ConcurrentDictionary<string, BatchState> _batches = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scheduledPins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileGates = new(StringComparer.Ordinal);
    private readonly string _root = Path.Combine(settings.DataRoot, "backup", "config");
    private readonly ConfigBackupBatchHistoryStore _batchHistory = new(settings);

    public ConfigBackupBatch Start(IReadOnlyList<string> profileIds, WebUserIdentity actor,
        P2B5SiteScopeDecision scope, string? configurationKind = null, bool retainUntilObserved = false)
    {
        if (actor.Role != "SUPERADMIN") throw new ConfigBackupException("CONFIG_BACKUP_PERMISSION_DENIED", "只有 SUPERADMIN 可以执行配置备份。", 403);
        if (!scope.IsAuthorized) throw new ConfigBackupException("CONFIG_BACKUP_SCOPE_DENIED", scope.Detail, 403);
        var kind = configurationKind ?? ConfigBackupKinds.Running;
        _ = ConfigBackupKinds.CommandFor(kind);
        var normalized = (profileIds ?? Array.Empty<string>()).Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (normalized.Length is < 1 or > MaxProfilesPerBatch)
            throw new ConfigBackupException("CONFIG_BACKUP_RESOURCE_LIMIT", $"一次最多选择 {MaxProfilesPerBatch} 台设备。", 413);

        var items = new List<BatchItemState>(normalized.Length);
        foreach (var profileId in normalized)
        {
            if (!catalog.H3cProfileIsInScope(profileId, scope))
                throw new ConfigBackupException("CONFIG_BACKUP_TARGET_NOT_VISIBLE", "所选设备不在当前 Site 权限范围内。", 404);
            var snapshot = taskScopes.CaptureDefinition(profileId, actor.UserId);
            RequireSupportedTarget(profileId);
            ValidateCompleteTarget(profileId, actor, scope, kind);
            items.Add(new(profileId, snapshot, "QUEUED", null, null));
        }

        var id = Guid.NewGuid().ToString("N");
        var batch = new BatchState(id, actor.UserId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), items, kind);
        lock (_batchGate)
        {
            _batchHistory.RequireAvailable();
            _batches[id] = batch;
            if (retainUntilObserved) _scheduledPins.Add(id);
            PruneCompletedBatches();
        }
        var admitted = batch.Snapshot();
        _ = Task.Run(() => RunBatchAsync(batch), CancellationToken.None);
        return admitted;
    }

    // Startup body collection is deliberately separate from the three regular
    // backup kinds: the device first reports the file reference, then the
    // service reads that exact validated reference through the same read-only
    // task/Raw/Site scope pipeline.
    public ConfigBackupBatch StartStartupBody(IReadOnlyList<string> profileIds, WebUserIdentity actor,
        P2B5SiteScopeDecision scope)
    {
        if (actor.Role != "SUPERADMIN") throw new ConfigBackupException("CONFIG_BACKUP_PERMISSION_DENIED", "只有 SUPERADMIN 可以执行配置备份。", 403);
        if (!scope.IsAuthorized) throw new ConfigBackupException("CONFIG_BACKUP_SCOPE_DENIED", scope.Detail, 403);
        var normalized = (profileIds ?? Array.Empty<string>()).Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (normalized.Length is < 1 or > MaxProfilesPerBatch)
            throw new ConfigBackupException("CONFIG_BACKUP_RESOURCE_LIMIT", $"一次最多选择 {MaxProfilesPerBatch} 台设备。", 413);
        var items = new List<BatchItemState>(normalized.Length);
        foreach (var profileId in normalized)
        {
            if (!catalog.H3cProfileIsInScope(profileId, scope))
                throw new ConfigBackupException("CONFIG_BACKUP_TARGET_NOT_VISIBLE", "所选设备不在当前 Site 权限范围内。", 404);
            var snapshot = taskScopes.CaptureDefinition(profileId, actor.UserId);
            RequireSupportedTarget(profileId);
            items.Add(new(profileId, snapshot, "QUEUED", null, null));
        }
        var id = Guid.NewGuid().ToString("N");
        var batch = new BatchState(id, actor.UserId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), items, ConfigBackupKinds.StartupBody);
        lock (_batchGate) { _batchHistory.RequireAvailable(); _batches[id] = batch; PruneCompletedBatches(); }
        var admitted = batch.Snapshot();
        _ = Task.Run(() => RunStartupBodyBatchAsync(batch), CancellationToken.None);
        return admitted;
    }

    public StartupConfigurationReference? GetStartupReference(string id, P2B5SiteScopeDecision scope)
    {
        var snapshot = Get(id, scope);
        if (snapshot is null || snapshot.ConfigurationKind != ConfigBackupKinds.StartupInfo) return null;
        var opened = OpenDownload(id, scope);
        if (opened is null) return null;
        using var stream = opened.Value.Stream;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return StartupConfigurationSupport.Inspect(reader.ReadToEnd());
    }

    internal void ValidateCompleteTarget(string profileId, WebUserIdentity actor,
        P2B5SiteScopeDecision scope, string kind)
    {
        if (kind != ConfigBackupKinds.CompleteRunning) return;
        if (completeConfigurations is null)
            throw new ConfigBackupException("CONFIG_COMPLETE_SERVICE_UNAVAILABLE", "完整配置服务不可用。", 503);
        try { completeConfigurations.ValidateCaptureTarget(profileId, actor, scope); }
        catch (ConfigRestoreException ex) { throw new ConfigBackupException(ex.Code, "完整配置目标不符合授权条件。", ex.StatusCode); }
    }

    private void RequireSupportedTarget(string profileId)
    {
        // The shared Netmiko profile catalog also contains other vendors. This
        // backup workflow currently has only a verified standalone H3C command
        // policy. FIT-AP collection remains owned by its managing AC.
        var target = catalog.GetTaskScopeTarget(profileId, P2B5SiteScopeDecision.Global);
        if (catalog.GetNetmikoVendor(profileId) != "h3c" ||
            string.Equals(target?.DeviceType, "FIT_AP", StringComparison.OrdinalIgnoreCase))
            throw new ConfigBackupException("CONFIG_BACKUP_TARGET_UNSUPPORTED", "当前配置备份仅支持独立管理的 H3C 设备。", 409);
    }

    public IReadOnlyList<ConfigBackupBatch> ListCompleteBatches(WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        if (actor.Role != "SUPERADMIN" || !scope.IsAuthorized || catalog.GetActiveTaskActor(actor.UserId)?.Role != "SUPERADMIN")
            throw new ConfigBackupException("CONFIG_BACKUP_PERMISSION_DENIED", "需要有效的 SUPERADMIN Site 权限。", 403);
        lock (_batchGate)
        {
            return _batches.Keys.Concat(_batchHistory.Ids()).Distinct(StringComparer.Ordinal)
                .Select(id => GetBatch(id, actor, scope))
                .OfType<ConfigBackupBatch>().Where(item => item.ConfigurationKind == ConfigBackupKinds.CompleteRunning)
                .OrderByDescending(item => item.CreatedMs).ThenBy(item => item.Id, StringComparer.Ordinal)
                .Take(50).ToArray();
        }
    }

    public ConfigBackupBatch? GetBatch(string id, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        if (actor.Role != "SUPERADMIN" || !scope.IsAuthorized) return null;
        var current = catalog.GetActiveTaskActor(actor.UserId);
        if (current?.Role != "SUPERADMIN") return null;
        lock (_batchGate)
        {
            _batchHistory.RequireAvailable();
            var batch = _batches.TryGetValue(id, out var live) ? live.Snapshot() : _batchHistory.Get(id);
            if (batch is null || batch.ActorUserId != actor.UserId ||
                batch.Items.Any(item => !catalog.H3cProfileIsInScope(item.ProfileId, scope))) return null;
            return batch;
        }
    }

    // Internal scheduling metadata only. A removed profile can hide the public
    // scoped view while its already admitted batch still needs to finish. This
    // does not expose configuration output, snapshots, credentials or a new API.
    internal ConfigBackupBatch? GetScheduledBatchStatus(string id, string ownerUserId)
    {
        lock (_batchGate)
        {
            _batchHistory.RequireAvailable();
            return _batches.TryGetValue(id, out var batch) && batch.ActorUserId == ownerUserId ? batch.Snapshot() : null;
        }
    }

    internal bool AcknowledgeScheduledBatch(string id, string ownerUserId)
    {
        lock (_batchGate)
        {
            if (!_batches.TryGetValue(id, out var batch) || batch.ActorUserId != ownerUserId ||
                batch.CompletedMs is null || !_scheduledPins.Remove(id)) return false;
            PruneCompletedBatches();
            return true;
        }
    }

    private void PruneCompletedBatches()
    {
        // Called under _batchGate. Never prune running work or a schedule whose
        // terminal result has not yet been durably observed by its scheduler.
        foreach (var old in _batches.Values.Where(item => item.CompletedMs is not null && !_scheduledPins.Contains(item.Id))
                     .OrderByDescending(item => item.CompletedMs).ThenByDescending(item => item.CreatedMs)
                     .ThenBy(item => item.Id, StringComparer.Ordinal).Skip(MaximumCompletedBatchMetadata).ToArray())
            _batches.TryRemove(old.Id, out _);
    }

    public IReadOnlyList<ConfigBackupSnapshot> List(string profileId, P2B5SiteScopeDecision scope,
        string? configurationKind = null)
    {
        if (!scope.IsAuthorized || !catalog.H3cProfileIsInScope(profileId, scope)) return Array.Empty<ConfigBackupSnapshot>();
        if (configurationKind is not null && configurationKind != ConfigBackupKinds.StartupBody)
            _ = ConfigBackupKinds.CommandFor(configurationKind);
        if (configurationKind == ConfigBackupKinds.StartupBody) { }
        else if (configurationKind is not null && !ConfigBackupKinds.IsSupported(configurationKind))
            _ = ConfigBackupKinds.CommandFor(configurationKind);
        var directory = ProfileDirectory(profileId);
        if (!Directory.Exists(directory) || !IsLocalBackupPath(directory)) return Array.Empty<ConfigBackupSnapshot>();
        var snapshots = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(IsLocalBackupPath)
            .Select(ReadSnapshot)
            .Where(item => item is not null && IsValidSnapshot(item) && item.ProfileId == profileId &&
                item.State == "SUCCEEDED" && (configurationKind is null || item.ConfigurationKind == configurationKind))
            .Cast<ConfigBackupSnapshot>()
            .OrderByDescending(item => item.CapturedMs ?? item.CreatedMs)
            .ThenByDescending(item => item.Version)
            .Take(MaxHistory)
            .ToArray();
        return snapshots.Select(item => item with { CanDownload = true }).ToArray();
    }

    public ConfigBackupSnapshot? Get(string id, P2B5SiteScopeDecision scope)
    {
        var snapshot = FindSnapshot(id);
        if (snapshot is null || !IsValidSnapshot(snapshot) || !scope.IsAuthorized ||
            !catalog.H3cProfileIsInScope(snapshot.ProfileId, scope))
            return null;
        var directory = ProfileDirectory(snapshot.ProfileId);
        if (!Directory.Exists(directory) || !IsLocalBackupPath(directory)) return null;
        return snapshot with { CanDownload = true };
    }

    public (FileStream Stream, string Name)? OpenDownload(string id, P2B5SiteScopeDecision scope)
    {
        var snapshot = Get(id, scope);
        if (snapshot is null || snapshot.DownloadName is null) return null;
        var stream = OpenVerified(snapshot);
        return stream is null ? null : (stream, snapshot.DownloadName);
    }

    public ConfigBackupDiff? Diff(string baseId, string targetId, P2B5SiteScopeDecision scope)
    {
        var baseline = Get(baseId, scope);
        var target = Get(targetId, scope);
        if (baseline is null || target is null || baseline.ProfileId != target.ProfileId ||
            baseline.ConfigurationKind != target.ConfigurationKind) return null;
        using var baseStream = OpenVerified(baseline);
        using var targetStream = OpenVerified(target);
        if (baseStream is null || targetStream is null) return null;
        var baseLines = ReadLines(baseStream);
        var targetLines = ReadLines(targetStream);
        var start = 0;
        while (start < baseLines.Count && start < targetLines.Count &&
               string.Equals(baseLines[start], targetLines[start], StringComparison.Ordinal)) start++;
        var baseEnd = baseLines.Count;
        var targetEnd = targetLines.Count;
        while (baseEnd > start && targetEnd > start &&
               string.Equals(baseLines[baseEnd - 1], targetLines[targetEnd - 1], StringComparison.Ordinal))
        {
            baseEnd--;
            targetEnd--;
        }

        var baseCount = baseEnd - start;
        var targetCount = targetEnd - start;
        var added = new List<string>(MaxDiffLines);
        var removed = new List<string>(MaxDiffLines);
        var addedCount = 0;
        var removedCount = 0;
        void Add(string line)
        {
            addedCount++;
            if (added.Count < MaxDiffLines) added.Add(line);
        }
        void Remove(string line)
        {
            removedCount++;
            if (removed.Count < MaxDiffLines) removed.Add(line);
        }

        if (baseCount == 0 || targetCount == 0 || (long)(baseCount + 1) * (targetCount + 1) > MaxDiffCells)
        {
            // Large unmatched regions are conservatively replaced in sequence. They
            // need not be a minimal edit, but never hide reordering or duplicates.
            for (var index = start; index < baseEnd; index++) Remove(baseLines[index]);
            for (var index = start; index < targetEnd; index++) Add(targetLines[index]);
        }
        else
        {
            // Shared prefix/suffix keep routine edits small; the cell budget bounds
            // the remaining LCS table and comparison work for unrelated configs.
            var lengths = new int[baseCount + 1, targetCount + 1];
            for (var i = baseCount - 1; i >= 0; i--)
                for (var j = targetCount - 1; j >= 0; j--)
                    lengths[i, j] = string.Equals(baseLines[start + i], targetLines[start + j], StringComparison.Ordinal)
                        ? lengths[i + 1, j + 1] + 1
                        : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            var baseIndex = 0;
            var targetIndex = 0;
            while (baseIndex < baseCount || targetIndex < targetCount)
            {
                if (baseIndex < baseCount && targetIndex < targetCount &&
                    string.Equals(baseLines[start + baseIndex], targetLines[start + targetIndex], StringComparison.Ordinal))
                {
                    baseIndex++;
                    targetIndex++;
                }
                else if (baseIndex < baseCount && (targetIndex == targetCount ||
                             lengths[baseIndex + 1, targetIndex] >= lengths[baseIndex, targetIndex + 1]))
                    Remove(baseLines[start + baseIndex++]);
                else
                    Add(targetLines[start + targetIndex++]);
            }
        }

        // Counts describe the complete edit; each ordered preview is capped.
        // ChangedLines pairs removals with additions and is not added to either total.
        return new(baseId, targetId, target.ProfileId, addedCount == 0 && removedCount == 0,
            addedCount, removedCount, Math.Min(addedCount, removedCount), added, removed, target.ConfigurationKind);
    }

    private async Task RunStartupBodyBatchAsync(BatchState batch)
    {
        using var parallel = new SemaphoreSlim(Math.Min(4, batch.Items.Count), Math.Min(4, batch.Items.Count));
        var tasks = batch.Items.Select(async item =>
        {
            await parallel.WaitAsync().ConfigureAwait(false);
            try { await RunStartupBodyItemAsync(batch, item).ConfigureAwait(false); }
            finally { parallel.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        CompleteBatch(batch);
    }

    private async Task RunStartupBodyItemAsync(BatchState batch, BatchItemState item)
    {
        var gate = _profileGates.GetOrAdd(item.ProfileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireSupportedTarget(item.ProfileId);
            item.State = "RUNNING";
            var prefix = $"config-startup-body-{batch.Id[..16]}-{Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(item.ProfileId))).ToLowerInvariant()[..12]}";
            var infoRun = await ScheduleAndWaitAsync(batch, item, prefix + "-info", H3cCommandPolicy.DisplayStartup).ConfigureAwait(false);
            if (infoRun is null) return;
            var infoRaw = catalog.ReadCompleteH3cTaskRaw(infoRun);
            if (infoRaw is null)
            {
                FailStartupBody(batch, item, "CONFIG_BACKUP_RAW_UNAVAILABLE");
                return;
            }
            var reference = StartupConfigurationSupport.Inspect(infoRaw.Output);
            if (!reference.BodyReadSupported)
            {
                FailStartupBody(batch, item, reference.RestoreError ?? "CONFIG_BACKUP_STARTUP_FILE_UNAVAILABLE");
                return;
            }
            var bodyRun = await ScheduleAndWaitAsync(batch, item, prefix + "-body", reference.ReadCommand).ConfigureAwait(false);
            if (bodyRun is null) return;
            var bodyRaw = catalog.ReadCompleteH3cTaskRaw(bodyRun);
            if (bodyRaw is null)
            {
                FailStartupBody(batch, item, "CONFIG_BACKUP_RAW_UNAVAILABLE");
                return;
            }
            var snapshot = WriteSnapshot(item.ProfileId, item.Scope.DeviceId, item.Scope.SiteId, bodyRun, bodyRaw,
                ConfigBackupKinds.StartupBody, reference.ReadCommand);
            item.SnapshotId = snapshot.Id;
            item.State = "SUCCEEDED";
            catalog.TryRecordDeviceAuditEvent(batch.ActorUserId, "CONFIG_BACKUP", "SUCCEEDED", item.ProfileId, bodyRun,
                Array.Empty<string>(), $"snapshot={snapshot.Id};kind={snapshot.ConfigurationKind}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailStartupBody(batch, item, ex switch
            {
                TaskScopeRevokedException scope => scope.Reason,
                ConfigBackupException backup => backup.Code,
                _ => ex.GetType().Name
            });
        }
        finally { gate.Release(); }
    }

    private async Task<string?> ScheduleAndWaitAsync(BatchState batch, BatchItemState item, string definitionId, string command)
    {
        catalog.CreateTaskDefinition(definitionId, "H3C_SSH_READONLY", item.ProfileId, command,
            TimeSpan.FromMinutes(10), scopeSnapshot: item.Scope);
        if (!scheduler.TryScheduleDefinition(definitionId, out var runId, batch.ActorUserId))
        {
            FailStartupBody(batch, item, "CONFIG_BACKUP_QUEUE_FULL");
            return null;
        }
        item.TaskRunId = runId;
        TaskRunSnapshot? run;
        while (true)
        {
            await Task.Delay(100).ConfigureAwait(false);
            run = catalog.GetTaskRun(runId);
            if (run?.State is "SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED") break;
        }
        if (run?.State == "SUCCEEDED") return runId;
        FailStartupBody(batch, item, run?.FailureReason ?? "CONFIG_BACKUP_COLLECTION_FAILED");
        return null;
    }

    private void FailStartupBody(BatchState batch, BatchItemState item, string error)
    {
        item.State = "FAILED";
        item.Error = error;
        catalog.TryRecordDeviceAuditEvent(batch.ActorUserId, "CONFIG_BACKUP", "FAILED", item.ProfileId,
            item.TaskRunId, Array.Empty<string>(), error);
    }

    private async Task RunBatchAsync(BatchState batch)
    {
        using var parallel = new SemaphoreSlim(Math.Min(4, batch.Items.Count), Math.Min(4, batch.Items.Count));
        var tasks = batch.Items.Select(async item =>
        {
            await parallel.WaitAsync().ConfigureAwait(false);
            try { await RunItemAsync(batch, item).ConfigureAwait(false); }
            finally { parallel.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        CompleteBatch(batch);
    }

    private void CompleteBatch(BatchState batch)
    {
        lock (_batchGate)
        {
            batch.Complete();
            // Public readers take this same gate, so completion is never
            // announced before the bounded metadata document is durable.
            try { _batchHistory.Store(batch.Snapshot()); }
            catch (ConfigBackupException) { /* Subsequent batch reads/admissions explicitly return 503. */ }
            PruneCompletedBatches();
        }
    }

    private async Task RunItemAsync(BatchState batch, BatchItemState item)
    {
        var gate = _profileGates.GetOrAdd(item.ProfileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireSupportedTarget(item.ProfileId);
            item.State = "RUNNING";
            if (batch.ConfigurationKind == ConfigBackupKinds.CompleteRunning)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var actor = catalog.GetActiveTaskActor(batch.ActorUserId)
                    ?? throw new TaskScopeRevokedException("TASK_ACTOR_REVOKED_ADMISSION");
                var backup = await completeConfigurations!.CaptureFrozenAsync(item.ProfileId, actor,
                    P2B5SiteScopeDecision.Global, item.Scope, deadline.Token).ConfigureAwait(false);
                item.TaskRunId = backup.TaskId;
                item.SnapshotId = backup.Id;
                item.State = "SUCCEEDED";
                return;
            }

            var definitionId = $"config-backup-{batch.Id[..16]}-{Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(item.ProfileId))).ToLowerInvariant()[..12]}";
            catalog.CreateTaskDefinition(definitionId, "H3C_SSH_READONLY",
                item.ProfileId, ConfigBackupKinds.CommandFor(batch.ConfigurationKind), TimeSpan.FromMinutes(10),
                scopeSnapshot: item.Scope);
            if (!scheduler.TryScheduleDefinition(definitionId, out var runId, batch.ActorUserId))
            {
                item.State = "FAILED";
                item.Error = "CONFIG_BACKUP_QUEUE_FULL";
                catalog.TryRecordDeviceAuditEvent(batch.ActorUserId, "CONFIG_BACKUP", "FAILED", item.ProfileId, null,
                    Array.Empty<string>(), item.Error);
                return;
            }
            item.TaskRunId = runId;
            TaskRunSnapshot? run;
            while (true)
            {
                await Task.Delay(100).ConfigureAwait(false);
                run = catalog.GetTaskRun(runId);
                if (run?.State is "SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED") break;
            }
            if (run?.State != "SUCCEEDED")
            {
                item.State = "FAILED";
                item.Error = run?.FailureReason ?? "CONFIG_BACKUP_COLLECTION_FAILED";
                catalog.TryRecordDeviceAuditEvent(batch.ActorUserId, "CONFIG_BACKUP", "FAILED", item.ProfileId, runId,
                    Array.Empty<string>(), item.Error);
                return;
            }
            var raw = catalog.ReadCompleteH3cTaskRaw(runId);
            if (raw is null)
            {
                item.State = "FAILED";
                item.Error = "CONFIG_BACKUP_RAW_UNAVAILABLE";
                catalog.TryRecordDeviceAuditEvent(batch.ActorUserId, "CONFIG_BACKUP", "FAILED", item.ProfileId, runId,
                    Array.Empty<string>(), item.Error);
                return;
            }
            var snapshot = WriteSnapshot(item.ProfileId, item.Scope.DeviceId, item.Scope.SiteId, runId, raw,
                batch.ConfigurationKind);
            item.SnapshotId = snapshot.Id;
            item.State = "SUCCEEDED";
            catalog.TryRecordDeviceAuditEvent(batch.ActorUserId, "CONFIG_BACKUP", "SUCCEEDED", item.ProfileId, runId,
                Array.Empty<string>(), $"snapshot={snapshot.Id};kind={snapshot.ConfigurationKind};version={snapshot.Version}");
        }
        catch (Exception ex)
        {
            item.State = "FAILED";
            item.Error = ex switch
            {
                TaskScopeRevokedException scope => scope.Reason,
                ConfigBackupException backup => backup.Code,
                ConfigRestoreException restore => restore.Code,
                OperationCanceledException => "CONFIG_BACKUP_TIMED_OUT",
                _ => ex.GetType().Name
            };
            catalog.TryRecordDeviceAuditEvent(batch.ActorUserId, "CONFIG_BACKUP", "FAILED", item.ProfileId,
                item.TaskRunId, Array.Empty<string>(), item.Error);
        }
        finally { gate.Release(); }
    }

    private ConfigBackupSnapshot WriteSnapshot(string profileId, string deviceId, string siteId,
        string runId, ConfigBackupRaw raw, string configurationKind, string? commandOverride = null)
    {
        var command = commandOverride ?? ConfigBackupKinds.CommandFor(configurationKind);
        if (configurationKind == ConfigBackupKinds.StartupBody && !H3cCommandPolicy.IsStartupFileReadCommand(command))
            throw new ConfigBackupException("CONFIG_BACKUP_STARTUP_COMMAND_INVALID", "启动文件读取命令不在安全范围内。", 409);
        if (raw.Command != command || raw.ProfileId != profileId || raw.RunId != runId)
            throw new ConfigBackupException("CONFIG_BACKUP_RAW_MISMATCH", "配置采集来源不一致，未生成备份。", 409);
        var transcript = CliTranscriptParser.Parse(raw.Output, command, completedExec: true);
        if (transcript.Status is CliTranscriptStatus.Unsupported or CliTranscriptStatus.PermissionDenied or
            CliTranscriptStatus.Paged or CliTranscriptStatus.Empty ||
            (transcript.Status == CliTranscriptStatus.Incomplete && transcript.Prompt.Length > 0) ||
            string.IsNullOrWhiteSpace(transcript.Output))
            throw new ConfigBackupException("CONFIG_BACKUP_OUTPUT_INCOMPLETE", "配置命令未返回完整有效内容，未生成备份。", 409);
        if (configurationKind == ConfigBackupKinds.Saved && Regex.IsMatch(transcript.Output,
            @"(?im)^\s*(?:(?:%|Error:|Info:)\s*)?(?:The\s+)?(?:saved\s+|startup\s+|next\s+)?(?:configuration|config)(?:\s+file)?\s+(?:does\s+not\s+exist|doesn't\s+exist|is\s+(?:not\s+(?:found|exist)|empty))\b|^\s*(?:No\s+(?:saved\s+|startup\s+)?configuration(?:\s+file)?\b|.*(?:配置文件不存在|没有保存的配置))",
            RegexOptions.CultureInvariant))
            throw new ConfigBackupException("CONFIG_BACKUP_SAVED_UNAVAILABLE", "设备没有可读取的保存配置，未生成备份。", 409);
        var directory = ProfileDirectory(profileId);
        EnsureBackupDirectoryIsLocal(directory);
        Directory.CreateDirectory(directory);
        EnsureBackupDirectoryIsLocal(directory);
        var version = Directory.EnumerateFiles(directory, "*.json")
            .Select(ReadSnapshot)
            .Where(item => item is not null && IsValidSnapshot(item) && item.ProfileId == profileId &&
                item.State == "SUCCEEDED" && item.ConfigurationKind == configurationKind)
            .Select(item => item!.Version).DefaultIfEmpty(0).Max() + 1;
        var id = Guid.NewGuid().ToString("N");
        var fileName = $"{id}.{configurationKind.ToLowerInvariant()}.cfg";
        var path = Path.Combine(directory, fileName);
        var bytes = Encoding.UTF8.GetBytes(ConfigBackupSanitizer.Sanitize(command, raw.Output));
        if (bytes.Length is < 1 or > H3cRawOutput.MaximumBytes)
            throw new InvalidDataException("Configuration backup exceeds the bounded Raw limit.");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var part = path + ".part";
        File.WriteAllBytes(part, bytes);
        File.Move(part, path, true);
        var snapshot = new ConfigBackupSnapshot(id, profileId, deviceId, siteId, version, "SUCCEEDED",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), raw.CapturedMs, sha, bytes.Length, runId, null, true, fileName,
            configurationKind, command);
        var manifest = Path.Combine(directory, id + ".json");
        var manifestPart = manifest + ".part";
        File.WriteAllText(manifestPart, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.Move(manifestPart, manifest, true);
        return snapshot;
    }

    private void EnsureBackupDirectoryIsLocal(string directory)
    {
        var root = Path.GetFullPath(settings.DataRoot);
        var current = Path.GetFullPath(directory);
        var relative = Path.GetRelativePath(root, current);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw UnsafeBackupPath();

        while (true)
        {
            if ((Directory.Exists(current) || File.Exists(current)) && IsReparse(current))
                throw UnsafeBackupPath();
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = parent;
        }
    }

    private static ConfigBackupException UnsafeBackupPath() =>
        new("CONFIG_BACKUP_STORAGE_PATH_UNSAFE", "配置备份目录包含链接或越界路径，未写入配置数据。", 409);

    private bool IsLocalBackupPath(string path)
    {
        try
        {
            EnsureBackupDirectoryIsLocal(path);
            return true;
        }
        catch (Exception ex) when (ex is ConfigBackupException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    private ConfigBackupSnapshot? FindSnapshot(string id)
    {
        if (!IsSafeId(id) || !IsLocalBackupPath(_root)) return null;
        foreach (var manifest in Directory.Exists(_root)
                     ? Directory.EnumerateFiles(_root, id + ".json", new EnumerationOptions
                     { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                     : Array.Empty<string>())
        {
            if (!IsLocalBackupPath(manifest)) continue;
            var snapshot = ReadSnapshot(manifest);
            if (snapshot?.Id == id && IsValidSnapshot(snapshot) && !IsReparse(manifest) &&
                !IsReparse(Path.GetDirectoryName(manifest)!)) return snapshot;
        }
        return null;
    }

    private static ConfigBackupSnapshot? ReadSnapshot(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var snapshot = JsonSerializer.Deserialize<ConfigBackupSnapshot>(document.RootElement.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            // Existing manifests contain running configurations and predate both
            // fields. Do not infer a different type from a filename or content.
            return snapshot is not null && !document.RootElement.TryGetProperty("configurationKind", out _)
                ? snapshot with { ConfigurationKind = ConfigBackupKinds.Running,
                    Command = snapshot.Command ?? H3cCommandPolicy.DisplayCurrentConfiguration }
                : snapshot;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return null; }
    }

    private FileStream? OpenVerified(ConfigBackupSnapshot snapshot)
    {
        FileStream? stream = null;
        try
        {
            var path = SnapshotPath(snapshot);
            if (!File.Exists(path) || !IsLocalBackupPath(path)) return null;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length == snapshot.ByteCount &&
                string.Equals(Convert.ToHexString(SHA256.HashData(stream)), snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                stream.Position = 0;
                return stream;
            }
            stream.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            return null;
        }
    }

    private static IReadOnlyList<string> ReadLines(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private string ProfileDirectory(string profileId)
    {
        if (!IsSafeId(profileId)) throw new ArgumentException("Profile ID is invalid.", nameof(profileId));
        return Path.Combine(_root, profileId);
    }

    private string SnapshotPath(ConfigBackupSnapshot snapshot) =>
        Path.Combine(ProfileDirectory(snapshot.ProfileId), snapshot.DownloadName ?? string.Empty);

    private static bool IsSafeId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

    private static bool IsValidSnapshot(ConfigBackupSnapshot snapshot) =>
        IsSafeId(snapshot.Id) && IsSafeId(snapshot.ProfileId) &&
        ConfigBackupKinds.IsSupported(snapshot.ConfigurationKind) &&
        (snapshot.ConfigurationKind == ConfigBackupKinds.StartupBody
            ? H3cCommandPolicy.IsStartupFileReadCommand(snapshot.Command)
            : snapshot.Command == ConfigBackupKinds.CommandFor(snapshot.ConfigurationKind)) &&
        (string.Equals(snapshot.DownloadName, $"{snapshot.Id}.{snapshot.ConfigurationKind.ToLowerInvariant()}.cfg", StringComparison.Ordinal) ||
            (snapshot.ConfigurationKind == ConfigBackupKinds.Running &&
                string.Equals(snapshot.DownloadName, snapshot.Id + ".cfg", StringComparison.Ordinal))) &&
        snapshot.ByteCount is > 0 and <= H3cRawOutput.MaximumBytes &&
        !string.IsNullOrWhiteSpace(snapshot.Sha256) && snapshot.Sha256.Length == 64 &&
        snapshot.Sha256.All(Uri.IsHexDigit);

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string? SafeBatchError(string? error) => error is null ? null :
        error == "Collection request exceeded its timeout." ? "CONFIG_BACKUP_TIMED_OUT" :
        error.Length is > 0 and <= 128 && error.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':')
            ? error : "CONFIG_BACKUP_COLLECTION_FAILED";

    internal sealed class BatchState
    {
        public BatchState(string id, string actorUserId, long createdMs, IReadOnlyList<BatchItemState> items,
            string configurationKind)
            => (Id, ActorUserId, CreatedMs, Items, ConfigurationKind) = (id, actorUserId, createdMs, items, configurationKind);
        public string Id { get; }
        public string ActorUserId { get; }
        public long CreatedMs { get; }
        public IReadOnlyList<BatchItemState> Items { get; }
        public string ConfigurationKind { get; }
        public long? CompletedMs { get; private set; }
        public void Complete() => CompletedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public ConfigBackupBatch Snapshot() => new(Id, ActorUserId,
            CompletedMs is null ? "RUNNING" : Items.Any(item => item.State == "FAILED") ? "PARTIAL" : "SUCCEEDED",
            CreatedMs, CompletedMs, Items.Count, Items.Count(item => item.State is "SUCCEEDED" or "FAILED"),
            Items.Count(item => item.State == "SUCCEEDED"), Items.Count(item => item.State == "FAILED"),
            Items.Select(item => new ConfigBackupBatchItem(item.ProfileId, item.State, item.SnapshotId, SafeBatchError(item.Error))).ToArray(), ConfigurationKind);
    }

    internal sealed class BatchItemState(string profileId, TaskScopeSnapshot scope, string state,
        string? snapshotId, string? error)
    {
        public string ProfileId { get; } = profileId;
        public TaskScopeSnapshot Scope { get; } = scope;
        public string State { get; set; } = state;
        public string? SnapshotId { get; set; } = snapshotId;
        public string? Error { get; set; } = error;
        public string? TaskRunId { get; set; }
    }
}

internal sealed class ConfigBackupException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal sealed partial class Catalog
{
    public ConfigBackupRaw? ReadCompleteH3cTaskRaw(string runId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT t.state,t.task_type,d.target,o.relative_path,o.command,o.byte_count,o.sha256,
            o.raw_segment_sha256,o.captured_ms,o.capture_status,r.state,r.record_count,r.sha256
            FROM task_runs t JOIN task_definitions d ON d.id=t.definition_id
            JOIN task_run_raw_outputs o ON o.task_run_id=t.id
            JOIN raw_segments r ON r.relative_path=o.relative_path
            WHERE t.id=$run LIMIT 1";
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != "SUCCEEDED" || reader.GetString(1) != "H3C_SSH_READONLY" ||
            reader.GetString(9) != "COMPLETE" || reader.GetString(10) != "FINALIZED" ||
            reader.GetInt32(11) != 1 || reader.GetString(7) != reader.GetString(12))
            return null;
        var profileId = reader.GetString(2);
        var relative = reader.GetString(3);
        if (!TryResolveRawPath(relative, out var path) || !File.Exists(path) || ContainsReparsePoint(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), reader.GetString(7), StringComparison.OrdinalIgnoreCase))
            return null;
        using var raw = JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 1));
        var root = raw.RootElement;
        if (root.GetProperty("device_profile_id").GetString() != profileId ||
            root.GetProperty("command").GetString() != reader.GetString(4) ||
            root.GetProperty("output_byte_count").GetInt64() != reader.GetInt64(5) ||
            !string.Equals(root.GetProperty("output_sha256").GetString(), reader.GetString(6), StringComparison.OrdinalIgnoreCase) ||
            root.GetProperty("captured_ms").GetInt64() != reader.GetInt64(8))
            return null;
        var output = Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("output_utf8_base64").GetString()!));
        return new(runId, profileId, reader.GetString(4), output, reader.GetString(6), reader.GetInt64(5), reader.GetInt64(8));
    }
}
