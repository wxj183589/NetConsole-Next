using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal static class IcmpDeploymentDiagnostics
{
    // This opt-in deployment probe uses the production TTL probe with a fixed
    // loopback target, before Host/mutex/data-root initialization.
    internal static async Task<int> RunAsync(TextWriter output, INetworkTracerouteProbe? probe = null,
        INetworkTracerouteProbe? nativeProbe = null)
    {
        // Run both implementations in this exact process/token. A standalone
        // dotnet diagnosis is not evidence that the running apphost can probe.
        // The system path remains authoritative; native API success is never a
        // retry or a replacement for a failed production Traceroute.
        var nativeErrors = new List<object>();
        string nativeState;
        string? nativeStatus = null;
        try
        {
            using var nativeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var nativeReply = await (nativeProbe ?? new IcmpNetworkTracerouteProbe()).SendAsync(
                IPAddress.Loopback, 1, 1500, nativeDeadline.Token);
            nativeStatus = nativeReply.Status.ToString();
            nativeState = nativeReply.Status == IPStatus.Success && IPAddress.Loopback.Equals(nativeReply.Address)
                ? "PASS" : "FAILED";
        }
        catch (Exception exception) when (IsProbeFailure(exception))
        {
            nativeState = "FAILED";
            CaptureErrors(exception, nativeErrors);
        }
        var errors = new List<object>();
        string state;
        string? status = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (probe is null)
            {
                var trace = await NetworkTraceroute.RunAsync("127.0.0.1", 2000, new IcmpNetworkTracerouteProbe(),
                    () => { }, _ => { }, deadline.Token);
                status = trace.DestinationReached ? IPStatus.Success.ToString() : trace.Hops.LastOrDefault()?.Status;
                state = trace.DestinationReached ? "PASS" : "FAILED";
            }
            else
            {
                var reply = await probe.SendAsync(IPAddress.Loopback, 1, 2000, deadline.Token);
                status = reply.Status.ToString();
                state = reply.Status == IPStatus.Success && IPAddress.Loopback.Equals(reply.Address) ? "PASS" : "FAILED";
            }
        }
        catch (Exception exception) when (IsProbeFailure(exception))
        {
            state = "FAILED";
            CaptureErrors(exception, errors);
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(new {
            state, target = "127.0.0.1", ttl = 1, status,
            probeKind = probe is not null ? "INJECTED_ICMP" : OperatingSystem.IsWindows() ? "WINDOWS_SYSTEM_ICMP_TRACERT" : "DOTNET_ICMP",
            executable = Environment.ProcessPath,
            processId = Environment.ProcessId, runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), errors,
            nativeIcmp = new { state = nativeState, status = nativeStatus,
                probeKind = "DOTNET_ICMP", errors = nativeErrors },
            dataRootAccessed = false, hostStarted = false
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return state == "PASS" ? 0 : 1;
    }

    private static bool IsProbeFailure(Exception exception) => exception is PingException or
        System.Net.Sockets.SocketException or UnauthorizedAccessException or OperationCanceledException or Win32Exception;

    private static void CaptureErrors(Exception exception, List<object> errors)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            errors.Add(new { type = current.GetType().FullName, current.HResult,
                nativeErrorCode = current is Win32Exception native ? (int?)native.NativeErrorCode : null });
    }
}
