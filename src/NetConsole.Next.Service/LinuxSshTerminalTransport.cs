using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NetConsole.Next.Service;

/// <summary>Linux interactive SSH only. Network-device CLI stays on Netmiko.</summary>
internal sealed class LinuxSshTerminalTransport : ITerminalTransport
{
    private readonly LinuxServerSnapshot _server;
    private readonly Catalog _catalog;
    private readonly SshClient _client;
    private readonly bool _openShell;
    private ShellStream? _shell;
    private int _disposed;

    public LinuxSshTerminalTransport(LinuxServerSnapshot server, string password, Catalog catalog, bool openShell = true)
    {
        _server = server; _catalog = catalog; _openShell = openShell;
        _client = new SshClient(new Renci.SshNet.ConnectionInfo(server.View.Host, server.View.Port, server.View.Username,
            new PasswordAuthenticationMethod(server.View.Username, password))
        {
            Timeout = TimeSpan.FromSeconds(10), Encoding = new UTF8Encoding(false)
        });
        _client.KeepAliveInterval = TimeSpan.FromSeconds(30);
    }

    public string Protocol => "SSH";
    public string? NegotiatedAlgorithm => _client.ConnectionInfo.CurrentKeyExchangeAlgorithm + "/" + _client.ConnectionInfo.CurrentHostKeyAlgorithm;
    public string? HostKeyEvent { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        string? keyType = null, fingerprint = null;
        _client.HostKeyReceived += (_, args) =>
        {
            try
            {
                keyType = args.HostKeyName;
                fingerprint = H3cHostKeyFingerprint.Normalize(args.FingerPrintSHA256);
                args.CanTrust = !string.IsNullOrWhiteSpace(keyType);
            }
            catch (ArgumentException) { args.CanTrust = false; }
        };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            // Trust becomes effective only after successful SSH authentication.
            if (keyType is null || fingerprint is null || !_catalog.CommitLinuxHostKey(_server, keyType, fingerprint))
                throw new H3cHostKeyMismatchException();
            HostKeyEvent = _server.View.HostKeySha256 == fingerprint ? "MATCH" :
                _server.View.HostKeySha256.Length == 0 ? "FIRST_TRUST" : "AUTO_UPDATE";
            if (_openShell)
                _shell = await Task.Run(() => _client.CreateShellStream("xterm-256color", 120, 40, 0, 0, 64 * 1024), timeout.Token)
                    .WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (SshAuthenticationException) { throw new H3cAuthenticationFailedException(); }
        catch (SshOperationTimeoutException) { throw new H3cConnectionTimeoutException(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new H3cConnectionTimeoutException(); }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        await (_shell ?? throw new InvalidOperationException("Linux SSH session is not connected."))
            .ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    public Task SendInputAsync(string data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var shell = _shell ?? throw new InvalidOperationException("Linux SSH session is not connected.");
        shell.Write(data); shell.Flush();
        return Task.CompletedTask;
    }

    public Task SendCommandAsync(string command, CancellationToken cancellationToken) => SendInputAsync(command + "\r", cancellationToken);

    public Task SendControlAsync(string control, CancellationToken cancellationToken) => control == "CTRL_C"
        ? SendInputAsync("\x03", cancellationToken) : throw new ArgumentException("Unsupported terminal control.");

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (_shell ?? throw new InvalidOperationException("Linux SSH session is not connected.")).ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { _shell?.Dispose(); _client.Dispose(); }
        return ValueTask.CompletedTask;
    }

    internal static (string Status, string Message) Failure(Exception error) => error switch
    {
        H3cAuthenticationFailedException or SshAuthenticationException => ("AUTHENTICATION_FAILED", "SSH 用户名或密码错误。"),
        H3cCredentialUnavailableException => ("CREDENTIAL_UNAVAILABLE", "请先保存服务器登录密码。"),
        H3cHostKeyMismatchException => ("HOST_KEY_CHANGED_DURING_CONNECT", "服务器信息已更新，请重新连接。"),
        TimeoutException or OperationCanceledException or SshOperationTimeoutException => ("TIMEOUT", "SSH 连接超时，请检查地址和端口。"),
        SocketException => ("UNREACHABLE", "无法访问 SSH 服务器，请检查网络和端口。"),
        _ => ("CONNECTION_FAILED", "SSH 连接失败，请检查服务器状态和登录设置。")
    };
}

internal sealed class LinuxServerConnectionTests(Catalog catalog)
{
    private readonly SemaphoreSlim _slots = new(4, 4);

    public async Task<object> TestAsync(string id, P2B5SiteScopeDecision scope, CancellationToken cancellationToken)
    {
        if (!await _slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new { status = "BUSY", message = "连接测试并发已达上限，请稍后重试。" };
        try
        {
            var server = catalog.GetLinuxServer(id) ?? throw new KeyNotFoundException("Linux server was not found.");
            if (!DeviceSiteScope.Allows(scope, server.View.SiteId)) throw new KeyNotFoundException("Linux server was not found.");
            if (!server.View.Enabled) return new { status = "DISABLED", message = "服务器已禁用。" };
            try
            {
                var password = catalog.GetLinuxServerPassword(id) ?? throw new H3cCredentialUnavailableException();
                await using var transport = new LinuxSshTerminalTransport(server, password, catalog, openShell: false);
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                catalog.RecordLinuxConnectionResult(id, "CONNECTED", connected: true);
                return new { status = "CONNECTED", message = "SSH 连接成功。" };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var failure = LinuxSshTerminalTransport.Failure(ex);
                catalog.RecordLinuxConnectionResult(id, failure.Status);
                return new { status = failure.Status, message = failure.Message };
            }
        }
        finally { _slots.Release(); }
    }
}
