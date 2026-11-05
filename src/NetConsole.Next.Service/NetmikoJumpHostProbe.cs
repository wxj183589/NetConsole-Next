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
                "Jump Host password is not configured for this Site.");

        Process? process = null;
        StreamWriter? input = null;
        StreamReader? output = null;
        try
        {
            var script = FindWorkerScript();
            var start = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_PYTHON") ?? "python",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            start.ArgumentList.Add(script);
            process = Process.Start(start) ?? throw new InvalidOperationException("Netmiko worker could not start.");
            input = process.StandardInput;
            output = process.StandardOutput;
            _ = Task.Run(async () =>
            {
                try { while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } }
                catch { }
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
            await input.WriteLineAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
            var line = await output.ReadLineAsync(cancellationToken).ConfigureAwait(false)
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
        catch (OperationCanceledException) { throw; }
        catch (NetmikoJumpHostProbeException) { throw; }
        catch (Exception ex)
        {
            throw new NetmikoJumpHostProbeException("JUMP_HOST_PROBE_FAILED", "Jump Host probe failed.", ex);
        }
        finally
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            try { input?.Dispose(); } catch { }
            try { output?.Dispose(); } catch { }
            try { process?.Dispose(); } catch { }
        }
    }

    private static NetmikoJumpHostProbeException MapWorkerFailure(string? error) => error switch
    {
        "AuthenticationException" or "NetmikoAuthenticationException" =>
            new("JUMP_HOST_AUTHENTICATION_FAILED", "Jump Host authentication failed."),
        "TimeoutError" or "NetmikoTimeoutException" or "socket.timeout" =>
            new("JUMP_HOST_CONNECTION_TIMEOUT", "Jump Host connection timed out."),
        _ => new("JUMP_HOST_PROBE_FAILED", "Jump Host probe failed.")
    };

    private static string FindWorkerScript()
    {
        var configured = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_WORKER");
        var candidates = new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "tools", "netmiko_worker.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "netmiko_worker.py")
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!));
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Netmiko worker script was not found.");
    }
}
