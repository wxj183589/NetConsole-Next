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
    string? DownloadName);

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
    IReadOnlyList<ConfigBackupBatchItem> Items);

internal sealed record ConfigBackupDiff(
    string BaseId,
    string TargetId,
    string ProfileId,
    bool Identical,
    int AddedLines,
    int RemovedLines,
    int ChangedLines,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed);

internal static class ConfigBackupSanitizer
{
    private static readonly Regex SecretLine = new(
        @"(?i)^\s*(?<prefix>.*?(?:password|passphrase|pass-phrase|pre-shared-key|community|private-key|secret)\b).*?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Sanitize(string command, string output) =>
        command == H3cCommandPolicy.DisplayCurrentConfiguration
            ? string.Join('\n', output.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n').Select(line => SecretLine.IsMatch(line)
                    ? SecretLine.Replace(line, "${prefix} <REDACTED>")
                    : line))
            : output;
}

internal sealed class ConfigBackupService(
    Catalog catalog,
    BoundedCollectionScheduler scheduler,
    TaskSiteScopeAuthorizer taskScopes,
    RuntimeSettings settings)
{
    private const int MaxProfilesPerBatch = 32;
    private const int MaxHistory = 100;
    private const int MaxDiffLines = 256;
    private const int MaxDiffCells = 1_048_576;
    private readonly ConcurrentDictionary<string, BatchState> _batches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileGates = new(StringComparer.Ordinal);
    private readonly string _root = Path.Combine(settings.DataRoot, "backup", "config");

    public ConfigBackupBatch Start(IReadOnlyList<string> profileIds, WebUserIdentity actor,
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
        var batch = new BatchState(id, actor.UserId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), items);
        _batches[id] = batch;
        _ = Task.Run(() => RunBatchAsync(batch), CancellationToken.None);
        return batch.Snapshot();
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

    public ConfigBackupBatch? GetBatch(string id, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        if (!_batches.TryGetValue(id, out var batch) || batch.ActorUserId != actor.UserId) return null;
        if (!scope.IsAuthorized || batch.Items.Any(item => !catalog.H3cProfileIsInScope(item.ProfileId, scope))) return null;
        return batch.Snapshot();
    }

    public IReadOnlyList<ConfigBackupSnapshot> List(string profileId, P2B5SiteScopeDecision scope)
    {
        if (!scope.IsAuthorized || !catalog.H3cProfileIsInScope(profileId, scope)) return Array.Empty<ConfigBackupSnapshot>();
        var directory = ProfileDirectory(profileId);
        if (!Directory.Exists(directory) || IsReparse(directory)) return Array.Empty<ConfigBackupSnapshot>();
        var snapshots = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(ReadSnapshot)
            .Where(item => item is not null && IsValidSnapshot(item) && item.State == "SUCCEEDED")
            .Cast<ConfigBackupSnapshot>()
            .OrderByDescending(item => item.Version)
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
        if (!Directory.Exists(directory) || IsReparse(directory)) return null;
        return snapshot with { CanDownload = true };
    }

    public (FileStream Stream, string Name)? OpenDownload(string id, P2B5SiteScopeDecision scope)
    {
        var snapshot = Get(id, scope);
        if (snapshot is null || snapshot.DownloadName is null) return null;
        var path = SnapshotPath(snapshot);
        if (!File.Exists(path) || IsReparse(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != snapshot.ByteCount ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
            return null;
        return (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan), snapshot.DownloadName);
    }

    public ConfigBackupDiff? Diff(string baseId, string targetId, P2B5SiteScopeDecision scope)
    {
        var baseline = Get(baseId, scope);
        var target = Get(targetId, scope);
        if (baseline is null || target is null || baseline.ProfileId != target.ProfileId) return null;
        var baseLines = ReadLines(SnapshotPath(baseline));
        var targetLines = ReadLines(SnapshotPath(target));
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
            addedCount, removedCount, Math.Min(addedCount, removedCount), added, removed);
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
        batch.Complete();
    }

    private async Task RunItemAsync(BatchState batch, BatchItemState item)
    {
        var gate = _profileGates.GetOrAdd(item.ProfileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireSupportedTarget(item.ProfileId);
            item.State = "RUNNING";
            var definitionId = $"config-backup-{batch.Id[..16]}-{Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(item.ProfileId))).ToLowerInvariant()[..12]}";
            catalog.CreateTaskDefinition(definitionId, "H3C_SSH_READONLY",
                item.ProfileId, H3cCommandPolicy.DisplayCurrentConfiguration, TimeSpan.FromMinutes(10),
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
            var snapshot = WriteSnapshot(item.ProfileId, item.Scope.DeviceId, item.Scope.SiteId, runId, raw);
            item.SnapshotId = snapshot.Id;
            item.State = "SUCCEEDED";
            catalog.TryRecordDeviceAuditEvent(batch.ActorUserId, "CONFIG_BACKUP", "SUCCEEDED", item.ProfileId, runId,
                Array.Empty<string>(), $"snapshot={snapshot.Id};version={snapshot.Version}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            item.State = "FAILED";
            item.Error = ex switch
            {
                TaskScopeRevokedException scope => scope.Reason,
                ConfigBackupException backup => backup.Code,
                _ => ex.GetType().Name
            };
            catalog.TryRecordDeviceAuditEvent(batch.ActorUserId, "CONFIG_BACKUP", "FAILED", item.ProfileId,
                item.TaskRunId, Array.Empty<string>(), item.Error);
        }
        finally { gate.Release(); }
    }

    private ConfigBackupSnapshot WriteSnapshot(string profileId, string deviceId, string siteId,
        string runId, ConfigBackupRaw raw)
    {
        var directory = ProfileDirectory(profileId);
        Directory.CreateDirectory(directory);
        var version = Directory.EnumerateFiles(directory, "*.json")
            .Select(ReadSnapshot)
            .Count(item => item is not null && IsValidSnapshot(item) && item.State == "SUCCEEDED") + 1;
        var id = Guid.NewGuid().ToString("N");
        var fileName = $"{id}.cfg";
        var path = Path.Combine(directory, fileName);
        var bytes = Encoding.UTF8.GetBytes(raw.Output);
        if (bytes.Length is < 1 or > H3cRawOutput.MaximumBytes)
            throw new InvalidDataException("Configuration backup exceeds the bounded Raw limit.");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var part = path + ".part";
        File.WriteAllBytes(part, bytes);
        File.Move(part, path, true);
        var snapshot = new ConfigBackupSnapshot(id, profileId, deviceId, siteId, version, "SUCCEEDED",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), raw.CapturedMs, sha, bytes.Length, runId, null, true, fileName);
        var manifest = Path.Combine(directory, id + ".json");
        var manifestPart = manifest + ".part";
        File.WriteAllText(manifestPart, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.Move(manifestPart, manifest, true);
        return snapshot;
    }

    private ConfigBackupSnapshot? FindSnapshot(string id)
    {
        if (!IsSafeId(id)) return null;
        foreach (var manifest in Directory.Exists(_root)
                     ? Directory.EnumerateFiles(_root, id + ".json", SearchOption.AllDirectories)
                     : Array.Empty<string>())
        {
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
            return JsonSerializer.Deserialize<ConfigBackupSnapshot>(document.RootElement.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return null; }
    }

    private static IReadOnlyList<string> ReadLines(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is < 1 or > H3cRawOutput.MaximumBytes)
            throw new InvalidDataException("Configuration backup exceeds the bounded Raw limit.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
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
        string.Equals(snapshot.DownloadName, snapshot.Id + ".cfg", StringComparison.Ordinal) &&
        snapshot.ByteCount is > 0 and <= H3cRawOutput.MaximumBytes &&
        !string.IsNullOrWhiteSpace(snapshot.Sha256) && snapshot.Sha256.Length == 64 &&
        snapshot.Sha256.All(Uri.IsHexDigit);

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    internal sealed class BatchState
    {
        public BatchState(string id, string actorUserId, long createdMs, IReadOnlyList<BatchItemState> items)
            => (Id, ActorUserId, CreatedMs, Items) = (id, actorUserId, createdMs, items);
        public string Id { get; }
        public string ActorUserId { get; }
        public long CreatedMs { get; }
        public IReadOnlyList<BatchItemState> Items { get; }
        public long? CompletedMs { get; private set; }
        public void Complete() => CompletedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public ConfigBackupBatch Snapshot() => new(Id, ActorUserId,
            CompletedMs is null ? "RUNNING" : Items.Any(item => item.State == "FAILED") ? "PARTIAL" : "SUCCEEDED",
            CreatedMs, CompletedMs, Items.Count, Items.Count(item => item.State is "SUCCEEDED" or "FAILED"),
            Items.Count(item => item.State == "SUCCEEDED"), Items.Count(item => item.State == "FAILED"),
            Items.Select(item => new ConfigBackupBatchItem(item.ProfileId, item.State, item.SnapshotId, item.Error)).ToArray());
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
