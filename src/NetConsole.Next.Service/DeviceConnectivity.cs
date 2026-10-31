using System.Diagnostics;
using System.Net.Sockets;

namespace NetConsole.Next.Service;

internal sealed class DeviceConnectivityService(IH3cCredentialProvider credentials)
{
    public async Task<DeviceConnectivityTestResult> TestAsync(DeviceConnectivityTestRequest request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var protocol = request.Protocol.Trim().ToUpperInvariant();
        if (protocol is not ("SSH" or "TELNET"))
            throw new ArgumentException("Protocol must be SSH or TELNET for a network connectivity test.", nameof(request));
        if (request.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(request.Port));
        if (string.IsNullOrWhiteSpace(request.ManagementAddress) ||
            request.ManagementAddress.Length > 253 || request.ManagementAddress.Any(char.IsWhiteSpace) ||
            request.ManagementAddress.Any(char.IsControl) || Uri.CheckHostName(request.ManagementAddress) == UriHostNameType.Unknown)
            throw new ArgumentException("Management address must be a valid IP address or DNS hostname.", nameof(request));
        if (!H3cCredentialReference.IsValid(request.CredentialReference))
            throw new ArgumentException("Credential reference is invalid.", nameof(request));
        if (request.HostKeyUpdatePolicy.Trim().ToUpperInvariant() != "STRICT")
            throw new ArgumentException("Connectivity testing requires STRICT SSH host-key verification.", nameof(request));
        var fingerprint = string.IsNullOrWhiteSpace(request.HostKeySha256) ? string.Empty : H3cHostKeyFingerprint.Normalize(request.HostKeySha256);
        if (protocol == "SSH" && fingerprint.Length == 0)
            throw new ArgumentException("SSH connectivity testing requires a SHA256 host-key fingerprint.", nameof(request));
        if (!TerminalTextEncoding.IsSupported(request.TerminalEncoding))
            throw new ArgumentException("Terminal encoding must be AUTO, UTF-8 or GB18030.", nameof(request));
        var timeoutMs = Math.Clamp(request.TimeoutMs, 1000, 60000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        try
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(request.ManagementAddress, request.Port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed("ADDRESS", true, false, false, false, "地址或端口连接超时。", started);
            }
            catch (SocketException ex)
            {
                return Failed("ADDRESS", false, false, false, false, SocketDetail(ex.SocketErrorCode), started);
            }
            catch (TimeoutException)
            {
                return Failed("ADDRESS", false, false, false, false, "地址或端口连接超时。", started);
            }

            var password = credentials.GetPassword(request.CredentialReference);
            if (string.IsNullOrEmpty(password))
                return Failed("CREDENTIAL", true, false, false, false, "凭据引用未在受控凭据库中找到可用秘密；页面不会接收或保存明文密码。", started);

            var profile = new H3cDeviceProfileSnapshot(Guid.NewGuid().ToString("N"), request.ManagementAddress.Trim(), request.Port,
                request.Username.Trim(), request.CredentialReference, fingerprint, true, 0, 0, "STRICT",
                request.AllowLegacyAlgorithms, request.TerminalEncoding.Trim().ToUpperInvariant(), protocol, false);
            if (protocol == "SSH")
            {
                using var transport = new SshNetH3cSshTransportFactory().Create(profile, password);
                await transport.ConnectAsync((_, presented) => H3cHostKeyFingerprint.Matches(fingerprint, presented), timeout.Token).ConfigureAwait(false);
                var versionOutput = await transport.ExecuteAsync(H3cCommandPolicy.DisplayVersion, timeout.Token).ConfigureAwait(false);
                var version = H3cComwareParser.ParseVersion(versionOutput);
                return new("SUCCEEDED", "IDENTITY", true, true, true, version.Recognized,
                    version.Recognized ? "SSH 握手、凭据认证和 H3C 版本识别均成功。" : "SSH 握手和凭据认证成功，但返回内容未识别为 H3C Comware。",
                    version.Recognized ? $"{version.Vendor} {version.Version}" : null, ElapsedMs(started));
            }

            await using (var transport = new TelnetTerminalTransport(profile, password))
            {
                await transport.ConnectAsync(timeout.Token).ConfigureAwait(false);
            }
            return new("PARTIAL", "AUTHENTICATION", true, true, true, false,
                "Telnet 握手和登录成功；当前受控探测不执行版本命令，因此设备身份仍待终端会话确认。", null, ElapsedMs(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed("CANCELLED", false, false, false, false, "连通性测试已取消。", started, "CANCELLED");
        }
        catch (OperationCanceledException)
        {
            return Failed("TIMEOUT", true, false, false, false, "连通性测试超时。", started);
        }
        catch (H3cHostKeyMismatchException)
        {
            return Failed("HOST_KEY", true, true, false, false, "SSH 主机密钥与填写的 SHA256 指纹不匹配，未接受未知主机。", started);
        }
        catch (H3cAuthenticationFailedException)
        {
            return Failed("AUTHENTICATION", true, true, false, false, "协议握手成功，但凭据认证失败。", started);
        }
        catch (H3cCredentialUnavailableException)
        {
            return Failed("CREDENTIAL", true, false, false, false, "凭据引用没有可用秘密。", started);
        }
        catch (H3cConnectionTimeoutException)
        {
            return Failed("PROTOCOL", true, false, false, false, "协议握手超时。", started);
        }
        catch (Exception ex) when (ex is H3cUnsupportedAlgorithmException or H3cProtocolNegotiationException)
        {
            return Failed("PROTOCOL", true, false, false, false, "协议算法协商失败，未放宽安全策略。", started);
        }
        catch (SocketException ex)
        {
            return Failed("PROTOCOL", true, false, false, false, SocketDetail(ex.SocketErrorCode), started);
        }
        catch (IOException)
        {
            return Failed("PROTOCOL", true, false, false, false, "协议连接失败。", started);
        }
    }

    private static DeviceConnectivityTestResult Failed(string stage, bool addressReachable, bool handshake,
        bool authentication, bool identityVerified, string detail, long started, string outcome = "FAILED") =>
        new(outcome, stage, addressReachable, handshake, authentication, identityVerified, detail, null, ElapsedMs(started));

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static string SocketDetail(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "地址可达，但端口拒绝连接。",
        SocketError.HostNotFound or SocketError.NoData => "管理地址无法解析。",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "管理地址或网络不可达。",
        _ => "地址或端口不可达。"
    };
}
