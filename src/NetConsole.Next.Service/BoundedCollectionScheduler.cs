using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using System.Data.Common;

namespace NetConsole.Next.Service;

internal sealed record DeviceCollectionRequest(string Target, string Command, TimeSpan Timeout);

internal sealed record DeviceCollectionSnapshot(
    string Id,
    string Target,
    string Command,
    string State,
    string? Result,
    string? Error,
    long SubmittedMs,
    long? StartedMs,
    long? EndedMs);

internal sealed record CollectionSchedulerSnapshot(int Active, int Queued, int QueueCapacity, int MaxConcurrency, long Accepted, long Rejected);

internal interface IDeviceSessionFactory
{
    IDeviceSession Create(string target);
}

internal interface IDeviceSession : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);
    Task<string> ExecuteAsync(string command, CancellationToken cancellationToken);
}

// Shared bounded execution core for future SSH/SNMP collectors. It never runs a shell command itself.
internal sealed class BoundedCollectionScheduler : IAsyncDisposable
{
    private const int CompletedHistoryLimit = 256;
    private static readonly TimeSpan MinimumTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(10);
    private readonly IDeviceSessionFactory _sessionFactory;
    private readonly Catalog? _catalog;
    private readonly Action<Exception>? _persistenceFailure;
    private readonly ITaskResultSink? _resultSink;
    private readonly Channel<WorkItem> _queue;
    private readonly ConcurrentDictionary<string, WorkItem> _items = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly object _gate = new();
    private bool _disposed;
    private int _queued;
    private int _active;
    private long _accepted;
    private long _rejected;

    public BoundedCollectionScheduler(IDeviceSessionFactory sessionFactory, int maxConcurrency, int queueCapacity,
        Catalog? catalog = null, Action<Exception>? persistenceFailure = null, ITaskResultSink? resultSink = null)
    {
        if (maxConcurrency is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        if (queueCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _sessionFactory = sessionFactory;
        _catalog = catalog;
        _persistenceFailure = persistenceFailure;
        _resultSink = resultSink;
        MaxConcurrency = maxConcurrency;
        QueueCapacity = queueCapacity;
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(queueCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = maxConcurrency == 1,
            SingleWriter = false
        });
        _workers = Enumerable.Range(0, maxConcurrency).Select(_ => RunWorkerAsync()).ToArray();
    }

    public int QueueCapacity { get; }
    public int MaxConcurrency { get; }

    public bool TrySchedule(DeviceCollectionRequest request, out string id)
        => TryScheduleCore(request, null, out id);

    public bool TryScheduleDefinition(string definitionId, out string id)
    {
        if (_catalog is null) throw new InvalidOperationException("A Catalog is required for persisted task definitions.");
        var definition = _catalog.GetTaskDefinition(definitionId)
            ?? throw new KeyNotFoundException($"Task definition '{definitionId}' does not exist.");
        if (!definition.Enabled) throw new InvalidOperationException("Disabled task definitions cannot be scheduled.");
        if (definition.RestartPolicy != "NEVER") throw new InvalidOperationException("Unsupported task restart policy.");
        var request = new DeviceCollectionRequest(definition.Target, definition.Command,
            TimeSpan.FromMilliseconds(definition.TimeoutMs));
        return TryScheduleCore(request, definition, out id);
    }

    private bool TryScheduleCore(DeviceCollectionRequest request, TaskDefinitionSnapshot? definition, out string id)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var submittedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        id = string.Empty;

        lock (_gate)
        {
            if (_disposed)
            {
                Interlocked.Increment(ref _rejected);
                return false;
            }

            if (Volatile.Read(ref _queued) >= QueueCapacity)
            {
                Interlocked.Increment(ref _rejected);
                return false;
            }

            id = definition is null ? Guid.NewGuid().ToString("N") : _catalog!.CreateTaskRun(definition);
            var item = new WorkItem(id, request, submittedMs, definition, definition is not null);

            PruneCompletedHistory();
            _items[id] = item;
            Interlocked.Increment(ref _queued);
            if (!_queue.Writer.TryWrite(item))
            {
                Interlocked.Decrement(ref _queued);
                _items.TryRemove(id, out _);
                if (item.Persisted) PersistState(item, "CANCELLED", "Queue admission failed.");
                Interlocked.Increment(ref _rejected);
                return false;
            }
            Interlocked.Increment(ref _accepted);
            return true;
        }
    }

    public DeviceCollectionSnapshot? Get(string id) =>
        _items.TryGetValue(id, out var item) ? item.Snapshot : null;

    public IReadOnlyList<DeviceCollectionSnapshot> Recent(int limit = 25) => _items.Values
        .Select(item => item.Snapshot)
        .OrderByDescending(item => item.SubmittedMs)
        .Take(Math.Clamp(limit, 1, CompletedHistoryLimit))
        .ToArray();

    public CollectionSchedulerSnapshot Status => new(
        Volatile.Read(ref _active), Volatile.Read(ref _queued), QueueCapacity, MaxConcurrency,
        Interlocked.Read(ref _accepted), Interlocked.Read(ref _rejected));

    public bool Cancel(string id)
    {
        lock (_gate)
        {
            if (_disposed || !_items.TryGetValue(id, out var item)) return false;
            lock (item.Gate)
            {
                if (IsTerminal(item.Snapshot.State) || item.CommitStarted) return false;
                item.CancelRequested = true;
                if (item.Snapshot.State == "QUEUED")
                {
                    item.Snapshot = item.Snapshot with { State = "CANCELLED", EndedMs = UtcNowMs() };
                    PersistState(item, "CANCELLED", null);
                }
                item.Cancellation.Cancel();
                return true;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        WorkItem[] items;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            items = _items.Values.ToArray();
        }

        foreach (var item in items)
        {
            bool cancel;
            lock (item.Gate)
            {
                cancel = !item.CommitStarted;
                if (cancel) item.CancelRequested = true;
                if (item.Snapshot.State == "QUEUED")
                {
                    item.Snapshot = item.Snapshot with { State = "CANCELLED", EndedMs = UtcNowMs() };
                    PersistState(item, "CANCELLED", null);
                }
            }
            if (cancel) item.Cancellation.Cancel();
        }
        _queue.Writer.TryComplete();
        while (_queue.Reader.TryRead(out _)) Interlocked.Decrement(ref _queued);
        _shutdown.Cancel();
        try { await Task.WhenAll(_workers); }
        catch (OperationCanceledException) { }
        foreach (var item in items) item.Cancellation.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                Interlocked.Decrement(ref _queued);
                var active = false;
                try
                {
                    if (!TryBegin(item)) continue;
                    active = true;
                    await ExecuteAsync(item);
                }
                catch (Exception ex)
                {
                    try { SetTerminal(item, "FAILED", null, $"Scheduler:{ex.GetType().Name}", false); }
                    catch (Exception persistenceError)
                    {
                        SetTerminalInMemory(item, "FAILED", null, $"TaskStatePersistence:{persistenceError.GetType().Name}");
                    }
                }
                finally
                {
                    if (active) Interlocked.Decrement(ref _active);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Host shutdown cancels active sessions; queued requests are marked cancelled by DisposeAsync.
        }
    }

    private bool TryBegin(WorkItem item)
    {
        lock (item.Gate)
        {
            if (item.Snapshot.State != "QUEUED") return false;
            item.Snapshot = item.Snapshot with { State = "CONNECTING", StartedMs = UtcNowMs() };
            if (item.Persisted) PersistState(item, "CONNECTING", null);
            Interlocked.Increment(ref _active);
            return true;
        }
    }

    private async Task ExecuteAsync(WorkItem item)
    {
        IDeviceSession? session = null;
        string terminalState;
        string? result = null;
        string? error = null;
        var captureStatus = "COMPLETE";
        var resultPersistedAtomically = false;
        using var timeout = new CancellationTokenSource(item.Request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, item.Cancellation.Token, timeout.Token);

        try
        {
            session = _sessionFactory.Create(item.Request.Target);
            await session.ConnectAsync(linked.Token);
            SetState(item, "RUNNING");
            result = await session.ExecuteAsync(item.Request.Command, linked.Token);
            terminalState = "SUCCEEDED";
        }
        catch (H3cPartialOutputException ex)
        {
            H3cCollectionMetrics.RecordFailure(ex);
            result = ex.Output;
            captureStatus = "PARTIAL";
            terminalState = ex.IsCancellation ? timeout.IsCancellationRequested ? "TIMED_OUT" : "CANCELLED" : "FAILED";
            error = ex.FailureCode;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || item.Cancellation.IsCancellationRequested)
        {
            H3cCollectionMetrics.RecordFailure(new OperationCanceledException());
            terminalState = "CANCELLED";
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            H3cCollectionMetrics.RecordFailure(new TimeoutException());
            terminalState = "TIMED_OUT";
            error = "Collection request exceeded its timeout.";
        }
        catch (TimeoutException ex)
        {
            if (item.Definition?.TaskType == "H3C_SSH_READONLY") H3cCollectionMetrics.RecordFailure(ex);
            terminalState = "TIMED_OUT";
            error = ex.GetType().Name;
        }
        catch (Exception ex)
        {
            terminalState = "FAILED";
            error = ex.GetType().Name;
            if (item.Definition?.TaskType == "H3C_SSH_READONLY") H3cCollectionMetrics.RecordFailure(ex);
            if (ex is DbException or IOException or UnauthorizedAccessException)
            {
                try { _persistenceFailure?.Invoke(ex); }
                catch { }
            }
        }

        if (session is not null)
        {
            try { await session.DisposeAsync(); }
            catch (Exception ex)
            {
                terminalState = "FAILED";
                error = error is null ? $"SessionDispose:{ex.GetType().Name}" : $"{error}; SessionDispose:{ex.GetType().Name}";
                if (item.Definition?.TaskType == "H3C_SSH_READONLY") H3cCollectionMetrics.RecordFailure(ex);
            }
        }

        if (result is not null && item.Definition?.TaskType == "H3C_SSH_READONLY")
        {
            try
            {
                if (_resultSink is null)
                    throw new InvalidOperationException("H3C task cannot complete without a durable Raw result sink.");
                BeginRawCommit(item, timeout, ref terminalState, ref error);
                var manifest = await _resultSink.PersistAsync(item.Snapshot.Id, item.Definition, result, captureStatus);
                var sqliteStarted = Stopwatch.GetTimestamp();
                _catalog!.CompleteTaskRunWithRawOutput(item.Snapshot.Id, manifest, terminalState, error);
                H3cCollectionMetrics.RecordSqliteCommit(Stopwatch.GetElapsedTime(sqliteStarted));
                resultPersistedAtomically = true;
            }
            catch (Exception ex)
            {
                terminalState = "FAILED";
                error = $"ResultPersistence:{ex.GetType().Name}";
                H3cCollectionMetrics.RecordFailure(ex);
                if (ex is DbException or IOException or UnauthorizedAccessException)
                {
                    try { _persistenceFailure?.Invoke(ex); }
                    catch { }
                }
            }
        }

        SetTerminal(item, terminalState, result, error,
            timeoutRequested: !item.CommitStarted && timeout.IsCancellationRequested,
            stateAlreadyPersisted: resultPersistedAtomically);
    }

    private void BeginRawCommit(WorkItem item, CancellationTokenSource timeout,
        ref string terminalState, ref string? error)
    {
        lock (item.Gate)
        {
            if (item.CancelRequested || _shutdown.IsCancellationRequested)
            {
                terminalState = "CANCELLED";
                error = "Cancelled after command capture; output is being durably recorded.";
            }
            else if (timeout.IsCancellationRequested && terminalState == "SUCCEEDED")
            {
                terminalState = "TIMED_OUT";
                error = "Collection request exceeded its timeout before Raw commit.";
            }
            item.CommitStarted = true;
        }
        // The command deadline covers connection and command execution. Once the
        // captured response enters the durable commit phase, finish or report a
        // storage failure instead of abandoning a partially written Raw result.
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
    }

    private void SetState(WorkItem item, string state)
    {
        lock (item.Gate)
        {
            if (!IsTerminal(item.Snapshot.State)) item.Snapshot = item.Snapshot with { State = state };
        }
        if (item.Persisted && state == "RUNNING") PersistState(item, state, null);
    }

    private void SetTerminal(WorkItem item, string state, string? result, string? error,
        bool timeoutRequested, bool stateAlreadyPersisted = false)
    {
        if (item.Definition?.TaskType == "H3C_SSH_READONLY")
            H3cCollectionMetrics.RecordTask(TimeSpan.FromMilliseconds(Math.Max(0, UtcNowMs() - item.Snapshot.SubmittedMs)));
        lock (item.Gate)
        {
            if (!item.CommitStarted && (item.CancelRequested || _shutdown.IsCancellationRequested))
            {
                state = "CANCELLED";
                result = null;
                error = null;
            }
            else if (timeoutRequested && state == "SUCCEEDED")
            {
                state = "TIMED_OUT";
                result = null;
                error = "Collection request exceeded its timeout.";
            }
            item.Snapshot = item.Snapshot with { State = state, Result = result, Error = error, EndedMs = UtcNowMs() };
        }
        if (item.Persisted && !stateAlreadyPersisted) PersistState(item, state, error);
        lock (_gate) PruneCompletedHistory();
    }

    private void SetTerminalInMemory(WorkItem item, string state, string? result, string? error)
    {
        lock (item.Gate)
            item.Snapshot = item.Snapshot with { State = state, Result = result, Error = error, EndedMs = UtcNowMs() };
        lock (_gate) PruneCompletedHistory();
    }

    private void PersistState(WorkItem item, string state, string? reason)
    {
        if (!item.Persisted || _catalog is null) return;
        try { _catalog.TransitionTaskRun(item.Snapshot.Id, state, reason); }
        catch (Exception ex)
        {
            try { _persistenceFailure?.Invoke(ex); }
            catch { /* Preserve the original persistence exception. */ }
            throw;
        }
    }

    private void PruneCompletedHistory()
    {
        var completed = _items.Values
            .Where(item => IsTerminal(item.Snapshot.State))
            .OrderBy(item => item.Snapshot.EndedMs ?? item.Snapshot.SubmittedMs)
            .ToArray();
        foreach (var item in completed.Take(Math.Max(0, completed.Length - CompletedHistoryLimit)))
            _items.TryRemove(item.Snapshot.Id, out _);
    }

    private static void ValidateRequest(DeviceCollectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Target) || request.Target.Length > 253 || request.Target.Any(char.IsControl))
            throw new ArgumentException("Target must be a nonempty host or device identifier of at most 253 characters.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Command) || request.Command.Length > 4096 || request.Command.Contains('\r') || request.Command.Contains('\n'))
            throw new ArgumentException("Command must contain one nonempty line of at most 4096 characters.", nameof(request));
        if (request.Timeout < MinimumTimeout || request.Timeout > MaximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(request), $"Timeout must be from {MinimumTimeout.TotalMilliseconds:0}ms to {MaximumTimeout.TotalMinutes:0} minutes.");
    }

    private static bool IsTerminal(string state) => state is "SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED";
    private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class WorkItem(string id, DeviceCollectionRequest request, long submittedMs,
        TaskDefinitionSnapshot? definition, bool persisted)
    {
        public object Gate { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public DeviceCollectionRequest Request { get; } = request;
        public TaskDefinitionSnapshot? Definition { get; } = definition;
        public bool Persisted { get; } = persisted;
        public bool CancelRequested { get; set; }
        public bool CommitStarted { get; set; }
        public DeviceCollectionSnapshot Snapshot { get; set; } = new(id, request.Target, request.Command, "QUEUED", null, null, submittedMs, null, null);
    }
}
