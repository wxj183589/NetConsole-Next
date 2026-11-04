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
            var versionOutput = await session.ExecuteAsync(H3cCommandPolicy.DisplayVersion, cancellationToken)
                .ConfigureAwait(false);
            var version = H3cComwareParser.ParseVersion(versionOutput);
            return Success(profile, started, "SSH", version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(profile, started, ProtocolFor(profile), MapFailure(ex));
        }
    }

    private async Task<H3cConnectionTestResult> TestTelnetAsync(
        H3cDeviceProfileSnapshot profile, long started, CancellationToken cancellationToken)
    {
        var password = credentials.GetPassword(profile.CredentialReference);
        if (string.IsNullOrEmpty(password))
            return Failure(profile, started, "TELNET", ("CREDENTIAL_UNAVAILABLE", "未配置可用凭据"));
        await using var transport = new TelnetTerminalTransport(profile, password);
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
        H3cHostKeyMismatchException or H3cHostKeyMissingException => ("HOST_KEY_REJECTED", "主机身份校验失败"),
        H3cConnectionTimeoutException or TimeoutException => ("CONNECT_TIMEOUT", "连接超时"),
        H3cProtocolNegotiationException or H3cUnsupportedAlgorithmException => ("PROTOCOL_MISMATCH", "协议或算法不匹配"),
        SocketException => ("UNREACHABLE", "设备不可达"),
        IOException => ("UNREACHABLE", "设备不可达"),
        ArgumentException => ("PROTOCOL_MISMATCH", "协议配置不匹配"),
        _ => ("CONNECTION_FAILED", "连接失败")
    };

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
