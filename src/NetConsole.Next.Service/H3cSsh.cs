using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Credentials;

namespace NetConsole.Next.Service;

internal interface IH3cCredentialProvider
{
    string? GetPassword(string reference);

    string? GetPassword(H3cDeviceProfileSnapshot profile) => GetPassword(profile.CredentialReference);
    string? GetPassword(string profileId, string reference) => GetPassword(reference);
}

internal sealed class H3cCredentialUnavailableException() : InvalidOperationException(
    "Configured H3C credential reference has no available secret.") { }

internal sealed class H3cHostKeyMismatchException() : InvalidOperationException(
    "SSH host key did not match the configured SHA256 pin.") { }

internal sealed class H3cHostKeyMissingException() : InvalidOperationException(
    "SSH connection completed without a verifiable host key.") { }

internal sealed class H3cJumpHostKeyMismatchException() : InvalidOperationException(
    "SSH Jump Host key did not match the configured SHA256 pin.") { }

internal sealed class H3cJumpHostKeyMissingException() : InvalidOperationException(
    "SSH Jump Host connection completed without a verifiable host key.") { }

internal sealed class H3cJumpHostConnectionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class H3cAuthenticationFailedException() : InvalidOperationException(
    "SSH authentication failed for the configured H3C profile.") { }

internal sealed class H3cConnectionTimeoutException() : TimeoutException(
    "H3C SSH connection timed out.") { }

internal sealed class H3cCommandTimeoutException() : TimeoutException(
    "设备命令读取超时，结果不完整。") { }

internal sealed class H3cConnectionLostException() : Exception(
    "设备连接已断开或不可达。") { }

internal sealed class H3cCommandInterruptedException() : Exception(
    "设备命令执行中断，结果不完整。") { }

internal sealed class H3cWorkerProtocolException() : InvalidOperationException(
    "Netmiko Worker 返回了无效响应。") { }

internal sealed class H3cUnsupportedAlgorithmException() : InvalidOperationException(
    "The SSH peer has no algorithm in the configured security allowlist.") { }

internal sealed class H3cProtocolNegotiationException() : InvalidOperationException(
    "The SSH peer could not negotiate a compatible protocol algorithm.") { }

internal static class H3cConnectionPolicy
{
    // One configured alternate transport, before any business command runs.
    // Authentication, host-key, Jump Host and cancellation failures never retry.
    public static bool CanFallbackToTelnet(H3cDeviceProfileSnapshot profile, Exception error) =>
        string.Equals(profile.TerminalProtocol, "AUTO", StringComparison.OrdinalIgnoreCase) &&
        profile.AllowTelnetFallback &&
        error is H3cProtocolNegotiationException or H3cConnectionTimeoutException;
}

internal sealed class H3cOutputLimitException() : Exception(
    "H3C command output exceeded the configured combined output/error limit.") { }

internal sealed class H3cInvalidEncodingException() : Exception(
    "H3C command output is not valid for the configured terminal encoding.") { }

internal sealed class H3cCommandRejectedException() : Exception(
    "H3C read-only command failed or returned an SSH error stream.") { }

internal sealed class H3cPaginationDetectedException() : Exception(
    "H3C response contains a paging prompt and is incomplete.") { }

internal class H3cPartialOutputException(
    string output, string failureCode, bool isCancellation) : IOException(failureCode)
{
    public string Output { get; } = output;
    public string FailureCode { get; } = failureCode;
    public bool IsCancellation { get; } = isCancellation;
}

internal sealed class H3cCommandBoundaryException(string output) : H3cPartialOutputException(
    output, "H3C_COMMAND_ECHO_CONTAMINATION", false) { }

// The bootstrap provider is only selected for an explicit Development configuration.
internal sealed class EnvironmentH3cCredentialProvider : IH3cCredentialProvider
{
    private readonly bool _enabled;

    public EnvironmentH3cCredentialProvider(bool enabled) => _enabled = enabled;

    public string? GetPassword(string reference) =>
        _enabled && H3cCredentialReference.IsValid(reference)
            ? Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_CREDENTIAL_" + reference)
            : null;
}

internal sealed class WindowsDpapiH3cCredentialProvider(string dataRoot) : IH3cCredentialProvider
{
    public string? GetPassword(string reference) => OperatingSystem.IsWindows()
        ? WindowsDpapiCredentialVault.Get(dataRoot, reference)
        : null;
}

internal sealed class UnavailableH3cCredentialProvider : IH3cCredentialProvider
{
    public string? GetPassword(string reference) => null;
}

// Production H3C authentication reads the password from the device-bound
// profile row. The reference parameter is retained only as an internal lookup
// key for compatibility with the existing terminal and ZTE abstractions.
internal sealed class DeviceRecordH3cCredentialProvider(Catalog catalog) : IH3cCredentialProvider
{
    public string? GetPassword(string reference) => null;
    public string? GetPassword(H3cDeviceProfileSnapshot profile) => catalog.GetH3cCredentialPassword(profile.Id);
    public string? GetPassword(string profileId, string reference) => catalog.GetH3cCredentialPassword(profileId);
}

internal interface IH3cSshTransportFactory
{
    IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password);
}

internal interface IH3cSshTransport : IDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken);
    Task<string> ExecuteAsync(string command, CancellationToken cancellationToken);
    void Abort();
}

/// <summary>
/// Production network-device transport.  The CLI implementation lives in the
/// pinned CPython/Netmiko worker; this .NET side owns process lifetime, bounded
/// request/response I/O, cancellation and the Catalog host-key decision.
/// </summary>
internal sealed class NetmikoWorkerH3cSshTransportFactory(Catalog? catalog = null) : IH3cSshTransportFactory
{
    public IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password)
    {
        var useJump = !string.Equals(profile.TerminalProtocol, "TELNET", StringComparison.OrdinalIgnoreCase);
        var jump = useJump ? catalog?.GetSiteSshJumpHost(profile.SiteId) : null;
        var vendor = catalog?.GetZteDeviceProfile(profile.Id) is not null ? "zte" :
            catalog?.GetNetmikoVendor(profile.Id) ?? (profile.DeviceType is "zte_zxros" or "zte_zxros_telnet" ? "zte" : "h3c");
        return new NetmikoRetryTransport(
            () =>
            {
                var currentJump = useJump ? catalog?.GetSiteSshJumpHost(profile.SiteId) : null;
                if (!SameJumpConfiguration(jump, currentJump))
                    throw new InvalidOperationException("SSH 中转配置在会话期间发生变化，请重新开始任务。");
                return new NetmikoWorkerH3cSshTransport(profile, password, currentJump, catalog);
            },
            command => CanReplay(profile, vendor, command));
    }

    private static bool SameJumpConfiguration(SiteSshJumpHostSnapshot? first, SiteSshJumpHostSnapshot? current) =>
        first is null ? current is null : current is not null && first.SiteId == current.SiteId &&
        first.Host == current.Host && first.Port == current.Port && first.Username == current.Username &&
        first.Password == current.Password && first.CredentialReference == current.CredentialReference && first.Enabled == current.Enabled;

    internal static bool CanReplay(H3cDeviceProfileSnapshot profile, string vendor, string command) => vendor switch
    {
        "h3c" when string.IsNullOrWhiteSpace(profile.DeviceType) || profile.DeviceType is "hp_comware" or "hp_comware_telnet" => H3cCommandPolicy.IsAllowed(command),
        "zte" when string.IsNullOrWhiteSpace(profile.DeviceType) || profile.DeviceType is "zte_zxros" or "zte_zxros_telnet" => ZteCommandPolicy.IsAllowed(command),
        _ => false
    };
}

internal sealed class NetmikoWorkerH3cSshTransport : IH3cSshTransport
{
    private readonly H3cDeviceProfileSnapshot _profile;
    private readonly string _password;
    private readonly SiteSshJumpHostSnapshot? _jumpHost;
    private readonly Catalog? _catalog;
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private bool _connected;
    private bool _disposed;
    private bool _connectStarted;
    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _stopped = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly bool _interactive;
    private readonly string _vendor;

    public NetmikoWorkerH3cSshTransport(H3cDeviceProfileSnapshot profile, string password,
        SiteSshJumpHostSnapshot? jumpHost = null, Catalog? catalog = null, bool interactive = false)
    {
        _profile = profile;
        _password = password;
        _catalog = catalog;
        _interactive = interactive;
        _vendor = catalog is not null && string.IsNullOrWhiteSpace(profile.DeviceType)
            ? catalog.GetNetmikoVendor(profile.Id) : "h3c";
        _jumpHost = jumpHost is { Enabled: true } &&
            !string.Equals(profile.TerminalProtocol, "TELNET", StringComparison.OrdinalIgnoreCase)
            ? jumpHost : null;
    }

    public bool IsConnected
    {
        get
        {
            lock (_lifetimeGate)
            {
                try { return _connected && !_disposed && _process is { HasExited: false }; }
                catch (InvalidOperationException) { return false; }
            }
        }
    }
    internal bool HasJumpHost => _jumpHost is not null;
    internal string? Driver { get; private set; }
    private bool _channelEnded;

    public async Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifyHostKey);
        if (_jumpHost is not null && string.IsNullOrEmpty(_jumpHost.Password))
            throw new H3cJumpHostConnectionException("JUMP_HOST_CREDENTIAL_UNAVAILABLE", "当前局点未保存 SSH 中转密码。");
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stopped.Token.ThrowIfCancellationRequested();
            if (_connectStarted) throw new InvalidOperationException("Netmiko transport can only connect once.");
            _connectStarted = true;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped.Token, timeout.Token);
        NetmikoWorkerBudget.Lease? slot = null;
        try
        {
            slot = await NetmikoWorkerBudget.AcquireAsync(deadline.Token).ConfigureAwait(false);
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                deadline.Token.ThrowIfCancellationRequested();
                StartWorker(ref slot);
            }
            var result = await RequestAsync(new
            {
                op = "connect",
                host = _profile.Host,
                port = _profile.Port,
                username = _profile.Username,
                password = _password,
                vendor = _vendor,
                protocol = string.Equals(_profile.TerminalProtocol, "TELNET", StringComparison.OrdinalIgnoreCase) ? "telnet" : "ssh",
                device_type = string.IsNullOrWhiteSpace(_profile.DeviceType) ? null : _profile.DeviceType,
                allow_legacy_algorithms = _profile.AllowLegacyAlgorithms,
                conn_timeout = 10,
                auth_timeout = 10,
                banner_timeout = 10,
                encoding = string.Equals(_profile.TerminalEncoding, "GB18030", StringComparison.OrdinalIgnoreCase)
                    ? "gb18030" : "utf-8",
                interactive = _interactive,
                jump_host = BuildJumpRequest()
            }, deadline.Token, connecting: true).ConfigureAwait(false);
            Driver = result.TryGetProperty("driver", out var driver) ? driver.GetString() : null;
            var keyType = result.TryGetProperty("host_key_type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString() : null;
            var fingerprint = result.TryGetProperty("host_key_sha256", out var key) && key.ValueKind == JsonValueKind.String
                ? key.GetString() : null;
            var isTelnet = string.Equals(_profile.TerminalProtocol, "TELNET", StringComparison.OrdinalIgnoreCase);
            if (_jumpHost is not null)
            {
                var jumpFingerprint = result.TryGetProperty("jump_host_key_sha256", out var jumpKey) && jumpKey.ValueKind == JsonValueKind.String
                    ? jumpKey.GetString() : null;
                if (string.IsNullOrWhiteSpace(jumpFingerprint)) throw new H3cJumpHostKeyMissingException();
            }
            if (!isTelnet)
            {
                if (string.IsNullOrWhiteSpace(fingerprint)) throw new H3cHostKeyMissingException();
                if (!verifyHostKey(keyType, fingerprint)) throw new H3cHostKeyMismatchException();
            }
            if (_jumpHost is not null && _catalog is not null &&
                result.TryGetProperty("jump_host_key_sha256", out var presentedJumpKey) && presentedJumpKey.ValueKind == JsonValueKind.String &&
                !_catalog.UpdateSiteSshJumpHostFingerprint(_jumpHost.SiteId, presentedJumpKey.GetString()!, _jumpHost))
                throw new H3cJumpHostConnectionException("JUMP_HOST_CONFIGURATION_CHANGED", "SSH 中转配置已变化，请重新连接。");
            lock (_lifetimeGate)
            {
                deadline.Token.ThrowIfCancellationRequested();
                _connected = true;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        { Abort(); throw new H3cConnectionTimeoutException(); }
        catch { Abort(); throw; }
        finally { slot?.Dispose(); }
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stopped.Token.ThrowIfCancellationRequested();
        }
        if (!IsConnected) throw new H3cConnectionLostException();
        var result = await RequestAsync(new { op = "execute", command, read_timeout = 30 }, cancellationToken).ConfigureAwait(false);
        return result.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String
            ? output.GetString() ?? string.Empty
            : throw new H3cWorkerProtocolException();
    }

    internal async Task<string> ReadChannelAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || !_interactive) throw new IOException("Netmiko interactive channel is not connected.");
        if (_channelEnded) throw new EndOfStreamException("设备终端已断开。");
        var result = await RequestAsync(new { op = "shell_read" }, cancellationToken).ConfigureAwait(false);
        _channelEnded = result.TryGetProperty("connected", out var connected) && !connected.GetBoolean();
        if (_channelEnded && string.IsNullOrEmpty(result.GetProperty("output").GetString()))
            throw new EndOfStreamException("设备终端已断开。");
        return result.GetProperty("output").GetString() ?? string.Empty;
    }

    internal async Task WriteChannelAsync(string data, CancellationToken cancellationToken)
    {
        if (!IsConnected || !_interactive) throw new IOException("Netmiko interactive channel is not connected.");
        await RequestAsync(new { op = "shell_write", data }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task ResizeChannelAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        if (!IsConnected || !_interactive) throw new IOException("Netmiko interactive channel is not connected.");
        await RequestAsync(new { op = "shell_resize", columns, rows }, cancellationToken).ConfigureAwait(false);
    }

    public void Abort()
    {
        lock (_lifetimeGate)
        {
            _connected = false;
            _stopped.Cancel();
            try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            // Never write an unqueued close request alongside an in-flight response.
            Abort();
            try { _input?.Dispose(); } catch { }
            try { _output?.Dispose(); } catch { }
            // The exit observer disposes the process and releases its slot.
        }
    }

    private void StartWorker(ref NetmikoWorkerBudget.Lease? slot)
    {
        var script = FindWorkerScript();
        var start = new ProcessStartInfo
        {
            FileName = NetmikoWorkerPaths.FindPython(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        start.ArgumentList.Add(script);
        start.StandardInputEncoding = new UTF8Encoding(false);
        start.StandardOutputEncoding = Encoding.UTF8;
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        var processSlot = slot!;
        slot = null;
        var worker = processSlot.Start(start);
        _process = worker.Process;
        _input = worker.Input;
        _output = worker.Output;
        _ = Task.Run(async () =>
        {
            try { while (await worker.Error.ReadLineAsync().ConfigureAwait(false) is not null) { } }
            catch { }
            finally { worker.Error.Dispose(); }
        });
    }

    private async Task<JsonElement> RequestAsync(object request, CancellationToken cancellationToken, bool connecting = false)
    {
        // A terminal reads continuously while the user writes. Queue both
        // operations so the JSON responses cannot be consumed by another call.
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var token = deadline.Token;
            var input = _input ?? throw new InvalidOperationException("Netmiko worker input is unavailable.");
            var output = _output ?? throw new InvalidOperationException("Netmiko worker output is unavailable.");
            await input.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token).ConfigureAwait(false);
            await input.FlushAsync(token).ConfigureAwait(false);
            var line = await output.ReadLineAsync(token).ConfigureAwait(false)
                ?? throw (connecting ? (Exception)new H3cConnectionLostException() : new H3cCommandInterruptedException());
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new H3cWorkerProtocolException();
            if (ok.GetBoolean())
                return root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                    ? result.Clone() : throw new H3cWorkerProtocolException();
            var error = root.TryGetProperty("error", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null;
            var category = root.TryGetProperty("failure_kind", out var failure) && failure.ValueKind == JsonValueKind.String ? failure.GetString() : null;
            throw MapWorkerFailure(error, category, connecting);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stopped.IsCancellationRequested)
        {
            Abort();
            if (connecting) throw new H3cConnectionTimeoutException();
            throw new H3cCommandTimeoutException();
        }
        catch (JsonException) { Abort(); throw new H3cWorkerProtocolException(); }
        catch (IOException error) when (IsWorkerPipeDisconnected(error))
        {
            Abort();
            if (connecting) throw new H3cConnectionLostException();
            throw new H3cCommandInterruptedException();
        }
        catch
        {
            // Once a request has started, cancellation/error invalidates the
            // stream. Kill it instead of allowing a stale reply into the queue.
            Abort();
            throw;
        }
        finally { _requestGate.Release(); }
    }

    private bool IsWorkerPipeDisconnected(IOException error)
    {
        // Windows broken pipe / pipe closing / no process; Unix EPIPE. Other
        // IO errors are not proof that a command is safe to replay.
        if ((error.HResult & 0xffff) is 109 or 232 or 233 or 32) return true;
        lock (_lifetimeGate)
        {
            try { return _process is { HasExited: true }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    internal static Exception MapWorkerFailure(string? error, string? category = null, bool connecting = false) => (category ?? error) switch
    {
        "CONNECTION_LOST" => new H3cConnectionLostException(),
        "COMMAND_INTERRUPTED" or "CommandInterruptedError" => new H3cCommandInterruptedException(),
        "CONNECT_TIMEOUT" => connecting ? new H3cConnectionTimeoutException() : new H3cCommandTimeoutException(),
        "COMMAND_TIMEOUT" => new H3cCommandTimeoutException(),
        "PROTOCOL_ERROR" => new H3cProtocolNegotiationException(),
        "JUMP_HOST_PROTOCOL_ERROR" => new H3cJumpHostConnectionException("JUMP_HOST_PROTOCOL_FAILED", "SSH 中转服务器协议协商失败。"),
        "JUMP_HOST_FORWARD_DENIED" => new H3cJumpHostConnectionException("JUMP_HOST_FORWARD_DENIED", "SSH 中转服务器拒绝转发，请检查转发权限。"),
        "JumpHostAuthenticationError" => new H3cJumpHostConnectionException("JUMP_HOST_AUTHENTICATION_FAILED", "SSH 中转服务器认证失败，请检查中转用户名和密码。"),
        "JumpHostConnectionError" => new H3cJumpHostConnectionException("JUMP_HOST_UNREACHABLE", "SSH 中转服务器不可达，请检查地址、端口和 SSH 服务。"),
        "JumpHostChannelError" => new H3cJumpHostConnectionException("JUMP_HOST_CHANNEL_FAILED", "SSH 中转已连接，但无法转发到目标设备，请检查路由和转发权限。"),
        "AuthenticationException" or "NetmikoAuthenticationException" => new H3cAuthenticationFailedException(),
        "ReadTimeout" => new H3cCommandTimeoutException(),
        "TimeoutError" or "NetmikoTimeoutException" or "socket.timeout" => connecting ? new H3cConnectionTimeoutException() : new H3cCommandTimeoutException(),
        "UnsupportedDeviceTypeError" or "ValueError" => new ArgumentException("设备驱动或连接参数不受支持。"),
        "OutputLimitError" => new H3cOutputLimitException(),
        "SSHException" or "IncompatiblePeer" => new H3cProtocolNegotiationException(),
        "EOFError" or "ConnectionError" or "ConnectionResetError" or "ConnectionAbortedError" or "BrokenPipeError" or "ConnectionRefusedError" =>
            connecting ? new H3cConnectionLostException() : new H3cCommandInterruptedException(),
        _ => new H3cWorkerProtocolException()
    };

    private static string FindWorkerScript()
    {
        return NetmikoWorkerPaths.FindScript();
    }

    private object? BuildJumpRequest() => _jumpHost is null ? null : new
    {
        host = _jumpHost.Host,
        port = _jumpHost.Port,
        username = _jumpHost.Username,
        password = _jumpHost.Password
    };
}

internal sealed class H3cSshDeviceSessionFactory(Catalog catalog, IH3cCredentialProvider credentials,
    IH3cSshTransportFactory? transportFactory = null) : IDeviceSessionFactory
{
    private readonly IH3cSshTransportFactory _transportFactory = transportFactory ?? new NetmikoWorkerH3cSshTransportFactory(catalog);

    public IDeviceSession Create(string target)
    {
        var profile = catalog.GetH3cDeviceProfile(target)
            ?? throw new KeyNotFoundException("H3C device profile was not found.");
        if (!profile.Enabled) throw new InvalidOperationException("H3C device profile is disabled.");
        return new H3cSshDeviceSession(profile, credentials, _transportFactory, catalog);
    }
}

internal sealed class H3cSshDeviceSession(H3cDeviceProfileSnapshot profile, IH3cCredentialProvider credentials,
    IH3cSshTransportFactory transportFactory, Catalog? catalog = null) : IDeviceSession
{
    private IH3cSshTransport? _transport;
    private bool _sessionCounted;
    private readonly NetmikoReconnectBudget _recoveryBudget = new();
    private Action? _operationBoundary;
    public void SetOperationBoundary(Action boundary) => _operationBoundary = boundary;
    public string Protocol { get; private set; } =
        string.Equals(profile.TerminalProtocol, "TELNET", StringComparison.OrdinalIgnoreCase) ? "TELNET" : "SSH";

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var password = credentials.GetPassword(profile);
        if (string.IsNullOrEmpty(password)) throw new H3cCredentialUnavailableException();
        try
        {
            await ConnectAttemptAsync(profile with { TerminalProtocol = Protocol }, password, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            H3cConnectionPolicy.CanFallbackToTelnet(profile, ex))
        {
            Protocol = "TELNET";
            await ConnectAttemptAsync(profile with { TerminalProtocol = "TELNET" }, password, cancellationToken)
                .ConfigureAwait(false);
        }
        _sessionCounted = true;
        H3cCollectionMetrics.SessionOpened();
    }

    private async Task ConnectAttemptAsync(H3cDeviceProfileSnapshot attempt, string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operationBoundary?.Invoke();
        _transport = transportFactory.Create(attempt, password);
        string? hostKeyCorrelation = null;
        void CommitKey()
        {
            if (catalog is not null && hostKeyCorrelation is not null && !catalog.CommitSshHostKey(hostKeyCorrelation))
                throw new H3cHostKeyMismatchException();
            hostKeyCorrelation = null;
        }
        void FailKey()
        {
            if (catalog is not null && hostKeyCorrelation is not null)
                catalog.FailSshHostKey(hostKeyCorrelation, "SSH_CONNECTION_FAILED_BEFORE_EFFECTIVE");
            hostKeyCorrelation = null;
        }
        if (_transport is NetmikoRetryTransport recovery)
        {
            recovery.ReconnectBudget = _recoveryBudget;
            recovery.BeforeReconnect = () => { _operationBoundary?.Invoke(); hostKeyCorrelation = null; };
            recovery.Reconnected = CommitKey;
            recovery.ReconnectFailed = FailKey;
        }
        try
        {
            await _transport.ConnectAsync((keyType, fingerprint) =>
            {
                if (catalog is null) return H3cHostKeyFingerprint.Matches(profile.HostKeySha256, fingerprint);
                var decision = catalog.CheckSshHostKey(profile, keyType, fingerprint);
                hostKeyCorrelation = decision.CorrelationId;
                return decision.Accepted;
            }, cancellationToken).ConfigureAwait(false);
            CommitKey();
        }
        catch
        {
            try { FailKey(); } catch { }
            try { _transport.Dispose(); } catch { }
            _transport = null;
            throw;
        }
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        if (!H3cCommandPolicy.IsAllowed(command))
            throw new ArgumentException("Only allowlisted H3C read-only commands are allowed.", nameof(command));
        var transport = _transport;
        if (transport is null || !transport.IsConnected)
            throw new InvalidOperationException("H3C SSH session is not connected.");
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        try { return await transport.ExecuteAsync(command, cancellationToken).ConfigureAwait(false); }
        finally { H3cCollectionMetrics.RecordCommand(Stopwatch.GetElapsedTime(started)); }
    }

    public ValueTask DisposeAsync()
    {
        try { _transport?.Dispose(); }
        finally
        {
            _transport = null;
            if (_sessionCounted)
            {
                _sessionCounted = false;
                H3cCollectionMetrics.SessionClosed();
            }
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed record H3cRawOutputManifest(string RelativePath, string Command, long ByteCount,
    string OutputSha256, string SegmentSha256, string CaptureStatus, string ParseStatus,
    string ParsedJson, long CapturedMs, int CommandIndex = 0);

internal interface ITaskResultSink
{
    Task<H3cRawOutputManifest> PersistAsync(string runId, TaskDefinitionSnapshot definition,
        string output, string captureStatus);

    Task<H3cRawOutputManifest> PersistCommandAsync(string runId, TaskDefinitionSnapshot definition,
        string command, string output, string captureStatus, int commandIndex)
        => PersistAsync(runId, definition, output, captureStatus);
}

internal sealed class H3cTaskRawResultSink(Catalog catalog, Action<Exception>? storageFailure = null) : ITaskResultSink
{
    public async Task<H3cRawOutputManifest> PersistAsync(string runId, TaskDefinitionSnapshot definition,
        string output, string captureStatus)
        => await PersistCommandAsync(runId, definition, definition.Command, output, captureStatus, 0).ConfigureAwait(false);

    public async Task<H3cRawOutputManifest> PersistCommandAsync(string runId, TaskDefinitionSnapshot definition,
        string command, string output, string captureStatus, int commandIndex)
    {
        if (definition.TaskType != "H3C_SSH_READONLY")
            throw new InvalidOperationException("Only H3C output can be written through the H3C Raw sink.");
        if (captureStatus is not ("COMPLETE" or "PARTIAL")) throw new ArgumentException("Capture status is invalid.", nameof(captureStatus));
        var profile = catalog.GetH3cDeviceProfile(definition.Target)
            ?? throw new InvalidOperationException("H3C result references a missing device profile.");
        var capturedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        output = ConfigBackupSanitizer.Sanitize(command, output);
        var outputByteCount = Encoding.UTF8.GetByteCount(output);
        if (outputByteCount > H3cRawOutput.MaximumBytes)
            throw new InvalidDataException("H3C response exceeds the Raw output byte limit.");

        var writer = new RawSegmentWriterFactory().CreateH3c(catalog, profile.Id);
        try
        {
            var outputSha = writer.AppendH3cCommand(profile.Id, runId, definition.ScopeSnapshot?.Endpoint?.Host ?? profile.Host,
                command, output, capturedMs, captureStatus);
            await writer.FlushAsync().ConfigureAwait(false);
            await writer.SealAsync().ConfigureAwait(false);
            H3cCollectionMetrics.RecordRawCommit(writer.LastSyncDurationMs, writer.LastSealDurationMs);
            var parse = captureStatus == "PARTIAL"
                ? new H3cParseResult("PARTIAL", "{}")
                : H3cComwareParser.Parse(command, output, completedExec: true);
            return new H3cRawOutputManifest(writer.RelativePath, command, outputByteCount,
                Convert.ToHexString(outputSha).ToLowerInvariant(), writer.Sha256!, captureStatus,
                parse.Status, parse.Json, capturedMs, commandIndex);
        }
        catch (Exception ex)
        {
            try { await writer.PreserveOpenAsync().ConfigureAwait(false); }
            catch (Exception preserveError) { storageFailure?.Invoke(preserveError); }
            if (ex is not InvalidDataException && (ex is IOException or UnauthorizedAccessException or SqliteException))
            {
                try { storageFailure?.Invoke(ex); }
                catch { }
            }
            throw;
        }
    }
}
