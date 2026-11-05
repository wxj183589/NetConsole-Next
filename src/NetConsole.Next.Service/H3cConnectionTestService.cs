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
    IDeviceSessionFactory sessions,
    IH3cCredentialProvider credentials)
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
        try
        {
            if (string.Equals(profile.TerminalProtocol, "TELNET", StringComparison.OrdinalIgnoreCase))
                return await TestTelnetAsync(profile, started, cancellationToken).ConfigureAwait(false);

            await using var session = sessions.Create(deviceId);
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            using var deviceInfoTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deviceInfoTimeout.CancelAfter(DeviceInfoTimeout);
            try
            {
                var versionOutput = await session.ExecuteAsync(H3cCommandPolicy.DisplayVersion, deviceInfoTimeout.Token)
                    .ConfigureAwait(false);
                var version = H3cComwareParser.ParseVersion(versionOutput);
                return Success(profile, started, "SSH", version);
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
        catch (H3cProtocolNegotiationException) when (
            string.Equals(profile.TerminalProtocol, "AUTO", StringComparison.OrdinalIgnoreCase) &&
            profile.AllowTelnetFallback)
        {
            try { return await TestTelnetAsync(profile, started, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return Failure(profile, started, "TELNET", MapFailure(ex)); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(profile, started, ProtocolFor(profile), MapFailure(ex));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Telnet login uses a short internal read window.  Translate that
            // timeout to the same safe connection result as SSH instead of
            // letting the request pipeline emit an unhandled 500.
            return Failure(profile, started, ProtocolFor(profile),
                ("CONNECT_TIMEOUT", "连接超时"));
        }
    }

    private async Task<H3cConnectionTestResult> TestTelnetAsync(
        H3cDeviceProfileSnapshot profile, long started, CancellationToken cancellationToken)
    {
        var password = credentials.GetPassword(profile);
        if (string.IsNullOrEmpty(password))
            return Failure(profile, started, "TELNET", ("CREDENTIAL_UNAVAILABLE", "未配置可用凭据"));
        await using var transport = new NetmikoTerminalTransport(profile with { TerminalProtocol = "TELNET" }, password, catalog);
        await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        // Telnet's login handshake is the connection check.  Avoid storing or
        // returning the initial prompt because it may contain device details.
        return Success(profile, started, "TELNET", new H3cVersionInfo("unknown", string.Empty, null, false));
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
        H3cCredentialUnavailableException => ("CREDENTIAL_UNAVAILABLE", "未配置可用凭据"),
        H3cAuthenticationFailedException => ("AUTHENTICATION_FAILED", "账号认证失败"),
        H3cHostKeyMismatchException or H3cHostKeyMissingException or H3cJumpHostKeyMismatchException or H3cJumpHostKeyMissingException
            => ("HOST_KEY_REJECTED", "主机身份校验失败"),
        H3cConnectionTimeoutException or TimeoutException => ("CONNECT_TIMEOUT", "连接超时"),
        H3cProtocolNegotiationException or H3cUnsupportedAlgorithmException => ("PROTOCOL_MISMATCH", "协议或算法不匹配"),
        SocketException => ("UNREACHABLE", "设备不可达"),
        IOException => ("UNREACHABLE", "设备不可达"),
        InvalidDataException or PlatformNotSupportedException => ("CREDENTIAL_UNAVAILABLE", "未配置可用凭据"),
        ArgumentException => ("PROTOCOL_MISMATCH", "协议配置不匹配"),
        _ => ("CONNECTION_FAILED", "连接失败")
    };

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
