using System.Diagnostics;
using System.Text.Json;

namespace NetConsole.Next.LocalManager;

internal static class LocalServiceReadiness
{
    internal static async Task WaitForHealthyAsync(HttpClient client, int port, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var timer = Stopwatch.StartNew();
        var lastState = "health endpoint has not responded";
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var requestBudget = timeout - timer.Elapsed;
            if (requestBudget <= TimeSpan.Zero) break;
            requestTimeout.CancelAfter(requestBudget < TimeSpan.FromSeconds(2) ? requestBudget : TimeSpan.FromSeconds(2));
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/api/system/health",
                    HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(requestTimeout.Token);
                    var buffer = new byte[65_537];
                    var count = 0;
                    while (count < buffer.Length)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(count), requestTimeout.Token);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count == buffer.Length) throw new InvalidDataException("健康接口响应超过限制。");
                    using var health = JsonDocument.Parse(buffer.AsMemory(0, count));
                    var value = health.RootElement;
                    if (value.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("健康接口响应格式无效。");
                    var state = value.TryGetProperty("healthState", out var healthState) && healthState.ValueKind == JsonValueKind.String
                        ? healthState.GetString() : null;
                    var service = value.TryGetProperty("serviceState", out var serviceState) && serviceState.ValueKind == JsonValueKind.String
                        ? serviceState.GetString() : null;
                    var hasProcess = value.TryGetProperty("processId", out var processId) &&
                        processId.ValueKind == JsonValueKind.Number && processId.TryGetInt32(out var pid) && pid > 0;
                    if (state == "HEALTHY" && service == "running" && hasProcess) return;
                    lastState = $"serviceState={service ?? "UNKNOWN"}, healthState={state ?? "UNKNOWN"}, processId={(hasProcess ? "present" : "missing")}";
                }
                else lastState = $"health endpoint returned HTTP {(int)response.StatusCode}";
            }
            catch (HttpRequestException ex) { lastState = ex.GetType().Name; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A final request can run out of the shared budget after earlier probes
                // returned a useful degraded state. Preserve that diagnosis at expiry.
                if (lastState == "health endpoint has not responded")
                    lastState = "health request timed out";
            }
            catch (JsonException) { lastState = "health endpoint returned invalid JSON"; }

            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException($"SERVICE_NOT_READY：服务控制器已报告运行，但服务未能在 {timeout.TotalSeconds:0} 秒内通过本机健康检查（{lastState}）。请运行本机诊断并查看服务日志。");
    }
}
