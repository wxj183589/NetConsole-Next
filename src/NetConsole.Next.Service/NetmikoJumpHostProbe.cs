using System.Diagnostics;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed class NetmikoJumpHostProbeException(string code, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public string Code { get; } = code;
}

internal sealed record NetmikoJumpHostProbeResult(string HostKeyType, string HostKeySha256);

/// <summary>
/// Runs the pinned worker for a one-shot Jump Host SSH probe.  The worker owns
/// the SSH implementation; this side owns cancellation, bounded JSON I/O and
/// the Catalog fingerprint decision.
/// </summary>
internal sealed class NetmikoJumpHostProbe
{
    public async Task<NetmikoJumpHostProbeResult> ProbeAsync(
        SiteSshJumpHostSnapshot jumpHost, CancellationToken cancellationToken)
    {
        if (jumpHost.Password is null)
            throw new NetmikoJumpHostProbeException(
                "JUMP_HOST_CREDENTIAL_UNAVAILABLE",
                "当前局点未保存 SSH 中转密码。");

        Process? process = null;
        StreamWriter? input = null;
        StreamReader? output = null;
        NetmikoWorkerBudget.Lease? slot = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var token = deadline.Token;
            slot = await NetmikoWorkerBudget.AcquireAsync(token).ConfigureAwait(false);
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
            start.StandardInputEncoding = new System.Text.UTF8Encoding(false);
            start.StandardOutputEncoding = System.Text.Encoding.UTF8;
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            token.ThrowIfCancellationRequested();
            var processSlot = slot;
            slot = null;
            var worker = processSlot.Start(start);
            process = worker.Process;
            input = worker.Input;
            output = worker.Output;
            _ = Task.Run(async () =>
            {
                try { while (await worker.Error.ReadLineAsync().ConfigureAwait(false) is not null) { } }
                catch { }
                finally { worker.Error.Dispose(); }
            });

            var request = JsonSerializer.Serialize(new
            {
                op = "probe_jump",
                host = jumpHost.Host,
                port = jumpHost.Port,
                username = jumpHost.Username,
                password = jumpHost.Password,
                conn_timeout = 10,
                auth_timeout = 10,
                banner_timeout = 10
            });
            await input.WriteLineAsync(request).WaitAsync(token).ConfigureAwait(false);
            await input.FlushAsync(token).ConfigureAwait(false);
            var line = await output.ReadLineAsync(token).ConfigureAwait(false)
                ?? throw new IOException("Netmiko worker exited before returning a Jump Host probe result.");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString() : null;
                throw MapWorkerFailure(error);
            }

            var result = root.GetProperty("result");
            var keyType = result.TryGetProperty("host_key_type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() : null;
            var fingerprint = result.TryGetProperty("host_key_sha256", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
                ? keyElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(keyType) || string.IsNullOrWhiteSpace(fingerprint))
                throw new NetmikoJumpHostProbeException("JUMP_HOST_KEY_MISSING", "Jump Host did not return a verifiable host key.");
            return new(keyType!, H3cHostKeyFingerprint.Normalize(fingerprint!));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new NetmikoJumpHostProbeException("JUMP_HOST_CONNECTION_TIMEOUT", "SSH 中转连接超时。"); }
        catch (OperationCanceledException) { throw; }
        catch (NetmikoJumpHostProbeException) { throw; }
        catch (Exception ex)
        {
            throw new NetmikoJumpHostProbeException("JUMP_HOST_PROBE_FAILED", "SSH 中转连接失败，请检查服务器地址、端口和 SSH 服务。", ex);
        }
        finally
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            try { input?.Dispose(); } catch { }
            try { output?.Dispose(); } catch { }
            slot?.Dispose();
            // A started process retains its slot until the exit observer sees
            // actual termination, including when Kill fails.
        }
    }

    private static NetmikoJumpHostProbeException MapWorkerFailure(string? error) => error switch
    {
        "AuthenticationException" or "NetmikoAuthenticationException" =>
            new("JUMP_HOST_AUTHENTICATION_FAILED", "SSH 中转服务器认证失败，请检查用户名和密码。"),
        "TimeoutError" or "NetmikoTimeoutException" or "socket.timeout" =>
            new("JUMP_HOST_CONNECTION_TIMEOUT", "SSH 中转连接超时。"),
        _ => new("JUMP_HOST_PROBE_FAILED", "SSH 中转连接失败，请检查服务器地址、端口和 SSH 服务。")
    };

    private static string FindWorkerScript()
    {
        return NetmikoWorkerPaths.FindScript();
    }
}
