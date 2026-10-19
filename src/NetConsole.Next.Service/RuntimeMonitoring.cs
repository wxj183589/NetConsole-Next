using System.Collections.Concurrent;
using System.Diagnostics;

namespace NetConsole.Next.Service;

internal sealed class RuntimeStatus
{
    private string _serviceState = "starting";
    private string _collectionState = "starting";
    private string? _collectionError;
    private string? _faultError;
    private readonly ConcurrentDictionary<string, string> _degradationReasons = new(StringComparer.Ordinal);

    public string ServiceState => Volatile.Read(ref _serviceState);
    public string CollectionState => Volatile.Read(ref _collectionState);
    public string? CollectionError => Volatile.Read(ref _collectionError);
    public string HealthState => Volatile.Read(ref _faultError) is not null ? "FAULTED" :
        _degradationReasons.IsEmpty ? "HEALTHY" : "DEGRADED";
    public string? HealthError => Volatile.Read(ref _faultError) ??
        (_degradationReasons.IsEmpty ? null : string.Join("; ", _degradationReasons.OrderBy(x => x.Key).Select(x => x.Value)));
    public bool CollectionAllowed => HealthState != "FAULTED";
    public void SetService(string state) => Interlocked.Exchange(ref _serviceState, state);
    public void SetHealth(string state, string? error = null)
    {
        if (state == "FAULTED")
        {
            SetFaulted(error ?? "Service health is faulted.");
            return;
        }
        if (state == "DEGRADED") SetDegradation("general", error ?? "Service health is degraded.");
        else if (state == "HEALTHY") ClearDegradation("general");
        else throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported health state.");
    }
    public void SetFaulted(string error)
    {
        Interlocked.Exchange(ref _faultError, error);
        SetCollection("faulted", error);
    }
    public void SetDegradation(string key, string? reason)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A degradation key is required.", nameof(key));
        if (reason is null) _degradationReasons.TryRemove(key, out _);
        else _degradationReasons[key] = reason;
    }
    public void ClearDegradation(string key) => _degradationReasons.TryRemove(key, out _);
    public void SetInterruptedTaskRuns(int count) => SetDegradation("interrupted-task-runs",
        count > 0 ? $"{count} task run(s) were interrupted by the previous process exit; automatic replay is disabled." : null);
    public void AssessRecovery(RawRecoverySummary recovery, int unresolvedIntegrityIssues)
    {
        if (HealthState == "FAULTED") return;
        if (recovery.Issues.Any(issue => issue.Critical))
            SetFaulted("Critical storage/recovery failure; new collection is disabled.");
        else
        {
            ReassessIntegrityIssues(unresolvedIntegrityIssues);
            if (recovery.Issues.Any(issue => issue.Category == "RECOVERY_INTERRUPTED"))
                SetDegradation("recovery-interrupted", "The previous Raw recovery was interrupted; this process completed its replay and remains DEGRADED until a clean restart.");
        }
    }

    public void ReassessIntegrityIssues(int unresolvedIntegrityIssues)
    {
        if (HealthState == "FAULTED") return;
        SetDegradation("raw-integrity", unresolvedIntegrityIssues > 0
            ? $"{unresolvedIntegrityIssues} unresolved Raw integrity issue(s) need attention."
            : null);
    }
    public void SetCollection(string state, string? error = null)
    {
        Interlocked.Exchange(ref _collectionState, state);
        Interlocked.Exchange(ref _collectionError, error);
    }
}

internal sealed record ResourceSnapshot(double? ProcessCpuPercent, long WorkingSetBytes, int ThreadCount,
    int? HandleCount, long? DataVolumeFreeBytes, long SampledAtMs);

internal sealed class RuntimeResources
{
    private ResourceSnapshot _snapshot = new(null, 0, 0, null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    public ResourceSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public void Update(ResourceSnapshot snapshot) => Interlocked.Exchange(ref _snapshot, snapshot);
}

internal sealed class ProcessResourceMonitor(RuntimeSettings settings, RuntimeResources resources,
    ILogger<ProcessResourceMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var process = Process.GetCurrentProcess();
        var previousCpu = process.TotalProcessorTime;
        var previousTick = Stopwatch.GetTimestamp();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                process.Refresh();
                var nowCpu = process.TotalProcessorTime;
                var nowTick = Stopwatch.GetTimestamp();
                var elapsed = (nowTick - previousTick) / (double)Stopwatch.Frequency;
                var cpuDelta = (nowCpu - previousCpu).TotalSeconds;
                var cpu = elapsed > 0 ? Math.Clamp(cpuDelta / elapsed / Environment.ProcessorCount * 100d, 0d, 100d) : 0d;
                long? freeBytes = null;
                try
                {
                    var volume = Path.GetPathRoot(settings.DataRoot);
                    if (!string.IsNullOrWhiteSpace(volume)) freeBytes = new DriveInfo(volume).AvailableFreeSpace;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    logger.LogDebug(ex, "Unable to read free space for the data volume.");
                }

                int? handles = null;
                if (OperatingSystem.IsWindows())
                {
                    try { handles = process.HandleCount; }
                    catch (InvalidOperationException) { }
                }
                resources.Update(new(cpu, process.WorkingSet64, process.Threads.Count, handles, freeBytes,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                previousCpu = nowCpu;
                previousTick = nowTick;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning(ex, "Could not sample process resources.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static SingleInstanceGuard Acquire()
    {
        var name = OperatingSystem.IsWindows() ? "Global\\NetConsoleNext.Service" : "NetConsoleNext.Service";
        var mutex = new Mutex(false, name);
        try
        {
            if (mutex.WaitOne(0)) return new(mutex, true);
        }
        catch (AbandonedMutexException)
        {
            return new(mutex, true);
        }
        mutex.Dispose();
        throw new InvalidOperationException("Another NetConsole Next service instance is already running.");
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _ownsMutex = false;
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}

internal sealed class ServiceRunLifecycle(Catalog catalog, RuntimeStatus status,
    ILogger<ServiceRunLifecycle> logger) : IHostedService
{
    private string? _runId;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!catalog.Available)
        {
            status.SetService("running");
            logger.LogCritical("Service host is running in FAULTED diagnostic mode without SQLite; no collection tasks will start.");
            return Task.CompletedTask;
        }
        _runId = catalog.StartTaskRun(null, "CORE_SERVICE");
        status.SetService("running");
        logger.LogInformation("Core service run {RunId} started.", _runId);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_runId is not null) catalog.EndTaskRun(_runId, "STOPPED");
        status.SetService("stopped");
        logger.LogInformation("Core service stopped.");
        return Task.CompletedTask;
    }
}

internal sealed class RawIntegrityAuditService(Catalog catalog, RuntimeStatus status,
    ILogger<RawIntegrityAuditService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!catalog.Available) return;
        try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = catalog.AuditOneFinalizedSegment();
                if (result is not null)
                {
                    logger.LogInformation("Low-priority Raw integrity audit {Outcome} for {RelativePath}: {Detail}",
                        result.Outcome, result.RelativePath, result.Detail);
                    status.ClearDegradation("raw-maintenance-runtime");
                    status.ReassessIntegrityIssues(catalog.GetUnresolvedIntegrityIssueCount());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
            {
                status.SetDegradation("raw-maintenance-runtime", "Low-priority Raw integrity audit failed; the next completed audit must clear this state.");
                logger.LogWarning(ex, "Low-priority Raw integrity audit failed; Raw files were retained.");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
