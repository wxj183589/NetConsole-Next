using System.Text.Json;

namespace NetConsole.Next.Service;

// Metadata only: configuration bodies, endpoint details and credentials stay in
// their existing stores. Running work is never replayed from this document.
internal sealed class ConfigBackupBatchHistoryStore
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private Dictionary<string, ConfigBackupBatch> _items = new(StringComparer.Ordinal);
    private bool _unavailable;

    internal ConfigBackupBatchHistoryStore(RuntimeSettings settings)
    {
        _path = Path.Combine(settings.DataRoot, "backup", "config-batches.json");
        try
        {
            CheckPath();
            if (!File.Exists(_path)) return;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > MaximumBytes) throw new InvalidDataException();
            var document = JsonSerializer.Deserialize<StoreDocument>(stream, JsonOptions);
            if (document is not { Version: 1, Batches: not null } ||
                document.Batches.Count > ConfigBackupService.MaximumCompletedBatchMetadata ||
                document.Batches.Any(item => !IsValid(item)) ||
                document.Batches.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != document.Batches.Count)
                throw new InvalidDataException();
            _items = document.Batches.ToDictionary(item => item.Id, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { _unavailable = true; } // Keep damaged evidence; other collection services may start.
    }

    internal void RequireAvailable()
    {
        if (_unavailable) throw Unavailable();
    }

    internal ConfigBackupBatch? Get(string id)
    {
        RequireAvailable();
        return _items.TryGetValue(id, out var item) ? item with { Items = item.Items.ToArray() } : null;
    }

    internal void Store(ConfigBackupBatch item)
    {
        RequireAvailable();
        try
        {
            if (!IsValid(item)) throw new InvalidDataException();
            var next = _items.Values.Where(value => value.Id != item.Id).Append(item)
                .OrderByDescending(value => value.CompletedMs).ThenByDescending(value => value.CreatedMs)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .Take(ConfigBackupService.MaximumCompletedBatchMetadata).ToDictionary(value => value.Id, StringComparer.Ordinal);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoreDocument(1, next.Values.ToArray()), JsonOptions);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException();
            CheckPath();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var part = _path + ".part";
            if (IsReparse(part)) throw new InvalidDataException();
            using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(part, _path, overwrite: true);
            _items = next;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _unavailable = true;
            throw Unavailable();
        }
    }

    private void CheckPath()
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(_path)!); directory is not null; directory = directory.Parent)
            if (IsReparse(directory.FullName)) throw new InvalidDataException();
        if (IsReparse(_path)) throw new InvalidDataException();
    }

    private static bool IsValid(ConfigBackupBatch? item) => item is not null && SafeId(item.Id) && SafeId(item.ActorUserId) &&
        item.CreatedMs > 0 && item.CompletedMs >= item.CreatedMs && item.State is "SUCCEEDED" or "PARTIAL" &&
        (ConfigBackupKinds.IsScheduled(item.ConfigurationKind) || item.ConfigurationKind == ConfigBackupKinds.StartupBody) &&
        item.Items is { Count: >= 1 and <= 32 } && item.Total == item.Items.Count && item.Completed == item.Total &&
        item.Succeeded == item.Items.Count(value => value is { State: "SUCCEEDED" }) &&
        item.Failed == item.Items.Count(value => value is { State: "FAILED" }) && item.Succeeded + item.Failed == item.Total &&
        item.State == (item.Failed == 0 ? "SUCCEEDED" : "PARTIAL") &&
        item.Items.Select(value => value?.ProfileId).Distinct(StringComparer.Ordinal).Count() == item.Total &&
        item.Items.All(value => value is not null && SafeId(value.ProfileId) &&
            (value.State == "SUCCEEDED" ? SafeId(value.SnapshotId) && value.Error is null :
                value.State == "FAILED" && value.SnapshotId is null && SafeError(value.Error)));

    private static bool SafeId(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');
    private static bool SafeError(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':');
    private static bool IsReparse(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0);
    }
    private static ConfigBackupException Unavailable() => new("CONFIG_BACKUP_BATCH_STORE_UNAVAILABLE",
        "备份批次记录无法安全读写，请检查数据目录并修复后重启；配置版本文件不受影响。", 503);
    private sealed record StoreDocument(int Version, IReadOnlyList<ConfigBackupBatch> Batches);
}
