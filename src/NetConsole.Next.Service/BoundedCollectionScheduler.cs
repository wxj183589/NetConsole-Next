using System.Collections.Concurrent;
using System.Threading.Channels;

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

    public BoundedCollectionScheduler(IDeviceSessionFactory sessionFactory, int maxConcurrency, int queueCapacity, Catalog? catalog = null)
    {
        if (maxConcurrency is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        if (queueCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _sessionFactory = sessionFactory;
        _catalog = catalog;
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
        return TryScheduleCore(request, definitionId, out id);
    }

    private bool TryScheduleCore(DeviceCollectionRequest request, string? definitionId, out string id)
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

            id = definitionId is null ? Guid.NewGuid().ToString("N") : _catalog!.CreateTaskRun(definitionId);
            var item = new WorkItem(id, request, submittedMs, definitionId is not null);

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
                if (IsTerminal(item.Snapshot.State)) return false;
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
            lock (item.Gate)
            {
                item.CancelRequested = true;
                if (item.Snapshot.State == "QUEUED")
                {
                    item.Snapshot = item.Snapshot with { State = "CANCELLED", EndedMs = UtcNowMs() };
                    PersistState(item, "CANCELLED", null);
                }
            }
            item.Cancellation.Cancel();
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
                if (!TryBegin(item)) continue;
                try { await ExecuteAsync(item); }
                catch (Exception ex)
                {
                    Interlocked.Decrement(ref _active);
                    SetTerminal(item, "FAILED", null, $"Scheduler:{ex.GetType().Name}", false);
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
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || item.Cancellation.IsCancellationRequested)
        {
            terminalState = "CANCELLED";
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            terminalState = "TIMED_OUT";
            error = "Collection request exceeded its timeout.";
        }
        catch (Exception ex)
        {
            terminalState = "FAILED";
            error = ex.GetType().Name;
        }

        if (session is not null)
        {
            try { await session.DisposeAsync(); }
            catch (Exception ex)
            {
                terminalState = "FAILED";
                error = error is null ? $"SessionDispose:{ex.GetType().Name}" : $"{error}; SessionDispose:{ex.GetType().Name}";
            }
        }

        Interlocked.Decrement(ref _active);
        SetTerminal(item, terminalState, result, error, timeout.IsCancellationRequested);
    }

    private void SetState(WorkItem item, string state)
    {
        lock (item.Gate)
        {
            if (!IsTerminal(item.Snapshot.State)) item.Snapshot = item.Snapshot with { State = state };
        }
        if (item.Persisted && state == "RUNNING") PersistState(item, state, null);
    }

    private void SetTerminal(WorkItem item, string state, string? result, string? error, bool timeoutRequested)
    {
        lock (item.Gate)
        {
            if (item.CancelRequested || _shutdown.IsCancellationRequested)
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
        if (item.Persisted) PersistState(item, state, error);
        lock (_gate) PruneCompletedHistory();
    }

    private void PersistState(WorkItem item, string state, string? reason)
    {
        if (!item.Persisted || _catalog is null) return;
        try { _catalog.TransitionTaskRun(item.Snapshot.Id, state, reason); }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // The Raw/SQLite service-level gate handles storage failures; scheduler state remains visible in memory.
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

    private sealed class WorkItem(string id, DeviceCollectionRequest request, long submittedMs, bool persisted)
    {
        public object Gate { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public DeviceCollectionRequest Request { get; } = request;
        public bool Persisted { get; } = persisted;
        public bool CancelRequested { get; set; }
        public DeviceCollectionSnapshot Snapshot { get; set; } = new(id, request.Target, request.Command, "QUEUED", null, null, submittedMs, null, null);
    }
}
