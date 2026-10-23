using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace NetConsole.Next.Service;

internal sealed class TerminalSessionManager : IAsyncDisposable
{
    private const int MaximumSessions = 8;
    private readonly Catalog _catalog;
    private readonly RuntimeSettings _settings;
    private readonly IH3cCredentialProvider _credentials;
    private readonly ConcurrentDictionary<string, TerminalSessionRuntime> _sessions = new(StringComparer.Ordinal);

    public TerminalSessionManager(Catalog catalog, RuntimeSettings settings, IH3cCredentialProvider credentials)
    {
        _catalog = catalog;
        _settings = settings;
        _credentials = credentials;
    }

    public IReadOnlyList<TerminalSessionSnapshot> Recent(string? targetId = null) =>
        _catalog.RecentTerminalSessions(targetId);

    public TerminalSessionRuntime? Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public async Task<bool> CloseAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return false;
        await session.CloseForUserAsync().ConfigureAwait(false);
        return true;
    }

    public string Create(TerminalSessionRequest request)
    {
        if (request.Mode != "RESTRICTED_READONLY")
            throw new ArgumentException("Only RESTRICTED_READONLY terminal sessions are enabled.", nameof(request));
        var requestedProtocol = request.Protocol?.Trim().ToUpperInvariant();
        var autoProtocol = string.Equals(requestedProtocol, "AUTO", StringComparison.Ordinal);
        TerminalProtocol protocol = TerminalProtocol.Ssh;
        if (!autoProtocol && !TerminalProtocolNames.TryParse(requestedProtocol ?? string.Empty, out protocol))
            throw new ArgumentException("Protocol must be AUTO, SSH, TELNET or SERIAL.", nameof(request));
        if (_sessions.Count >= MaximumSessions)
            throw new InvalidOperationException("The terminal session limit has been reached.");

        H3cDeviceProfileSnapshot? profile = null;
        TerminalSerialConfiguration? serial = null;
        string targetName;
        string? host = null;
        int? port = null;
        string? serialPort = null;
        if (autoProtocol && !string.IsNullOrWhiteSpace(request.SerialPort))
            throw new ArgumentException("AUTO protocol selection is available only for registered network devices.", nameof(request));
        if (autoProtocol && protocol == TerminalProtocol.Ssh)
        {
            if (profile is null && string.IsNullOrWhiteSpace(request.SerialPort))
            {
                profile = _catalog.GetH3cDeviceProfile(request.TargetId)
                    ?? throw new KeyNotFoundException("The target is not an authorized registered device.");
                if (!profile.Enabled) throw new InvalidOperationException("The registered device is disabled.");
            }
            if (profile is not null)
                protocol = profile.TerminalProtocol == "TELNET" ? TerminalProtocol.Telnet : TerminalProtocol.Ssh;
        }
        if (protocol is TerminalProtocol.Ssh or TerminalProtocol.Telnet)
        {
            profile = _catalog.GetH3cDeviceProfile(request.TargetId)
                ?? throw new KeyNotFoundException("The target is not an authorized registered device.");
            if (!profile.Enabled) throw new InvalidOperationException("The registered device is disabled.");
            targetName = profile.Host;
            host = profile.Host;
            port = profile.Port;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.SerialPort))
                throw new ArgumentException("A serial port is required.", nameof(request));
            if (request.SerialPort.StartsWith("SIMULATED-", StringComparison.OrdinalIgnoreCase) &&
                !_settings.TerminalDevelopmentSimulationEnabled)
                throw new InvalidOperationException("Simulated serial ports are available only in explicit Development test mode.");
            serial = new TerminalSerialConfiguration(request.SerialPort.Trim(), request.BaudRate, request.DataBits,
                request.StopBits, request.Parity, request.Handshake, request.DtrEnable, request.RtsEnable);
            serialPort = serial.PortName;
            targetName = serial.PortName;
        }

        var id = Guid.NewGuid().ToString("N");
        var recording = new TerminalRecordingWriter(_settings.DataRoot, id);
        var snapshot = new TerminalSessionSnapshot(id, request.TargetId, targetName,
            TerminalProtocolNames.ToStorage(protocol), request.Mode, host, port, serialPort,
            serial?.ToStorage(), "CONNECTING", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, null, null, null, recording.RelativePath, 0, null, false);
        try
        {
            _catalog.CreateTerminalSession(snapshot);
            var runtime = new TerminalSessionRuntime(this, _catalog, _credentials, _settings.DataRoot,
                snapshot, profile, serial, recording, autoProtocol);
            if (!_sessions.TryAdd(id, runtime)) throw new InvalidOperationException("Terminal session id collision.");
            runtime.Start();
            return id;
        }
        catch
        {
            recording.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_sessions.Values.Select(session => session.DisposeAsync().AsTask())).ConfigureAwait(false);
        _sessions.Clear();
    }

    internal void Remove(string id) => _sessions.TryRemove(id, out _);
}

internal sealed class TerminalSessionRuntime : IAsyncDisposable
{
    private readonly TerminalSessionManager _owner;
    private readonly Catalog _catalog;
    private readonly IH3cCredentialProvider _credentials;
    private readonly string _dataRoot;
    private readonly H3cDeviceProfileSnapshot? _profile;
    private readonly TerminalSerialConfiguration? _serial;
    private readonly TerminalRecordingWriter _recording;
    private readonly bool _allowProtocolFallback;
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = true
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _startGate = new();
    private Task? _lifecycle;
    private ITerminalTransport? _transport;
    private int _disposed;

    public TerminalSessionSnapshot Snapshot { get; private set; }
    public ChannelReader<byte[]> Output => _output.Reader;

    internal TerminalSessionRuntime(TerminalSessionManager owner, Catalog catalog, IH3cCredentialProvider credentials,
        string dataRoot, TerminalSessionSnapshot snapshot, H3cDeviceProfileSnapshot? profile,
        TerminalSerialConfiguration? serial, TerminalRecordingWriter recording, bool allowProtocolFallback = false)
    {
        _owner = owner;
        _catalog = catalog;
        _credentials = credentials;
        _dataRoot = dataRoot;
        Snapshot = snapshot;
        _profile = profile;
        _serial = serial;
        _recording = recording;
        _allowProtocolFallback = allowProtocolFallback;
    }

    public void Start()
    {
        lock (_startGate) _lifecycle ??= Task.Run(RunAsync);
    }

    public async Task SendCommandAsync(string text, CancellationToken cancellationToken)
    {
        if (!RestrictedReadonlyInputPolicy.TryNormalize(text, out var command, out var reason))
        {
            RecordEvent("COMMAND_REJECTED", new { reason });
            throw new InvalidOperationException(reason);
        }
        var transport = _transport ?? throw new InvalidOperationException("Terminal session is not connected.");
        RecordEvent("COMMAND_SUBMITTED", new { command });
        await transport.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        await _recording.AppendAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "input",
            System.Text.Encoding.UTF8.GetBytes(command + "\r\n"), cancellationToken).ConfigureAwait(false);
    }

    public async Task SendControlAsync(string control, CancellationToken cancellationToken)
    {
        var transport = _transport ?? throw new InvalidOperationException("Terminal session is not connected.");
        await transport.SendControlAsync(control, cancellationToken).ConfigureAwait(false);
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        if (columns is < 1 or > 500 || rows is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal dimensions are outside the supported range.");
        return (_transport ?? throw new InvalidOperationException("Terminal session is not connected.")).ResizeAsync(columns, rows, cancellationToken);
    }

    private async Task RunAsync()
    {
        try
        {
            _transport = await CreateTransportAsync().ConfigureAwait(false);
            try
            {
                await _transport.ConnectAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (H3cProtocolNegotiationException) when (_allowProtocolFallback &&
                _profile?.TerminalProtocol == "AUTO" && _profile.AllowTelnetFallback && Snapshot.Protocol == "SSH")
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
                _transport = new TelnetTerminalTransport(_profile, _credentials.GetPassword(_profile.CredentialReference) ?? string.Empty);
                await _transport.ConnectAsync(_stop.Token).ConfigureAwait(false);
                _catalog.UpdateTerminalProtocol(Snapshot.Id, _transport.Protocol);
                Snapshot = Snapshot with { Protocol = _transport.Protocol };
            }
            _catalog.UpdateTerminalState(Snapshot.Id, "CONNECTED", "CONNECTED",
                negotiatedAlgorithm: _transport.NegotiatedAlgorithm, hostKeyEvent: _transport.HostKeyEvent);
            _catalog.RecordTerminalEvent(Snapshot.Id, new("CONNECTED", new { protocol = _transport.Protocol }));
            _catalog.RecordTerminalEvent(Snapshot.Id, new("AUTHENTICATED", new { protocol = _transport.Protocol }));
            if (_transport.HostKeyEvent is "FIRST_TRUST" or "AUTO_UPDATE")
                _catalog.RecordTerminalEvent(Snapshot.Id, new("HOST_KEY_CHANGED", new
                {
                    decision = _transport.HostKeyEvent,
                    warning = "Automatic host-key acceptance is not independent device identity verification."
                }));
            Snapshot = Snapshot with { State = "CONNECTED", ConnectionResult = "CONNECTED", NegotiatedAlgorithm = _transport.NegotiatedAlgorithm };
            await ReadLoopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            await CloseAsync("CANCELLED", "DISCONNECTED").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var reason = ex switch
            {
                H3cHostKeyMismatchException => "HOST_KEY_MISMATCH",
                H3cUnsupportedAlgorithmException or H3cProtocolNegotiationException => "UNSUPPORTED_ALGORITHM",
                H3cAuthenticationFailedException => "AUTHENTICATION_FAILED",
                H3cCredentialUnavailableException => "CREDENTIAL_UNAVAILABLE",
                TimeoutException => "TIMEOUT",
                UnauthorizedAccessException => "SERIAL_ACCESS_DENIED",
                IOException => "TRANSPORT_IO_ERROR",
                _ => "CONNECTION_FAILED"
            };
            try
            {
                _catalog.UpdateTerminalState(Snapshot.Id, "FAILED", reason, ex.Message);
                _catalog.RecordTerminalEvent(Snapshot.Id, new("DISCONNECTED", new { reason }));
            }
            catch { }
            Snapshot = Snapshot with { State = "FAILED", ConnectionResult = reason, DisconnectReason = ex.Message };
            await CloseAsync(reason, "FAILED").ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[8192];
        while (!_stop.IsCancellationRequested)
        {
            var count = await _transport!.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
            if (count == 0) break;
            var payload = buffer.AsSpan(0, count).ToArray();
            try
            {
                await _recording.AppendAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "output", payload, _stop.Token).ConfigureAwait(false);
            }
            catch
            {
                _recording.MarkIncomplete();
                RecordEvent("STORAGE_ERROR", new { reason = "RECORDING_WRITE_FAILED" });
            }
            RecordEvent("OUTPUT_RECEIVED", new { bytes = count });
            if (!_output.Writer.TryWrite(payload))
            {
                RecordEvent("BACKPRESSURE", new { reason = "OUTPUT_BUFFER_FULL" });
                await CloseAsync("OUTPUT_BACKPRESSURE", "DISCONNECTED").ConfigureAwait(false);
                return;
            }
        }
        await CloseAsync("REMOTE_DISCONNECT", "DISCONNECTED").ConfigureAwait(false);
    }

    private async Task<ITerminalTransport> CreateTransportAsync()
    {
        if (_serial is not null)
            return _serial.PortName.StartsWith("SIMULATED-", StringComparison.OrdinalIgnoreCase)
                ? new SimulatedSerialTerminalTransport(_serial.PortName)
                : new SerialTerminalTransport(_serial);
        var profile = _profile ?? throw new InvalidOperationException("Terminal device profile is missing.");
        var password = _credentials.GetPassword(profile.CredentialReference);
        if (string.IsNullOrEmpty(password)) throw new H3cCredentialUnavailableException();
        return Snapshot.Protocol switch
        {
                "SSH" => new SshTerminalTransport(profile, password, _catalog),
            "TELNET" => new TelnetTerminalTransport(profile, password),
            _ => throw new InvalidOperationException("Unsupported terminal protocol.")
        };
    }

    private void RecordEvent(string eventType, object? detail = null)
    {
        try { _catalog.RecordTerminalEvent(Snapshot.Id, new(eventType, detail)); }
        catch { _recording.MarkIncomplete(); }
    }

    private async Task CloseAsync(string reason, string state)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false); }
        catch { }
        try
        {
            var recording = await _recording.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            _catalog.CompleteTerminalRecording(Snapshot.Id, recording);
            _catalog.UpdateTerminalState(Snapshot.Id, state, disconnectReason: reason);
            _catalog.RecordTerminalEvent(Snapshot.Id, new(state == "CLOSED" ? "SESSION_CLOSED" : "DISCONNECTED", new { reason }));
            Snapshot = Snapshot with { State = state, DisconnectReason = reason, RecordingBytes = recording.Bytes,
                RecordingSha256 = recording.Sha256, RecordingComplete = recording.Complete, EndedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        }
        catch
        {
            _recording.MarkIncomplete();
            try { _catalog.RecordTerminalEvent(Snapshot.Id, new("STORAGE_ERROR", new { reason = "SESSION_CLOSE_PERSIST_FAILED" })); } catch { }
        }
        _output.Writer.TryComplete();
        _owner.Remove(Snapshot.Id);
    }

    internal Task CloseForUserAsync() => CloseAsync("USER_REQUEST", "CLOSED");

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false); }
        catch { }
        if (_lifecycle is not null)
        {
            try { await _lifecycle.ConfigureAwait(false); } catch { }
        }
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            await CloseAsync("SERVICE_STOP", "CLOSED").ConfigureAwait(false);
        _stop.Dispose();
    }
}

internal sealed class TerminalTicketStore
{
    private readonly ConcurrentDictionary<string, (string SessionId, long ExpiresAtMs)> _tickets = new(StringComparer.Ordinal);

    public TerminalTicketResult Issue(string sessionId)
    {
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds();
        _tickets[ticket] = (sessionId, expires);
        return new(ticket, sessionId, expires);
    }

    public bool TryConsume(string ticket, string sessionId)
    {
        if (!_tickets.TryRemove(ticket, out var value)) return false;
        return value.SessionId == sessionId && value.ExpiresAtMs >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
