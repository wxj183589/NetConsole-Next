using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace NetConsole.Next.Service;

internal sealed class FileArchiveService : BackgroundService
{
    private const int CopyBufferBytes = 128 * 1024;
    private const long DiskCheckIntervalBytes = 4 * 1024 * 1024;

    private readonly FileManagerService _files;
    private readonly RuntimeSettings _settings;
    private readonly IArchiveDiskSpaceProvider _diskSpace;
    private readonly Channel<string> _queue;
    private readonly ConcurrentDictionary<string, ArchiveJob> _jobs = new(StringComparer.Ordinal);
    private readonly object _admissionGate = new();
    private readonly Dictionary<string, int> _activeByUser = new(StringComparer.Ordinal);
    private int _activeJobs;
    private int _queuedJobs;
    private int _runningJobs;
    private long _reservedInputBytes;

    public FileArchiveService(FileManagerService files, RuntimeSettings settings)
        : this(files, settings, new DriveInfoArchiveDiskSpaceProvider()) { }

    public FileArchiveService(FileManagerService files, RuntimeSettings settings, IArchiveDiskSpaceProvider diskSpace)
    {
        _files = files;
        _settings = settings;
        _diskSpace = diskSpace;
        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(settings.ArchiveMaxQueuedJobs)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public ArchiveJobSnapshot Create(ArchiveRequest request, WebUserIdentity owner, CancellationToken cancellationToken = default)
    {
        CleanupExpired();
        var reservation = ReserveAdmission(owner);
        ArchiveJob? job = null;
        try
        {
            if (request.Selections is null || request.Selections.Count == 0 || request.Selections.Count > 50)
                throw new ArchiveRejectedException("ARCHIVE_RESOURCE_LIMIT", "The archive request contains too many selections.", StatusCodes.Status413PayloadTooLarge);

            // This is an admission check against the volume that owns archive-tmp.
            // It must happen before Resolve/Expand can touch the selected tree. The
            // worker repeats the check because this is only a TOCTOU admission gate.
            EnsureAvailableDisk();
            var budget = new ArchiveExpansionBudget(_settings);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var seenFiles = new HashSet<string>(comparer);
            var expanded = new List<(ResolvedFile File, string EntryName, long Size)>();
            foreach (var selection in request.Selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                expanded.AddRange(_files.Expand(selection, budget, seenFiles, cancellationToken));
            }

            if (expanded.Count == 0)
                throw new ArchiveRejectedException("ARCHIVE_EMPTY", "The selection contains no downloadable files.", StatusCodes.Status400BadRequest);

            var files = MakeUniqueEntryNames(expanded);
            var totalBytes = budget.InputBytes;
            lock (_admissionGate)
            {
                if (reservation.ActiveReleased)
                    throw new ArchiveRejectedException("ARCHIVE_SERVICE_STOPPING", "The archive service is stopping.", StatusCodes.Status503ServiceUnavailable);
                if (_reservedInputBytes > _settings.ArchiveMaxAggregateReservedBytes - totalBytes)
                    throw new ArchiveRejectedException("ARCHIVE_RESOURCE_LIMIT", "The aggregate archive resource budget is full.", StatusCodes.Status429TooManyRequests);

                reservation.InputBytes = totalBytes;
                reservation.InputReserved = true;
                _reservedInputBytes += totalBytes;
                job = new ArchiveJob(Guid.NewGuid().ToString("N"), owner.UserId, owner.SessionId, files, totalBytes,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TempRoot, reservation);
                _jobs[job.Id] = job;
                if (!TryEnqueueLocked(job.Id))
                    throw new ArchiveRejectedException("ARCHIVE_QUEUE_FULL", "The archive queue is full; try again after an existing task completes.", StatusCodes.Status429TooManyRequests);
                reservation.Enqueued = true;
                _queuedJobs++;
            }

            return job.Snapshot();
        }
        catch
        {
            if (job is not null)
            {
                _jobs.TryRemove(job.Id, out _);
                ReleaseAdmission(job);
            }
            else
            {
                ReleaseReservation(reservation);
            }
            throw;
        }
    }

    public ArchiveJobSnapshot? Get(string id, WebUserIdentity owner)
    {
        if (!_jobs.TryGetValue(id, out var job) || !job.IsOwner(owner)) return null;
        return job.Snapshot();
    }

    public bool Cancel(string id, WebUserIdentity owner)
    {
        lock (_admissionGate)
        {
            if (!_jobs.TryGetValue(id, out var job) || !job.IsOwner(owner)) return false;
            var result = job.RequestCancel();
            if (result == CancelResult.Queued)
            {
                ReleaseAdmissionLocked(job, releaseInput: true);
                job.PublishCancelled();
            }
            return true;
        }
    }

    public ArchiveDownload? OpenDownload(string id, WebUserIdentity owner)
    {
        if (!_jobs.TryGetValue(id, out var job) || !job.IsOwner(owner)) return null;
        return job.OpenDownload();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(TempRoot);
        CleanupExpired(startup: true);
        try
        {
            await Task.WhenAll(ProcessQueueAsync(stoppingToken), MaintenanceLoopAsync(stoppingToken));
        }
        finally
        {
            CancelOutstandingJobs();
            CleanupExpired(startup: true);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (_jobs.TryGetValue(id, out var job) && TryStart(job))
                    await RunAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task MaintenanceLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.ArchiveCleanupIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) CleanupExpired();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunAsync(ArchiveJob job, CancellationToken stoppingToken)
    {
        var partPath = Path.Combine(TempRoot, job.Id + ".zip.part");
        var finalPath = Path.Combine(TempRoot, job.Id + ".zip");
        var completion = ArchiveCompletion.None;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.CancellationToken);
            var token = linked.Token;
            job.ThrowIfCancelled(token);
            EnsureAvailableDisk();
            await using (var file = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new ArchiveOutputStream(file, _settings.ArchiveMaxTempBytesPerJob, _settings.ArchiveMinFreeDiskBytes, _diskSpace))
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
                foreach (var item in job.Files)
                {
                    job.ThrowIfCancelled(token);
                    job.SetCurrent(item.EntryName);
                    var resolved = _files.Resolve(item.File.RootId, item.File.RelativePath, true);
                    if (resolved.IsDirectory || !string.Equals(resolved.FullPath, item.File.FullPath, StringComparison.OrdinalIgnoreCase))
                        throw new ArchiveRejectedException("ARCHIVE_INPUT_CHANGED", "A selected file changed before it could be archived.", StatusCodes.Status409Conflict);

                    await using var input = new FileStream(resolved.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length != item.Size)
                        throw new ArchiveRejectedException("ARCHIVE_INPUT_CHANGED", "A selected file changed before it could be archived.", StatusCodes.Status409Conflict);
                    var entry = archive.CreateEntry(item.EntryName, CompressionLevel.Fastest);
                    await using var target = entry.Open();
                    var copied = await CopyFileAsync(input, target, token);
                    if (copied != item.Size || input.Length != item.Size)
                        throw new ArchiveRejectedException("ARCHIVE_INPUT_CHANGED", "A selected file changed while it was being archived.", StatusCodes.Status409Conflict);
                    job.Advance(item.Size);
                }
            }

            job.ThrowIfCancelled(token);
            EnsureAvailableDisk();
            File.Move(partPath, finalPath);
            partPath = string.Empty;
            if (job.TryPrepareCompletion(finalPath, $"netconsole-next-{job.Id}.zip"))
            {
                completion = ArchiveCompletion.Completed;
            }
            else
            {
                TryDelete(finalPath);
                completion = ArchiveCompletion.Cancelled;
            }
        }
        catch (OperationCanceledException) when (job.CancellationToken.IsCancellationRequested || stoppingToken.IsCancellationRequested)
        {
            completion = ArchiveCompletion.Cancelled;
        }
        catch (ArchiveRejectedException ex)
        {
            completion = ArchiveCompletion.Failed(ex.Code, ex.Message);
        }
        catch (Exception)
        {
            completion = ArchiveCompletion.Failed("ARCHIVE_FAILED", "Archive could not be completed safely.");
        }
        finally
        {
            TryDelete(partPath);
            lock (_admissionGate)
            {
                switch (completion.Kind)
                {
                    case ArchiveCompletionKind.Completed:
                        // Keep the completed job's input reservation until retention cleanup, but
                        // release the active/user slot before making COMPLETED observable.
                        ReleaseAdmissionLocked(job, releaseInput: false);
                        ArchiveTestHooks.BeforePublishCompleted?.Invoke(() => job.RequestCancel());
                        if (!job.PublishCompleted())
                        {
                            TryDelete(job.OutputPath);
                            ReleaseInputLocked(job.Reservation);
                            job.PublishCancelled();
                        }
                        break;
                    case ArchiveCompletionKind.Cancelled:
                        ReleaseAdmissionLocked(job, releaseInput: true);
                        job.PublishCancelled();
                        break;
                    case ArchiveCompletionKind.Failed:
                        ReleaseAdmissionLocked(job, releaseInput: true);
                        job.PublishFailed(completion.ErrorCode!, completion.Error!);
                        break;
                    default:
                        ReleaseAdmissionLocked(job, releaseInput: true);
                        job.PublishFailed("ARCHIVE_FAILED", "Archive could not be completed safely.");
                        break;
                }
            }
        }
    }

    private async Task<long> CopyFileAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        if (_settings.ArchiveTestCopyDelayMilliseconds <= 0)
        {
            await input.CopyToAsync(output, CopyBufferBytes, cancellationToken);
            return input is FileStream file ? file.Position : 0;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        long copied = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken);
                if (read == 0) return copied;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                await Task.Delay(_settings.ArchiveTestCopyDelayMilliseconds, cancellationToken);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private AdmissionReservation ReserveAdmission(WebUserIdentity owner)
    {
        lock (_admissionGate)
        {
            _activeByUser.TryGetValue(owner.UserId, out var userJobs);
            if (userJobs >= _settings.ArchiveMaxActiveJobsPerUser)
                throw new ArchiveRejectedException("ARCHIVE_USER_LIMIT", "This user already has the maximum number of active archive tasks.");
            if (_activeJobs >= _settings.ArchiveMaxRunningJobs + _settings.ArchiveMaxQueuedJobs)
                throw new ArchiveRejectedException("ARCHIVE_QUEUE_FULL", "The archive queue is full; try again after an existing task completes.");
            var reservation = new AdmissionReservation(owner.UserId);
            _activeJobs++;
            _activeByUser[owner.UserId] = userJobs + 1;
            return reservation;
        }
    }

    private bool TryStart(ArchiveJob job)
    {
        lock (_admissionGate)
        {
            if (!job.TryStart()) return false;
            job.Reservation.Enqueued = false;
            job.Reservation.Running = true;
            _queuedJobs = Math.Max(0, _queuedJobs - 1);
            _runningJobs++;
            return true;
        }
    }

    private bool TryEnqueueLocked(string id)
    {
        if (_queue.Writer.TryWrite(id)) return true;
        var retained = new List<string>();
        while (_queue.Reader.TryRead(out var queuedId))
        {
            if (_jobs.TryGetValue(queuedId, out var queued) && queued.State == "CANCELLED") continue;
            retained.Add(queuedId);
        }
        foreach (var queuedId in retained)
            if (!_queue.Writer.TryWrite(queuedId)) return false;
        return _queue.Writer.TryWrite(id);
    }

    private void ReleaseReservation(AdmissionReservation reservation)
    {
        lock (_admissionGate) ReleaseReservationLocked(reservation);
    }

    private void ReleaseAdmission(ArchiveJob job)
    {
        lock (_admissionGate) ReleaseAdmissionLocked(job, releaseInput: true);
    }

    private void ReleaseAdmissionLocked(ArchiveJob job, bool releaseInput)
    {
        var reservation = job.Reservation;
        if (!reservation.ActiveReleased)
        {
            reservation.ActiveReleased = true;
            if (reservation.Running) _runningJobs = Math.Max(0, _runningJobs - 1);
            else if (reservation.Enqueued) _queuedJobs = Math.Max(0, _queuedJobs - 1);
            _activeJobs = Math.Max(0, _activeJobs - 1);
            ReleaseUserSlotLocked(reservation.UserId);
        }
        if (releaseInput) ReleaseInputLocked(reservation);
    }

    private void ReleaseReservationLocked(AdmissionReservation reservation)
    {
        if (reservation.ActiveReleased) return;
        reservation.ActiveReleased = true;
        if (reservation.Enqueued) _queuedJobs = Math.Max(0, _queuedJobs - 1);
        if (reservation.Running) _runningJobs = Math.Max(0, _runningJobs - 1);
        _activeJobs = Math.Max(0, _activeJobs - 1);
        ReleaseUserSlotLocked(reservation.UserId);
        ReleaseInputLocked(reservation);
    }

    private void ReleaseUserSlotLocked(string userId)
    {
        if (!_activeByUser.TryGetValue(userId, out var userJobs)) return;
        if (userJobs <= 1) _activeByUser.Remove(userId);
        else _activeByUser[userId] = userJobs - 1;
    }

    private void ReleaseInputLocked(AdmissionReservation reservation)
    {
        if (!reservation.InputReserved) return;
        reservation.InputReserved = false;
        _reservedInputBytes = Math.Max(0, _reservedInputBytes - reservation.InputBytes);
    }

    private void CancelOutstandingJobs()
    {
        foreach (var job in _jobs.Values.ToArray())
        {
            lock (_admissionGate)
            {
                if (job.RequestCancel() == CancelResult.Queued)
                {
                    ReleaseAdmissionLocked(job, releaseInput: true);
                    job.PublishCancelled();
                }
            }
        }
    }

    private void CleanupExpired(bool startup = false)
    {
        var now = DateTimeOffset.UtcNow;
        var terminal = _jobs.Values.Where(job => job.IsTerminal)
            .OrderBy(job => job.CompletedMs ?? job.CreatedMs)
            .ToArray();
        var remove = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in terminal)
        {
            var retention = job.State switch
            {
                "COMPLETED" => TimeSpan.FromHours(_settings.ArchiveCompletedRetentionHours),
                "FAILED" => TimeSpan.FromHours(_settings.ArchiveFailedRetentionHours),
                _ => TimeSpan.FromHours(_settings.ArchiveCancelledRetentionHours)
            };
            if (now - DateTimeOffset.FromUnixTimeMilliseconds(job.CompletedMs ?? job.CreatedMs) >= retention)
                remove.Add(job.Id);
        }
        foreach (var job in terminal.Take(Math.Max(0, terminal.Length - _settings.ArchiveMaxRetainedTerminalJobs)))
            remove.Add(job.Id);
        foreach (var id in remove) TryRemoveJob(id);

        var activePaths = _jobs.Values.SelectMany(job => job.ManagedPaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphanCutoff = startup ? now : now - TimeSpan.FromHours(24);
        if (!Directory.Exists(TempRoot)) return;
        foreach (var path in Directory.EnumerateFiles(TempRoot))
        {
            if (activePaths.Contains(Path.GetFullPath(path))) continue;
            if (File.GetLastWriteTimeUtc(path) < orphanCutoff.UtcDateTime) TryDelete(path);
        }
    }

    private void TryRemoveJob(string id)
    {
        if (!_jobs.TryGetValue(id, out var job) || !job.TryClaimRemoval()) return;
        if (!_jobs.TryRemove(id, out _)) return;
        lock (_admissionGate) ReleaseInputLocked(job.Reservation);
        TryDelete(job.OutputPath);
    }

    private void EnsureAvailableDisk()
    {
        long? available;
        try
        {
            available = _diskSpace.GetAvailableFreeSpace(TempRoot);
        }
        catch (Exception) { available = null; }
        if (!available.HasValue)
            throw new ArchiveRejectedException("ARCHIVE_STORAGE_UNAVAILABLE", "Archive storage availability could not be verified.", StatusCodes.Status503ServiceUnavailable);
        if (available.Value < _settings.ArchiveMinFreeDiskBytes)
            throw new ArchiveRejectedException("ARCHIVE_DISK_SPACE_LOW", "The host has insufficient free space for an archive.", StatusCodes.Status503ServiceUnavailable);
    }

    private string TempRoot => Path.Combine(_settings.DataRoot, "archive-tmp");

    internal (int ActiveJobs, int QueuedJobs, int RunningJobs, long ReservedInputBytes, int JobCount) AdmissionState
    {
        get
        {
            lock (_admissionGate)
                return (_activeJobs, _queuedJobs, _runningJobs, _reservedInputBytes, _jobs.Count);
        }
    }

    private static void TryDelete(string? path) { try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { } }

    private static IReadOnlyList<(ResolvedFile File, string EntryName, long Size)> MakeUniqueEntryNames(
        IReadOnlyList<(ResolvedFile File, string EntryName, long Size)> files)
    {
        var result = new List<(ResolvedFile File, string EntryName, long Size)>(files.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files)
        {
            var name = item.EntryName;
            var directory = Path.GetDirectoryName(name)?.Replace(Path.DirectorySeparatorChar, '/');
            var fileName = Path.GetFileName(name);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var suffix = 1;
            while (!names.Add(name))
            {
                var candidate = $"{stem} ({suffix++}){extension}";
                name = string.IsNullOrEmpty(directory) ? candidate : $"{directory}/{candidate}";
            }
            result.Add((item.File, name, item.Size));
        }
        return result;
    }

    private sealed class AdmissionReservation(string userId)
    {
        public string UserId { get; } = userId;
        public bool ActiveReleased { get; set; }
        public bool Enqueued { get; set; }
        public bool Running { get; set; }
        public bool InputReserved { get; set; }
        public long InputBytes { get; set; }
    }

    private enum CancelResult { Queued, Running, AlreadyTerminal }

    private enum ArchiveCompletionKind { None, Completed, Cancelled, Failed }

    private sealed record ArchiveCompletion(ArchiveCompletionKind Kind, string? ErrorCode = null, string? Error = null)
    {
        public static ArchiveCompletion None { get; } = new(ArchiveCompletionKind.None);
        public static ArchiveCompletion Completed { get; } = new(ArchiveCompletionKind.Completed);
        public static ArchiveCompletion Cancelled { get; } = new(ArchiveCompletionKind.Cancelled);
        public static ArchiveCompletion Failed(string code, string error) => new(ArchiveCompletionKind.Failed, code, error);
    }

    private sealed class ArchiveJob
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancel = new();
        private string _state = "QUEUED";
        private int _processed;
        private long _bytes;
        private string? _current;
        private string? _errorCode;
        private string? _error;
        private long? _completed;
        private int _activeDownloads;
        private bool _cleanupClaimed;

        public ArchiveJob(string id, string userId, string sessionId,
            IReadOnlyList<(ResolvedFile File, string EntryName, long Size)> files,
            long totalBytes, long createdMs, string tempRoot, AdmissionReservation reservation)
        {
            Id = id; UserId = userId; SessionId = sessionId; Files = files; TotalBytes = totalBytes;
            CreatedMs = createdMs; TempRoot = tempRoot; Reservation = reservation;
        }

        public string Id { get; }
        public string UserId { get; }
        public string SessionId { get; }
        public IReadOnlyList<(ResolvedFile File, string EntryName, long Size)> Files { get; }
        public long TotalBytes { get; }
        public long CreatedMs { get; }
        public string TempRoot { get; }
        public AdmissionReservation Reservation { get; }
        public string? OutputPath { get; private set; }
        public string DownloadName { get; private set; } = string.Empty;
        public CancellationToken CancellationToken => _cancel.Token;
        public string State { get { lock (_gate) return _state; } }
        public long? CompletedMs { get { lock (_gate) return _completed; } }
        public bool IsTerminal { get { lock (_gate) return _state is "COMPLETED" or "FAILED" or "CANCELLED" or "EXPIRED"; } }
        public IEnumerable<string> ManagedPaths
        {
            get
            {
                lock (_gate)
                {
                    var paths = new List<string>();
                    if (!string.IsNullOrWhiteSpace(OutputPath)) paths.Add(OutputPath);
                    paths.Add(Path.Combine(TempRoot, Id + ".zip.part"));
                    return paths;
                }
            }
        }
        public bool IsOwner(WebUserIdentity identity) => string.Equals(UserId, identity.UserId, StringComparison.Ordinal) && string.Equals(SessionId, identity.SessionId, StringComparison.Ordinal);

        public bool TryStart() { lock (_gate) { if (_state != "QUEUED") return false; _state = "RUNNING"; return true; } }
        public void SetCurrent(string value) { lock (_gate) _current = value; }
        public void Advance(long bytes) { lock (_gate) { _processed++; _bytes += bytes; } }
        public bool TryPrepareCompletion(string output, string name)
        {
            lock (_gate)
            {
                if (_state != "RUNNING" || _cancel.IsCancellationRequested) return false;
                OutputPath = output; DownloadName = name; return true;
            }
        }

        public bool PublishCompleted()
        {
            lock (_gate)
            {
                if (_state != "RUNNING" || _cancel.IsCancellationRequested) return false;
                _state = "COMPLETED"; _completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); return true;
            }
        }

        public bool PublishFailed(string code, string error)
        {
            lock (_gate)
            {
                if (_state != "RUNNING") return false;
                _errorCode = code; _error = error; _state = "FAILED"; _completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); return true;
            }
        }

        public bool PublishCancelled()
        {
            lock (_gate)
            {
                if (_state is "COMPLETED" or "FAILED" or "CANCELLED" or "EXPIRED") return false;
                _state = "CANCELLED"; _errorCode = "ARCHIVE_CANCELLED"; _error = "The archive task was cancelled.";
                _completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); return true;
            }
        }

        public CancelResult RequestCancel()
        {
            CancelResult result;
            lock (_gate)
            {
                if (_state is "COMPLETED" or "FAILED" or "CANCELLED" or "EXPIRED") return CancelResult.AlreadyTerminal;
                result = _state == "QUEUED" ? CancelResult.Queued : CancelResult.Running;
            }
            _cancel.Cancel();
            return result;
        }
        public void ThrowIfCancelled(CancellationToken stoppingToken) { _cancel.Token.ThrowIfCancellationRequested(); stoppingToken.ThrowIfCancellationRequested(); }

        public ArchiveDownload? OpenDownload()
        {
            lock (_gate)
            {
                if (_state != "COMPLETED" || _cleanupClaimed || string.IsNullOrWhiteSpace(OutputPath) || !File.Exists(OutputPath)) return null;
                try
                {
                    var stream = new FileStream(OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    _activeDownloads++;
                    return new ArchiveDownload(new DownloadLeaseStream(stream, ReleaseDownload), DownloadName);
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
        }

        private void ReleaseDownload() { lock (_gate) _activeDownloads = Math.Max(0, _activeDownloads - 1); }

        public bool TryClaimRemoval()
        {
            lock (_gate)
            {
                if (!IsTerminal || _activeDownloads > 0 || _cleanupClaimed) return false;
                _cleanupClaimed = true;
                return true;
            }
        }

        public ArchiveJobSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new(Id, _state, Files.Count, _processed, _bytes, TotalBytes, _current, _errorCode, _error,
                    CreatedMs, _completed, DownloadName, _state == "COMPLETED", _state is "QUEUED" or "RUNNING");
            }
        }
    }

    private sealed class DownloadLeaseStream(Stream inner, Action release) : Stream
    {
        private int _released;
        private void ReleaseOnce() { if (Interlocked.Exchange(ref _released, 1) == 0) release(); }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); ReleaseOnce(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); ReleaseOnce(); GC.SuppressFinalize(this); }
    }

    private sealed class ArchiveOutputStream(FileStream inner, long maxBytes, long minFreeBytes, IArchiveDiskSpaceProvider diskSpace) : Stream
    {
        private long _nextDiskCheck;
        private void EnsureCapacity(int count)
        {
            if (count < 0 || inner.Position > maxBytes - count)
                throw new ArchiveRejectedException("ARCHIVE_DISK_SPACE_LOW", "The archive temporary-file budget was exceeded.", StatusCodes.Status503ServiceUnavailable);
            if (inner.Position < _nextDiskCheck && inner.Position > 0) return;
            long? available;
            try { available = diskSpace.GetAvailableFreeSpace(inner.Name); }
            catch (Exception) { available = null; }
            if (!available.HasValue)
                throw new ArchiveRejectedException("ARCHIVE_STORAGE_UNAVAILABLE", "Archive storage availability could not be verified.", StatusCodes.Status503ServiceUnavailable);
            if (available.Value < minFreeBytes)
                throw new ArchiveRejectedException("ARCHIVE_DISK_SPACE_LOW", "The host has insufficient free space for an archive.", StatusCodes.Status503ServiceUnavailable);
            _nextDiskCheck = inner.Position + DiskCheckIntervalBytes;
        }
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(Span<byte> buffer) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) { if (value > maxBytes) throw new ArchiveRejectedException("ARCHIVE_DISK_SPACE_LOW", "The archive temporary-file budget was exceeded.", StatusCodes.Status503ServiceUnavailable); inner.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { EnsureCapacity(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { EnsureCapacity(buffer.Length); inner.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { EnsureCapacity(count); return inner.WriteAsync(buffer, offset, count, cancellationToken); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { EnsureCapacity(buffer.Length); await inner.WriteAsync(buffer, cancellationToken); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

internal static class ArchiveTestHooks
{
    public static Action<Action>? BeforePublishCompleted { get; set; }
}
