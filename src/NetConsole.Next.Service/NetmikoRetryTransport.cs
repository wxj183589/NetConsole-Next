namespace NetConsole.Next.Service;

internal sealed class NetmikoReconnectBudget
{
    private int _used;
    internal bool TryReserve(out TimeSpan backoff)
    {
        backoff = TimeSpan.FromMilliseconds(_used == 0 ? 250 : 1000);
        if (_used >= 2) return false;
        _used++;
        return true;
    }
}

// A bounded recovery policy around the existing worker, not a connection pool.
// Only a lost connection permits replay, and only for the selected vendor's
// exact read-only command policy. Interactive terminals never use this wrapper.
internal sealed class NetmikoRetryTransport(
    Func<IH3cSshTransport> createTransport,
    Func<string, bool> canReplay,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IH3cSshTransport
{
    private readonly object _lifetimeGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _stopped = new();
    private IH3cSshTransport? _current;
    private Func<string?, string?, bool>? _verifyHostKey;
    private bool _connectStarted, _connected, _disposed;
    private int _reconnects;

    internal NetmikoReconnectBudget ReconnectBudget { get; set; } = new();

    internal Action? BeforeReconnect { get; set; }
    internal Action? Reconnected { get; set; }
    internal Action? ReconnectFailed { get; set; }

    // A connected logical session may have lost its worker between commands.
    // Keep it callable so ExecuteAsync can detect and recover that loss.
    public bool IsConnected { get { lock (_lifetimeGate) return _connected && !_disposed; } }

    public async Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifyHostKey);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                linked.Token.ThrowIfCancellationRequested();
                if (_connectStarted) throw new InvalidOperationException("Netmiko session can only connect once.");
                _connectStarted = true;
                _verifyHostKey = verifyHostKey;
            }
            while (true)
            {
                try
                {
                    if (_reconnects > 0) BeforeReconnect?.Invoke();
                    linked.Token.ThrowIfCancellationRequested();
                    await CreateCurrent(linked.Token).ConnectAsync(verifyHostKey, linked.Token).ConfigureAwait(false);
                    lock (_lifetimeGate)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        _connected = true;
                    }
                    return;
                }
                catch (Exception error)
                {
                    ReleaseCurrent();
                    ReconnectFailed?.Invoke();
                    linked.Token.ThrowIfCancellationRequested();
                    if (!CanRetryConnect(error) || !TryReserveReconnect(out var backoff)) throw;
                    await DelayAsync(backoff, linked.Token).ConfigureAwait(false);
                }
            }
        }
        finally { _operationGate.Release(); }
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                linked.Token.ThrowIfCancellationRequested();
                if (!_connected) throw new InvalidOperationException("Netmiko session is not connected.");
            }
            while (true)
            {
                try
                {
                    var current = _current ?? throw new H3cConnectionLostException();
                    return await current.ExecuteAsync(command, linked.Token).ConfigureAwait(false);
                }
                catch (H3cConnectionLostException) when (canReplay(command))
                {
                    await ReconnectAsync(linked.Token).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            lock (_lifetimeGate) _connected = false;
            ReleaseCurrent();
            throw;
        }
        finally { _operationGate.Release(); }
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        ReleaseCurrent();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReserveReconnect(out var backoff)) throw new H3cConnectionLostException();
            await DelayAsync(backoff, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Includes the caller's live authorization check. A rejected
            // boundary must stop before creating a worker or opening a socket.
            BeforeReconnect?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await CreateCurrent(cancellationToken).ConnectAsync(_verifyHostKey!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                ReleaseCurrent();
                ReconnectFailed?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanRetryConnect(error)) throw;
                continue;
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Reconnected?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            catch
            {
                ReleaseCurrent();
                ReconnectFailed?.Invoke();
                throw;
            }
        }
    }

    private IH3cSshTransport CreateCurrent(CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stopped.Token.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            return _current = createTransport();
        }
    }

    private void ReleaseCurrent()
    {
        IH3cSshTransport? current;
        lock (_lifetimeGate) { current = _current; _current = null; }
        current?.Dispose();
    }

    private bool TryReserveReconnect(out TimeSpan backoff)
    {
        if (!ReconnectBudget.TryReserve(out backoff)) return false;
        _reconnects++;
        return true;
    }

    private Task DelayAsync(TimeSpan backoff, CancellationToken cancellationToken) =>
        delay is null ? Task.Delay(backoff, cancellationToken) : delay(backoff, cancellationToken);

    internal static bool CanRetryConnect(Exception error) => error is H3cConnectionLostException or H3cConnectionTimeoutException ||
        error is H3cJumpHostConnectionException { Code: "JUMP_HOST_UNREACHABLE" or "JUMP_HOST_CHANNEL_FAILED" };

    public void Abort()
    {
        lock (_lifetimeGate)
        {
            _connected = false;
            _stopped.Cancel();
            _current?.Abort();
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _connected = false;
            _stopped.Cancel();
        }
        ReleaseCurrent();
        // In-flight operations own the semaphore/token registrations until
        // they unwind. Disposing those primitives here would race cancellation.
    }
}
