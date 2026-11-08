using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using NetConsole.Next.Service;

internal static class IcmpDeploymentDiagnosticsTests
{
    internal static async Task RunAsync()
    {
        foreach (var status in new[] { IPStatus.Success, IPStatus.TimedOut })
        {
            using var text = new StringWriter();
            var exit = await IcmpDeploymentDiagnostics.RunAsync(text, new Probe(status));
            using var json = JsonDocument.Parse(text.ToString());
            var root = json.RootElement;
            Check(exit == (status == IPStatus.Success ? 0 : 1) && root.GetProperty("status").GetString() == status.ToString(), "probe exit/status must preserve actual IP result");
            Check(!root.GetProperty("hostStarted").GetBoolean() && !root.GetProperty("dataRootAccessed").GetBoolean(), "diagnostic cannot start the host or initialize data");
        }
        using var failed = new StringWriter();
        Check(await IcmpDeploymentDiagnostics.RunAsync(failed, new Probe(IPStatus.Success, true)) == 1, "native failure must fail diagnosis");
        using var error = JsonDocument.Parse(failed.ToString());
        var chain = error.RootElement.GetProperty("errors");
        Check(chain.GetArrayLength() == 2 && chain[1].GetProperty("nativeErrorCode").GetInt32() == 5 && !failed.ToString().Contains("private-test-message"), "preserve nested native code without arbitrary error text");
        Console.WriteLine("ICMP_DEPLOYMENT_DIAGNOSTICS_TESTS=3/3 PASS");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Probe(IPStatus status, bool fail = false) : INetworkTracerouteProbe
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => throw new Exception("fixed loopback diagnostic must not resolve DNS");
        public Task<NetworkTracerouteProbeReply> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken cancellationToken)
        {
            Check(address.Equals(IPAddress.Loopback) && ttl == 1 && timeoutMs == 2000 && cancellationToken.CanBeCanceled, "use fixed bounded loopback production probe contract");
            if (fail) throw new PingException("private-test-message", new Win32Exception(5));
            return Task.FromResult(new NetworkTracerouteProbeReply(status, status == IPStatus.Success ? address : null, null));
        }
    }
}
