using System.Diagnostics;
using System.Net.Sockets;

namespace NetConsole.Next.Service;

internal sealed record H3cConnectionTestResult(
    string DeviceId,
    string Host,
    int Port,
    string Protocol,
    string Status,
    string Message,
    string? Vendor,
    string? Version,
    string? HostKeyDecision,
    long ElapsedMs);

/// <summary>
/// Runs the one-click connection check without returning transport output or
/// exception details.  SSH uses the existing read-only session path so host-key
/// first-trust/rotation and credential handling stay identical to collection.
/// </summary>
internal sealed class H3cConnectionTestService(
    Catalog catalog,
    IDeviceSessionFactory sessions)
{
    // A connection check must remain a bounded login probe.  Some older HCL
    // images accept SSH authentication but never close an exec channel for
    // `display version`; the long collection deadline must not make the UI
    // wait for minutes in that case.
    private static readonly TimeSpan DeviceInfoTimeout = TimeSpan.FromSeconds(15);

    public async Task<H3cConnectionTestResult?> TestAsync(string deviceId, CancellationToken cancellationToken)
    {
        var profile = catalog.GetH3cDeviceProfile(deviceId);
        if (profile is null) return null;
        var started = Stopwatch.GetTimestamp();
        var protocol = ProtocolFor(profile);
        try
        {
            await using var session = sessions.Create(deviceId);
            try { await session.ConnectAsync(cancellationToken).ConfigureAwait(false); }
            finally { protocol = session.Protocol; }
            // Explicit Telnet and AUTO fallback use the same authenticated
            // session as collection. A login probe need not run another command.
            if (protocol == "TELNET")
                return Success(profile, started, protocol, new H3cVersionInfo("unknown", string.Empty, null, false));
            using var deviceInfoTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deviceInfoTimeout.CancelAfter(DeviceInfoTimeout);
            try
            {
                var versionOutput = await session.ExecuteAsync(H3cCommandPolicy.DisplayVersion, deviceInfoTimeout.Token)
                    .ConfigureAwait(false);
                var version = H3cComwareParser.ParseVersion(versionOutput);
                return Success(profile, started, protocol, version);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Authentication and host-key handling already succeeded.  A
                // device-info command timeout is a useful, bounded result and
                // must not be reported as an HTTP 500 or a failed login.
                return ConnectedWithoutDeviceInfo(profile, started,
                    "连接成功，但设备信息识别超时");
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The SSH handshake and password authentication have already
                // completed.  Older HCL images may close or stall an Exec
                // channel; preserve the useful login result and keep the
                // device-info failure separate from credential failures.
                return ConnectedWithoutDeviceInfo(profile, started,
                    "连接成功，但设备信息识别失败");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(profile, started, protocol, MapFailure(ex));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Telnet login uses a short internal read window.  Translate that
            // timeout to the same safe connection result as SSH instead of
            // letting the request pipeline emit an unhandled 500.
            return Failure(profile, started, protocol,
                ("CONNECT_TIMEOUT", "连接超时"));
        }
    }

    private static H3cConnectionTestResult Success(H3cDeviceProfileSnapshot profile, long started,
        string protocol, H3cVersionInfo version) =>
        new(profile.Id, profile.Host, profile.Port, protocol,
            version.Recognized ? "CONNECTED" : "DEVICE_INFO_UNRECOGNIZED",
            version.Recognized ? "连接成功，设备信息识别成功" : "连接成功，但未识别设备版本",
            version.Recognized ? version.Vendor : null,
            version.Recognized ? version.Version : null,
            null, ElapsedMs(started));

    private static H3cConnectionTestResult ConnectedWithoutDeviceInfo(
        H3cDeviceProfileSnapshot profile, long started, string message) =>
        new(profile.Id, profile.Host, profile.Port, "SSH", "CONNECTED", message,
            null, null, null, ElapsedMs(started));

    private static H3cConnectionTestResult Failure(H3cDeviceProfileSnapshot profile, long started,
        string protocol, (string Code, string Message) failure) =>
        new(profile.Id, profile.Host, profile.Port, protocol, failure.Code, failure.Message,
            null, null, null, ElapsedMs(started));

    private static string ProtocolFor(H3cDeviceProfileSnapshot profile) =>
        profile.TerminalProtocol is "TELNET" ? "TELNET" : "SSH";

    private static (string Code, string Message) MapFailure(Exception exception) => exception switch
    {
        H3cJumpHostConnectionException jump => (jump.Code, jump.Message),
        H3cCredentialUnavailableException => ("CREDENTIAL_UNAVAILABLE", "未配置可用凭据"),
        H3cAuthenticationFailedException => ("AUTHENTICATION_FAILED", "账号认证失败"),
        H3cHostKeyMismatchException or H3cHostKeyMissingException or H3cJumpHostKeyMismatchException or H3cJumpHostKeyMissingException
            => ("HOST_KEY_REJECTED", "主机身份校验失败"),
        H3cConnectionTimeoutException or TimeoutException => ("CONNECT_TIMEOUT", "连接超时"),
        H3cProtocolNegotiationException or H3cUnsupportedAlgorithmException => ("PROTOCOL_MISMATCH", "协议或算法不匹配"),
        H3cConnectionLostException or SocketException => ("UNREACHABLE", "设备不可达"),
        IOException => ("UNREACHABLE", "设备不可达"),
        InvalidDataException or PlatformNotSupportedException => ("CREDENTIAL_UNAVAILABLE", "未配置可用凭据"),
        ArgumentException => ("PROTOCOL_MISMATCH", "协议配置不匹配"),
        _ => ("CONNECTION_FAILED", "连接失败")
    };

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
