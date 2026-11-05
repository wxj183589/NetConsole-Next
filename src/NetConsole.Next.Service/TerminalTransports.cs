using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NetConsole.Next.Service;

internal interface ITerminalTransport : IAsyncDisposable
{
    string Protocol { get; }
    string? NegotiatedAlgorithm { get; }
    string? HostKeyEvent { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    Task SendCommandAsync(string command, CancellationToken cancellationToken);
    Task SendControlAsync(string control, CancellationToken cancellationToken);
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken);
}

internal sealed class SshTerminalTransport : ITerminalTransport
{
    private readonly H3cDeviceProfileSnapshot _profile;
    private readonly SshClient _client;
    private readonly Catalog _catalog;
    private ShellStream? _shell;
    private bool _hostKeySeen;
    private bool _hostKeyAccepted;
    private string? _hostKeyCorrelation;
    private string? _hostKeyDecision;
    private readonly byte[] _pending = new byte[64 * 1024];
    private readonly TerminalTextDecoder _decoder;
    private int _pendingOffset;
    private int _pendingCount;

    public SshTerminalTransport(H3cDeviceProfileSnapshot profile, string password, Catalog catalog)
    {
        _profile = profile;
        _client = CreateClient(profile, password);
        _catalog = catalog;
        _decoder = new TerminalTextDecoder(profile.TerminalEncoding);
    }

    public string Protocol => "SSH";
    public string? NegotiatedAlgorithm => string.Join("/", new[]
    {
        _client.ConnectionInfo.CurrentKeyExchangeAlgorithm,
        _client.ConnectionInfo.CurrentHostKeyAlgorithm,
        _client.ConnectionInfo.CurrentServerHmacAlgorithm
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string? HostKeyEvent => _hostKeyDecision;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _client.HostKeyReceived += (_, args) =>
        {
            _hostKeySeen = true;
            var check = _catalog.CheckSshHostKey(_profile, args.HostKeyName, args.FingerPrintSHA256);
            _hostKeyAccepted = check.Accepted;
            _hostKeyCorrelation = check.CorrelationId;
            _hostKeyDecision = check.Decision;
            args.CanTrust = _hostKeyAccepted;
        };
        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!_hostKeySeen || !_hostKeyAccepted) throw new H3cHostKeyMismatchException();
            _shell = _client.CreateShellStream("vt100", 120, 40, 1200, 8000, 64 * 1024);
            if (_hostKeyCorrelation is not null && !_catalog.CommitSshHostKey(_hostKeyCorrelation))
                throw new H3cHostKeyMismatchException();
        }
        catch (SshAuthenticationException)
        {
            FailPendingHostKey();
            throw new H3cAuthenticationFailedException();
        }
        catch (SshOperationTimeoutException)
        {
            FailPendingHostKey();
            throw new H3cConnectionTimeoutException();
        }
        catch (SshConnectionException) when (_hostKeySeen && !_hostKeyAccepted)
        {
            // A rejected host key is an identity failure, never a reason to
            // fall back to Telnet.
            FailPendingHostKey();
            throw new H3cHostKeyMismatchException();
        }
        catch (SshConnectionException ex) when (IsAlgorithmNegotiationFailure(ex.Message))
        {
            FailPendingHostKey();
            throw new H3cProtocolNegotiationException();
        }
        catch
        {
            if (_hostKeyCorrelation is not null)
            {
                try { _catalog.FailSshHostKey(_hostKeyCorrelation, "SSH_CONNECTION_FAILED_BEFORE_EFFECTIVE"); } catch { }
            }
            throw;
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_pendingCount > 0)
        {
            var copy = Math.Min(buffer.Length, _pendingCount);
            _pending.AsMemory(_pendingOffset, copy).CopyTo(buffer);
            _pendingOffset += copy;
            _pendingCount -= copy;
            return copy;
        }
        var shell = _shell ?? throw new InvalidOperationException("SSH terminal is not connected.");
        var raw = new byte[Math.Min(buffer.Length, 64 * 1024)];
        var read = await shell.ReadAsync(raw, cancellationToken).ConfigureAwait(false);
        if (read == 0) return 0;
        var normalized = _decoder.Decode(raw.AsSpan(0, read));
        if (normalized.Length > _pending.Length)
            throw new InvalidDataException("SSH decoded output exceeded the bounded buffer.");
        normalized.CopyTo(_pending, 0);
        _pendingOffset = 0;
        _pendingCount = normalized.Length;
        return await ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public Task SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        var shell = _shell ?? throw new InvalidOperationException("SSH terminal is not connected.");
        cancellationToken.ThrowIfCancellationRequested();
        shell.Write(command + "\r");
        shell.Flush();
        return Task.CompletedTask;
    }

    public Task SendControlAsync(string control, CancellationToken cancellationToken)
    {
        var shell = _shell ?? throw new InvalidOperationException("SSH terminal is not connected.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(control, "CTRL_C", StringComparison.Ordinal))
            throw new InvalidOperationException("Only CTRL_C is available in RESTRICTED_READONLY mode.");
        shell.Write("\x03");
        shell.Flush();
        return Task.CompletedTask;
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var shell = _shell ?? throw new InvalidOperationException("SSH terminal is not connected.");
        shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _shell?.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static SshClient CreateClient(H3cDeviceProfileSnapshot profile, string password)
    {
        var info = new Renci.SshNet.ConnectionInfo(profile.Host, profile.Port, profile.Username,
            new PasswordAuthenticationMethod(profile.Username, password))
        {
            Timeout = TimeSpan.FromSeconds(10),
            Encoding = TerminalTextEncoding.Resolve(profile.TerminalEncoding)
        };
        H3cSshAlgorithmPolicy.Apply(info, profile.AllowLegacyAlgorithms);
        return new SshClient(info);
    }

    private void FailPendingHostKey()
    {
        if (_hostKeyCorrelation is null) return;
        try { _catalog.FailSshHostKey(_hostKeyCorrelation, "SSH_CONNECTION_FAILED_BEFORE_EFFECTIVE"); } catch { }
    }

    private static bool IsAlgorithmNegotiationFailure(string? message)
    {
        var value = message ?? string.Empty;
        return value.Contains("algorithm", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("key exchange", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("cipher", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("mac", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("no matching", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Thin terminal adapter over the collection worker and HostKey authority.</summary>
internal sealed class NetmikoTerminalTransport : ITerminalTransport
{
    private readonly H3cDeviceProfileSnapshot _profile;
    private readonly Catalog _catalog;
    private readonly NetmikoWorkerH3cSshTransport _worker;
    private byte[] _pending = [];
    private int _pendingOffset;

    public NetmikoTerminalTransport(H3cDeviceProfileSnapshot profile, string password, Catalog catalog)
    {
        _profile = profile;
        _catalog = catalog;
        _worker = new(profile, password, catalog.GetSiteSshJumpHost(profile.SiteId), catalog, interactive: true);
    }

    public string Protocol => _profile.TerminalProtocol == "TELNET" ? "TELNET" : "SSH";
    public string? NegotiatedAlgorithm => "NETMIKO/" + _worker.Driver;
    public string? HostKeyEvent { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        string? correlation = null;
        try
        {
            await _worker.ConnectAsync((type, fingerprint) =>
            {
                var decision = _catalog.CheckSshHostKey(_profile, type, fingerprint);
                correlation = decision.CorrelationId;
                HostKeyEvent = decision.Decision;
                return decision.Accepted;
            }, cancellationToken).ConfigureAwait(false);
            if (correlation is not null && !_catalog.CommitSshHostKey(correlation))
                throw new H3cHostKeyMismatchException();
        }
        catch
        {
            _worker.Abort();
            if (correlation is not null)
                _catalog.FailSshHostKey(correlation, "SSH_CONNECTION_FAILED_BEFORE_EFFECTIVE");
            throw;
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty) return 0;
        while (_pendingOffset >= _pending.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _pending = Encoding.UTF8.GetBytes(await _worker.ReadChannelAsync(cancellationToken).ConfigureAwait(false));
                _pendingOffset = 0;
            }
            catch (EndOfStreamException) { return 0; }
            if (_pending.Length == 0)
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        var count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
        _pendingOffset += count;
        return count;
    }

    public Task SendCommandAsync(string command, CancellationToken cancellationToken) =>
        _worker.WriteChannelAsync(command + "\n", cancellationToken);

    public Task SendControlAsync(string control, CancellationToken cancellationToken) => control == "CTRL_C"
        ? _worker.WriteChannelAsync("\x03", cancellationToken)
        : throw new InvalidOperationException("Only CTRL_C is available in RESTRICTED_READONLY mode.");

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken) =>
        _worker.ResizeChannelAsync(columns, rows, cancellationToken);

    public ValueTask DisposeAsync() { _worker.Dispose(); return ValueTask.CompletedTask; }
}
internal sealed class SerialTerminalTransport(TerminalSerialConfiguration configuration) : ITerminalTransport
{
    private readonly SerialPort _port = CreatePort(configuration);
    public string Protocol => "SERIAL";
    public string? NegotiatedAlgorithm => "SERIAL";
    public string? HostKeyEvent => null;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _port.Open();
        return Task.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        return await _port.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        var newline = "\r\n";
        await _port.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(command + newline), cancellationToken).ConfigureAwait(false);
        await _port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SendControlAsync(string control, CancellationToken cancellationToken)
    {
        if (control != "CTRL_C") throw new InvalidOperationException("Only CTRL_C is available in RESTRICTED_READONLY mode.");
        await _port.BaseStream.WriteAsync(new byte[] { 3 }, cancellationToken).ConfigureAwait(false);
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() { _port.Dispose(); return ValueTask.CompletedTask; }

    private static SerialPort CreatePort(TerminalSerialConfiguration configuration)
    {
        if (!SerialPort.GetPortNames().Contains(configuration.PortName, StringComparer.OrdinalIgnoreCase))
            throw new IOException("The selected serial port is not currently present.");
        if (configuration.BaudRate is < 110 or > 4_000_000 || configuration.DataBits is < 5 or > 8)
            throw new ArgumentException("Serial parameters are outside the supported range.");
        if (!Enum.TryParse<StopBits>(configuration.StopBits, true, out var stopBits) || stopBits == StopBits.None ||
            !Enum.TryParse<Parity>(configuration.Parity, true, out var parity) ||
            !Enum.TryParse<Handshake>(configuration.Handshake, true, out var handshake))
            throw new ArgumentException("Serial parameters are invalid.");
        return new SerialPort(configuration.PortName, configuration.BaudRate, parity, configuration.DataBits, stopBits)
        {
            Handshake = handshake,
            DtrEnable = configuration.DtrEnable,
            RtsEnable = configuration.RtsEnable,
            Encoding = new UTF8Encoding(false, true),
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };
    }
}

// Development-only transport used by the browser E2E harness. It is never
// enumerated or accepted unless RuntimeSettings explicitly enables simulation.
internal sealed class SimulatedSerialTerminalTransport(string portName) : ITerminalTransport
{
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(16);
    private byte[]? _pending;
    private int _pendingOffset;

    public string Protocol => "SERIAL";
    public string? NegotiatedAlgorithm => "SERIAL-SIMULATED";
    public string? HostKeyEvent => null;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _output.Writer.WriteAsync(Encoding.UTF8.GetBytes($"{portName} simulated console ready\r\n> "), cancellationToken);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_pending is null || _pendingOffset >= _pending.Length)
        {
            _pending = await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _pendingOffset = 0;
        }
        var count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
        _pendingOffset += count;
        return count;
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        await _output.Writer.WriteAsync(Encoding.UTF8.GetBytes($"{command}\r\nsimulated:{command}\r\n> "), cancellationToken);
    }

    public async Task SendControlAsync(string control, CancellationToken cancellationToken)
    {
        if (control != "CTRL_C") throw new InvalidOperationException("Only CTRL_C is available in RESTRICTED_READONLY mode.");
        await _output.Writer.WriteAsync(Encoding.UTF8.GetBytes("^C\r\n> "), cancellationToken);
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
