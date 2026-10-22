using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;

namespace NetConsole.Next.Service;

internal interface ITerminalTransport : IAsyncDisposable
{
    string Protocol { get; }
    string? NegotiatedAlgorithm { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    Task SendCommandAsync(string command, CancellationToken cancellationToken);
    Task SendControlAsync(string control, CancellationToken cancellationToken);
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken);
}

internal sealed class SshTerminalTransport(H3cDeviceProfileSnapshot profile, string password) : ITerminalTransport
{
    private readonly SshClient _client = CreateClient(profile, password);
    private ShellStream? _shell;
    private bool _hostKeySeen;
    private bool _hostKeyAccepted;

    public string Protocol => "SSH";
    public string? NegotiatedAlgorithm => _client.ConnectionInfo.CurrentKeyExchangeAlgorithm;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _client.HostKeyReceived += (_, args) =>
        {
            _hostKeySeen = true;
            _hostKeyAccepted = H3cHostKeyFingerprint.Matches(profile.HostKeySha256, args.FingerPrintSHA256);
            args.CanTrust = _hostKeyAccepted;
        };
        await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!_hostKeySeen || !_hostKeyAccepted) throw new H3cHostKeyMismatchException();
        _shell = _client.CreateShellStream("vt100", 120, 40, 1200, 8000, 64 * 1024);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var shell = _shell ?? throw new InvalidOperationException("SSH terminal is not connected.");
        return await shell.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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
        // SSH.NET exposes the initial PTY size through CreateShellStream but does not
        // expose a public window-change request in the pinned package version.
        // Keep the gateway contract ready and leave the initial PTY dimensions stable.
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
            Encoding = new UTF8Encoding(false, true)
        };
        H3cSshAlgorithmPolicy.Apply(info);
        return new SshClient(info);
    }
}

internal sealed class TelnetTerminalTransport(H3cDeviceProfileSnapshot profile, string password) : ITerminalTransport
{
    private readonly H3cDeviceProfileSnapshot _profile = profile;
    private readonly string _password = password;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly TelnetNegotiator _negotiator = new();
    private readonly byte[] _pending = new byte[64 * 1024];
    private int _pendingOffset;
    private int _pendingCount;

    public string Protocol => "TELNET";
    public string? NegotiatedAlgorithm => "TELNET-IAC";

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_profile.Host, _profile.Port, cancellationToken).ConfigureAwait(false);
        _stream = _client.GetStream();
        await _stream.WriteAsync(TelnetNegotiator.InitialNegotiation, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await PumpLoginAsync(cancellationToken).ConfigureAwait(false);
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
        var stream = _stream ?? throw new InvalidOperationException("Telnet terminal is not connected.");
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0) return 0;
        var payload = _negotiator.Process(buffer.Span[..read]);
        if (_negotiator.Replies.Length > 0)
        {
            await stream.WriteAsync(_negotiator.Replies, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        if (payload.Length == 0) return await ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        payload.CopyTo(_pending, 0);
        _pendingOffset = 0;
        _pendingCount = payload.Length;
        return await ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Telnet terminal is not connected.");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SendControlAsync(string control, CancellationToken cancellationToken)
    {
        if (control != "CTRL_C") throw new InvalidOperationException("Only CTRL_C is available in RESTRICTED_READONLY mode.");
        var stream = _stream ?? throw new InvalidOperationException("Telnet terminal is not connected.");
        await stream.WriteAsync(new byte[] { 3 }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task PumpLoginAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (DateTime.UtcNow < deadline)
        {
            var stream = _stream!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0) throw new IOException("Telnet peer disconnected during login.");
            var payload = _negotiator.Process(buffer.AsSpan(0, read));
            if (_negotiator.Replies.Length > 0) await stream.WriteAsync(_negotiator.Replies, cancellationToken).ConfigureAwait(false);
            if (payload.Length == 0) continue;
            var chunk = Encoding.UTF8.GetString(payload);
            text.Append(chunk);
            var lower = text.ToString().ToLowerInvariant();
            if (lower.Contains("password"))
            {
                await SendSecretAsync(_password, cancellationToken).ConfigureAwait(false);
                text.Clear();
            }
            else if (lower.Contains("login:") || lower.Contains("username:"))
            {
                await SendSecretAsync(_profile.Username, cancellationToken).ConfigureAwait(false);
                text.Clear();
            }
            else if (lower.EndsWith("> ", StringComparison.Ordinal) || lower.EndsWith("# ", StringComparison.Ordinal) ||
                     lower.EndsWith(">", StringComparison.Ordinal) || lower.EndsWith("#", StringComparison.Ordinal))
            {
                _pendingOffset = 0;
                _pendingCount = payload.Length;
                payload.CopyTo(_pending, 0);
                return;
            }
            if (text.Length > 8192) text.Remove(0, text.Length - 4096);
        }
        throw new TimeoutException("Telnet login prompt was not completed within the deadline.");
    }

    private async Task SendSecretAsync(string value, CancellationToken cancellationToken)
    {
        var stream = _stream!;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value + "\r\n"), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class SerialTerminalTransport(TerminalSerialConfiguration configuration) : ITerminalTransport
{
    private readonly SerialPort _port = CreatePort(configuration);
    public string Protocol => "SERIAL";
    public string? NegotiatedAlgorithm => "SERIAL";

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

internal sealed class TelnetNegotiator
{
    private const byte Iac = 255;
    private const byte Will = 251;
    private const byte Wont = 252;
    private const byte Do = 253;
    private const byte Dont = 254;
    private const byte Sb = 250;
    private const byte Se = 240;
    private readonly List<byte> _payload = new();
    private readonly List<byte> _replies = new();
    private bool _inIac;
    private bool _inSubnegotiation;
    private bool _afterSubnegotiationIac;
    private byte _command;

    public static byte[] InitialNegotiation => new byte[] { Iac, Will, 1, Iac, Will, 3, Iac, Do, 24, Iac, Do, 31 };
    public ReadOnlyMemory<byte> Replies => _replies.ToArray();

    public byte[] Process(ReadOnlySpan<byte> bytes)
    {
        _payload.Clear();
        _replies.Clear();
        foreach (var value in bytes) ProcessByte(value);
        return _payload.ToArray();
    }

    private void ProcessByte(byte value)
    {
        if (_inSubnegotiation)
        {
            if (_afterSubnegotiationIac)
            {
                _afterSubnegotiationIac = false;
                if (value == Se) _inSubnegotiation = false;
                return;
            }
            if (value == Iac) _afterSubnegotiationIac = true;
            return;
        }
        if (_inIac)
        {
            _inIac = false;
            if (value == Iac) { _payload.Add(value); return; }
            if (value == Sb) { _inSubnegotiation = true; return; }
            if (value is Will or Wont or Do or Dont) { _command = value; _inIac = true; return; }
            if (_command is Will or Wont)
            {
                if (_command == Will && value is 1 or 3 or 24 or 31) Reply(Dont, value);
                else Reply(Dont, value);
            }
            else if (_command is Do or Dont) Reply(_command == Do ? Wont : Dont, value);
            _command = 0;
            return;
        }
        if (value == Iac) { _inIac = true; return; }
        _payload.Add(value);
    }

    private void Reply(byte command, byte option) { _replies.Add(Iac); _replies.Add(command); _replies.Add(option); }
}
