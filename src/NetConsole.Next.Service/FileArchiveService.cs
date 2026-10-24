using System.Collections.Concurrent;
using System.IO.Compression;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace NetConsole.Next.Service;

internal sealed class FileArchiveService : BackgroundService
{
    private readonly FileManagerService _files;
    private readonly RuntimeSettings _settings;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, ArchiveJob> _jobs = new(StringComparer.Ordinal);

    public FileArchiveService(FileManagerService files, RuntimeSettings settings)
    { _files = files; _settings = settings; }

    public ArchiveJobSnapshot Create(ArchiveRequest request, WebUserIdentity owner)
    {
        if (request.Selections is null || request.Selections.Count == 0 || request.Selections.Count > 50)
            throw new ArgumentException("At least one archive selection is required.");
        var files = new List<(ResolvedFile File, string EntryName, long Size)>();
        foreach (var selection in request.Selections)
            files.AddRange(_files.Expand(selection));
        var deduped = new List<(ResolvedFile File, string EntryName, long Size)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files)
        {
            if (deduped.Count >= 100_000) throw new InvalidOperationException("Archive contains too many files.");
            var name = item.EntryName; var suffix = 1;
            while (!names.Add(name)) name = $"{Path.GetFileNameWithoutExtension(item.EntryName)} ({suffix++}){Path.GetExtension(item.EntryName)}";
            deduped.Add((item.File, name, item.Size));
        }
        if (deduped.Count == 0) throw new InvalidOperationException("The selection contains no downloadable files.");
        var total = deduped.Sum(item => item.Size);
        if (total > 20L * 1024 * 1024 * 1024) throw new InvalidOperationException("Archive exceeds the 20 GiB safety limit.");
        var job = new ArchiveJob(Guid.NewGuid().ToString("N"), owner.UserId, owner.SessionId, deduped, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _jobs[job.Id] = job;
        _queue.Writer.TryWrite(job.Id);
        return job.Snapshot();
    }

    public ArchiveJobSnapshot? Get(string id, WebUserIdentity owner)
    {
        if (!_jobs.TryGetValue(id, out var job) || !job.IsOwner(owner)) return null;
        return job.Snapshot();
    }

    public bool Cancel(string id, WebUserIdentity owner)
    {
        if (!_jobs.TryGetValue(id, out var job) || !job.IsOwner(owner)) return false;
        job.Cancel(); return true;
    }

    public (FileStream Stream, string Name)? OpenDownload(string id, WebUserIdentity owner)
    {
        if (!_jobs.TryGetValue(id, out var job) || !job.IsOwner(owner) || job.State != "COMPLETED" || job.OutputPath is null || !File.Exists(job.OutputPath)) return null;
        return (new FileStream(job.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan), job.DownloadName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(TempRoot);
        CleanupExpired();
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (_jobs.TryGetValue(id, out var job)) await RunAsync(job, stoppingToken);
            CleanupExpired();
        }
    }

    private async Task RunAsync(ArchiveJob job, CancellationToken stoppingToken)
    {
        if (!job.TryStart()) return;
        var temp = Path.Combine(TempRoot, job.Id + ".zip");
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
                foreach (var item in job.Files)
                {
                    job.ThrowIfCancelled(stoppingToken);
                    job.SetCurrent(item.EntryName);
                    var entry = archive.CreateEntry(item.EntryName, CompressionLevel.Fastest);
                    await using var input = new FileStream(item.File.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var target = entry.Open();
                    await input.CopyToAsync(target, stoppingToken);
                    job.Advance(item.Size);
                }
            }
            job.Complete(temp, $"netconsole-next-{job.Id}.zip");
        }
        catch (OperationCanceledException)
        {
            job.Cancel(); TryDelete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            job.Fail("Archive could not be completed safely."); TryDelete(temp);
        }
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
        foreach (var pair in _jobs)
            if (pair.Value.CreatedMs < cutoff && pair.Value.IsTerminal && _jobs.TryRemove(pair.Key, out var old)) { TryDelete(old.OutputPath); }
        if (Directory.Exists(TempRoot))
            foreach (var path in Directory.EnumerateFiles(TempRoot, "*.zip"))
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-24)) TryDelete(path);
    }

    private string TempRoot => Path.Combine(_settings.DataRoot, "archive-tmp");
    private static void TryDelete(string? path) { try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { } }

    private sealed class ArchiveJob
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancel = new();
        private string _state = "QUEUED";
        private int _processed;
        private long _bytes;
        private string? _current;
        private string? _error;
        private long? _completed;
        public string Id { get; }
        public string UserId { get; }
        public string SessionId { get; }
        public IReadOnlyList<(ResolvedFile File, string EntryName, long Size)> Files { get; }
        public long CreatedMs { get; }
        public string? OutputPath { get; private set; }
        public string DownloadName { get; private set; } = string.Empty;
        public string State { get { lock (_gate) return _state; } }
        public bool IsTerminal { get { lock (_gate) return _state is "COMPLETED" or "FAILED" or "CANCELLED" or "EXPIRED"; } }

        public ArchiveJob(string id, string userId, string sessionId, IReadOnlyList<(ResolvedFile File, string EntryName, long Size)> files, long createdMs)
        { Id = id; UserId = userId; SessionId = sessionId; Files = files; CreatedMs = createdMs; }
        public bool IsOwner(WebUserIdentity identity) => string.Equals(UserId, identity.UserId, StringComparison.Ordinal) && string.Equals(SessionId, identity.SessionId, StringComparison.Ordinal);
        public bool TryStart() { lock (_gate) { if (_state != "QUEUED") return false; _state = "RUNNING"; return true; } }
        public void SetCurrent(string value) { lock (_gate) _current = value; }
        public void Advance(long bytes) { lock (_gate) { _processed++; _bytes += bytes; } }
        public void Complete(string output, string name) { lock (_gate) { OutputPath = output; DownloadName = name; _state = "COMPLETED"; _completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }
        public void Fail(string error) { lock (_gate) { _error = error; _state = "FAILED"; _completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }
        public void Cancel() { lock (_gate) { if (!IsTerminal) _state = "CANCELLED"; _cancel.Cancel(); } }
        public void ThrowIfCancelled(CancellationToken stoppingToken) { _cancel.Token.ThrowIfCancellationRequested(); stoppingToken.ThrowIfCancellationRequested(); }
        public ArchiveJobSnapshot Snapshot() { lock (_gate) return new(Id, _state, Files.Count, _processed, _bytes, Files.Sum(x => x.Size), _current, _error, CreatedMs, _completed, DownloadName, _state == "COMPLETED", _state is "QUEUED" or "RUNNING"); }
    }
}
