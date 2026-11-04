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
    private readonly ITaskScopeAuthorizer? _scopeAuthorizer;
    private readonly Channel<WorkItem> _queue;
    private readonly ConcurrentDictionary<string, WorkItem> _items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly object _gate = new();
    private bool _disposed;
    private int _queued;
    private int _active;
    private long _accepted;
    private long _rejected;

    public BoundedCollectionScheduler(IDeviceSessionFactory sessionFactory, int maxConcurrency, int queueCapacity,
        Catalog? catalog = null, Action<Exception>? persistenceFailure = null, ITaskResultSink? resultSink = null,
        ITaskScopeAuthorizer? scopeAuthorizer = null)
    {
        if (maxConcurrency is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        if (queueCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _sessionFactory = sessionFactory;
        _catalog = catalog;
        _persistenceFailure = persistenceFailure;
        _resultSink = resultSink;
        _scopeAuthorizer = scopeAuthorizer;
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

    public bool TryScheduleDefinition(string definitionId, out string id, string? actorUserId = null)
    {
        if (_catalog is null) throw new InvalidOperationException("A Catalog is required for persisted task definitions.");
        var definition = _catalog.GetTaskDefinition(definitionId)
            ?? throw new KeyNotFoundException($"Task definition '{definitionId}' does not exist.");
        if (!definition.Enabled) throw new InvalidOperationException("Disabled task definitions cannot be scheduled.");
        if (definition.RestartPolicy != "NEVER") throw new InvalidOperationException("Unsupported task restart policy.");
        var request = new DeviceCollectionRequest(definition.Target, definition.Command,
            TimeSpan.FromMilliseconds(definition.TimeoutMs));
        var scope = definition.TaskType == "H3C_SSH_READONLY" ? _scopeAuthorizer?.CaptureExecution(definition, actorUserId) : null;
        return TryScheduleCore(request, definition, out id, scope);
    }

    private bool TryScheduleCore(DeviceCollectionRequest request, TaskDefinitionSnapshot? definition, out string id,
        TaskExecutionScope? scope = null)
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

            id = definition is null ? Guid.NewGuid().ToString("N") : _catalog!.CreateTaskRun(definition, scope?.ActorUserId);
            var item = new WorkItem(id, request, submittedMs, definition, definition is not null, scope);

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

    public bool TryRetryTaskRun(string runId, out string retryId, string? actorUserId = null)
        => TryReplayTaskRun(runId, out retryId, actorUserId, resumeInterrupted: false);

    public bool TryResumeInterruptedTaskRun(string runId, out string resumedId, string? actorUserId = null)
        => TryReplayTaskRun(runId, out resumedId, actorUserId, resumeInterrupted: true);

    private bool TryReplayTaskRun(string runId, out string newRunId, string? actorUserId, bool resumeInterrupted)
    {
        newRunId = string.Empty;
        if (_catalog is null || string.IsNullOrWhiteSpace(runId)) return false;
        var previous = _catalog.GetTaskRun(runId);
        if (previous is null || previous.DefinitionId is null) return false;
        if (resumeInterrupted ? previous.State != "INTERRUPTED" :
            previous.State is not ("FAILED" or "TIMED_OUT" or "CANCELLED" or "INTERRUPTED")) return false;
        try
        {
            var accepted = TryScheduleDefinition(previous.DefinitionId, out newRunId, actorUserId);
            if (accepted)
            {
                _catalog.TryRecordDeviceAuditEvent(actorUserId ?? "collector", resumeInterrupted ? "TASK_RESUME_REQUESTED" : "TASK_RETRY_REQUESTED",
                    "ACCEPTED", previous.Id, newRunId, Array.Empty<string>(), $"previousState={previous.State}");
            }
            return accepted;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or TaskScopeRevokedException)
        {
            _catalog.TryRecordDeviceAuditEvent(actorUserId ?? "collector", resumeInterrupted ? "TASK_RESUME_REQUESTED" : "TASK_RETRY_REQUESTED",
                "DENIED", previous.Id, null, Array.Empty<string>(), ex.GetType().Name);
            return false;
        }
    }

    public CollectionSchedulerSnapshot Status => new(
        Volatile.Read(ref _active), Volatile.Read(ref _queued), QueueCapacity, MaxConcurrency,
        Interlocked.Read(ref _accepted), Interlocked.Read(ref _rejected));

    public bool Cancel(string id)
    {
        string? scopeReason = null;
        if (_items.TryGetValue(id, out var candidate) && candidate.Scope is not null)
        {
            try { _scopeAuthorizer!.Require(candidate.Scope, candidate.Request.Target, "CANCEL_BOUNDARY"); }
            catch (TaskScopeRevokedException ex) { scopeReason = ex.Reason; }
        }
        lock (_gate)
        {
            if (_disposed || !_items.TryGetValue(id, out var item)) return false;
            lock (item.Gate)
            {
                if (IsTerminal(item.Snapshot.State) || item.CommitStarted) return false;
                item.CancelRequested = true;
                item.ScopeCancellationReason = scopeReason;
                if (item.Snapshot.State == "QUEUED")
                {
                    var reason = scopeReason is null ? null : scopeReason + ";CANCEL_REQUESTED";
                    item.Snapshot = item.Snapshot with { State = "CANCELLED", EndedMs = UtcNowMs(), Error = reason };
                    PersistState(item, "CANCELLED", reason);
                }
                if (scopeReason is not null && item.Scope is not null)
                    _catalog!.TryRecordDeviceAuditEvent(item.Scope.ActorUserId, "TASK_SCOPE_REVOKED", "DENIED",
                        item.Snapshot.Id, item.Definition?.Id, Array.Empty<string>(), scopeReason + ";CANCEL_REQUESTED");
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
        var gate = _deviceGates.GetOrAdd(item.Request.Target, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(item.Cancellation.Token);
        try
        {
            await ExecuteCoreAsync(item);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ExecuteCoreAsync(WorkItem item)
    {
        IDeviceSession? session = null;
        string terminalState;
        string? result = null;
        string? error = null;
        var commandOutputs = new List<(string Command, string Output, int Index, string CaptureStatus)>();
        var currentCommandIndex = 0;
        var commandPlan = item.Definition?.CommandPlan ?? [item.Request.Command];
        var resultPersistedAtomically = false;
        var operationStarted = false;
        using var timeout = new CancellationTokenSource(item.Request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, item.Cancellation.Token, timeout.Token);

        try
        {
            if (item.Scope is not null) _scopeAuthorizer!.Require(item.Scope, item.Request.Target, "BEFORE_CONNECT");
            session = _sessionFactory.Create(item.Request.Target);
            if (item.Scope is not null) _scopeAuthorizer!.Require(item.Scope, item.Request.Target, "BEFORE_CONNECT");
            operationStarted = true;
            await session.ConnectAsync(linked.Token);
            if (item.Scope is not null) _scopeAuthorizer!.Require(item.Scope, item.Request.Target, "BEFORE_COMMAND");
            SetState(item, "RUNNING");
            for (currentCommandIndex = 0; currentCommandIndex < commandPlan.Count; currentCommandIndex++)
            {
                var command = commandPlan[currentCommandIndex];
                var output = await session.ExecuteAsync(command, linked.Token);
                commandOutputs.Add((command, output, currentCommandIndex, "COMPLETE"));
            }
            result = commandOutputs.Count == 1 ? commandOutputs[0].Output : null;
            if (item.Scope is not null) _scopeAuthorizer!.Require(item.Scope, item.Request.Target, "AFTER_COMMAND");
            terminalState = "SUCCEEDED";
        }
        catch (TaskScopeRevokedException ex)
        {
            terminalState = "CANCELLED";
            error = operationStarted ? "REVOKED_AFTER_START:" + ex.Reason : ex.Reason;
        }
        catch (H3cPartialOutputException ex)
        {
            H3cCollectionMetrics.RecordFailure(ex);
            if (!string.IsNullOrEmpty(ex.Output))
                commandOutputs.Add((commandPlan[Math.Min(currentCommandIndex, commandPlan.Count - 1)], ex.Output,
                    Math.Min(currentCommandIndex, commandPlan.Count - 1), "PARTIAL"));
            result = commandOutputs.Count == 1 ? commandOutputs[0].Output : null;
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

        if (item.Scope is not null)
        {
            try { _scopeAuthorizer!.Require(item.Scope, item.Request.Target, "BEFORE_RESULT_COMMIT"); }
            catch (TaskScopeRevokedException ex)
            {
                terminalState = "CANCELLED";
                var reason = operationStarted ? "REVOKED_AFTER_START:" + ex.Reason : ex.Reason;
                if (!IsScopeReason(error)) error = error is null ? reason : reason + ";PRIOR_OUTCOME=" + error;
            }
            if (IsScopeReason(error))
                _catalog!.TryRecordDeviceAuditEvent(item.Scope.ActorUserId, "TASK_SCOPE_REVOKED", "DENIED",
                    item.Snapshot.Id, item.Definition?.Id, Array.Empty<string>(), error);
        }

        // Any command output attached to a non-successful run is evidence of a
        // partial collection boundary. Preserve it independently, but never let
        // it enter the complete-facts publication path.
        if (terminalState != "SUCCEEDED" && commandOutputs.Count > 0)
            commandOutputs = commandOutputs.Select(item => (item.Command, item.Output, item.Index, "PARTIAL")).ToList();

        if (commandOutputs.Count > 0 && item.Definition?.TaskType == "H3C_SSH_READONLY")
        {
            try
            {
                if (_resultSink is null)
                    throw new InvalidOperationException("H3C task cannot complete without a durable Raw result sink.");
                BeginRawCommit(item, timeout, ref terminalState, ref error);
                var manifests = new List<H3cRawOutputManifest>(commandOutputs.Count);
                foreach (var commandOutput in commandOutputs)
                {
                    var manifest = await _resultSink.PersistCommandAsync(item.Snapshot.Id, item.Definition,
                        commandOutput.Command, commandOutput.Output, commandOutput.CaptureStatus, commandOutput.Index);
                    manifests.Add(manifest);
                }
                var sqliteStarted = Stopwatch.GetTimestamp();
                if (item.Definition.CommandPlan.Count == 1 && manifests.Count == 1)
                    _catalog!.CompleteTaskRunWithRawOutput(item.Snapshot.Id, manifests[0], terminalState, error);
                else
                    _catalog!.CompleteTaskRunWithRawOutputs(item.Snapshot.Id, manifests, terminalState, error);
                H3cCollectionMetrics.RecordSqliteCommit(Stopwatch.GetElapsedTime(sqliteStarted));
                resultPersistedAtomically = true;
                if (terminalState == "SUCCEEDED")
                {
                    try
                    {
                        if (item.Scope is not null) _scopeAuthorizer!.Require(item.Scope, item.Request.Target, "BEFORE_FACT_PUBLICATION");
                        var publication = _catalog.PublishH3cTaskFacts(item.Snapshot.Id);
                        _catalog.TryRecordDeviceAuditEvent(item.Scope?.ActorUserId ?? "collector", "H3C_FACT_PUBLICATION",
                            publication.Status, item.Snapshot.Id, item.Definition.Id, Array.Empty<string>(), "facts=" + publication.FactCount);
                    }
                    catch (Exception ex)
                    {
                        _catalog.TryRecordDeviceAuditEvent(item.Scope?.ActorUserId ?? "collector", "H3C_FACT_PUBLICATION",
                            ex is TaskScopeRevokedException ? "SCOPE_REVOKED" : "REPAIR_REQUIRED",
                            item.Snapshot.Id, item.Definition.Id, Array.Empty<string>(), ex.GetType().Name);
                    }
                }
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
                error ??= item.ScopeCancellationReason;
                error = IsScopeReason(error) ? error + ";CANCEL_REQUESTED" : "Cancelled after command capture; output is being durably recorded.";
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
                error ??= item.ScopeCancellationReason;
                error = IsScopeReason(error) ? error + ";CANCEL_REQUESTED" : null;
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
        try { _catalog.TransitionTaskRun(item.Snapshot.Id, state, reason, (long)item.Request.Timeout.TotalMilliseconds); }
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
    private static bool IsScopeReason(string? reason) => reason is not null &&
        (reason.StartsWith("TASK_", StringComparison.Ordinal) || reason.StartsWith("REVOKED_AFTER_START:", StringComparison.Ordinal));
    private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class WorkItem(string id, DeviceCollectionRequest request, long submittedMs,
        TaskDefinitionSnapshot? definition, bool persisted, TaskExecutionScope? scope)
    {
        public object Gate { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public DeviceCollectionRequest Request { get; } = request;
        public TaskDefinitionSnapshot? Definition { get; } = definition;
        public bool Persisted { get; } = persisted;
        public TaskExecutionScope? Scope { get; } = scope;
        public bool CancelRequested { get; set; }
        public bool CommitStarted { get; set; }
        public string? ScopeCancellationReason { get; set; }
        public DeviceCollectionSnapshot Snapshot { get; set; } = new(id, request.Target, request.Command, "QUEUED", null, null, submittedMs, null, null);
    }
}
