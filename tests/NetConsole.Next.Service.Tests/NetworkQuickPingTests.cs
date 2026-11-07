using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

internal static class NetworkQuickPingTests
{
    public static async Task RunAsync()
    {
        var probe = new Probe();
        var progress = new List<QuickPingSnapshot>();
        var checks = 0;
        var final = await NetworkQuickPing.RunAsync("fixture", 22, "ICMP", 3, 100, 100,
            probe, () => checks++, progress.Add, CancellationToken.None);
        Assert(final is { Sent: 3, Received: 2, Lost: 1, LossPercent: 33.33, MinMs: 2, MaxMs: 4, AverageMs: 3 }, "loss and latency use actual replies only");
        Assert(progress[0].Sent == 0 && progress[1].Sent == 1 && probe.Resolves == 1 && checks == 8, "immutable progress, pinned DNS and authority boundaries");
        var cancel = new CancellationTokenSource();
        try
        {
            await NetworkQuickPing.RunAsync("fixture", 22, "TCP", 3, 100, 100, new Probe(), () => { },
                p => { if (p.Sent == 1) cancel.Cancel(); }, cancel.Token);
            throw new Exception("cancellation expected");
        }
        catch (OperationCanceledException) { }
        foreach (var arguments in new[] { (1, 100, 100, "ICMP", 1), (61, 100, 100, "ICMP", 1),
            (2, 99, 100, "ICMP", 1), (60, 5000, 5000, "ICMP", 1), (3, 100, 100, "OTHER", 1), (3, 100, 100, "TCP", 9) })
        {
            try { NetworkQuickPing.Validate(arguments.Item1, arguments.Item2, arguments.Item3, arguments.Item4, arguments.Item5); throw new Exception("invalid limits accepted"); }
            catch (ArgumentException) { }
        }
        var noReplies = await NetworkQuickPing.RunAsync("fixture", 22, "TCP", 2, 100, 100,
            new Probe { AllLost = true }, () => { }, _ => { }, CancellationToken.None);
        Assert(noReplies is { LossPercent: 100, AverageMs: null, MinMs: null, MaxMs: null }, "missing latency remains unknown");
        var revoked = 0;
        try
        {
            await NetworkQuickPing.RunAsync("fixture", 22, "TCP", 2, 100, 100, new Probe(),
                () => { if (++revoked == 4) throw new DeviceSiteScopeException("REVOKED", "fixture"); }, _ => { }, CancellationToken.None);
            throw new Exception("revocation expected");
        }
        catch (DeviceSiteScopeException) { }
        await LiveLoopbackHistoryAndScope();
        Console.WriteLine("QUICK_PING_TESTS=7/7 PASS (loss, progress, cancellation, limits, unknown, revocation, real TCP/history/scope)");
    }

    private static async Task LiveLoopbackHistoryAndScope()
    {
        var root = Path.Combine("D:\\study\\NetConsole-Next-Workspace\\test-data", "r70-quick-ping-" + Guid.NewGuid().ToString("N"));
        var catalog = new Catalog(new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = 0,
            SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 });
        catalog.Initialize();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db"), Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT id FROM web_users WHERE username='superadmin'";
        var actor = (string)command.ExecuteScalar()!;
        // Inventory port is used directly: no arbitrary per-request network endpoint.
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var device = Guid.NewGuid().ToString("N");
            catalog.CreateH3cDeviceProfile(new(device, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                "fixture", "", "", true, 0, 0));
            string id;
            using (var service = new NetworkDiagnosticsService(catalog))
            {
                id = service.Start(new([device], ["QUICK_PING"], 500, SampleCount: 3, IntervalMs: 100, QuickPingProtocol: "TCP"), actor, P2B5SiteScopeDecision.Global).Id;
                using var deadline = new CancellationTokenSource(5000);
                while (service.Get(id, P2B5SiteScopeDecision.Global) is not { ProgressPercent: 100 }) await Task.Delay(10, deadline.Token);
                Assert(service.Get(id, P2B5SiteScopeDecision.Global)?.Items.Single() is { State: "SUCCEEDED", QuickPing: { Sent: 3, Received: 3 } }, "real TCP sampling through inventory endpoint");
            }
            using var restarted = new NetworkDiagnosticsService(catalog);
            Assert(restarted.Get(id, actor, P2B5SiteScopeDecision.Global)?.Items.Single().QuickPing?.Samples.Count == 3, "complete samples survive restart");
            var denied = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "foreign" }, "fixture");
            Assert(restarted.Get(id, actor, denied)?.Items.Count == 0 && restarted.List(actor, denied).Count == 0, "hidden samples and summaries do not leak across Sites");
        }
        finally { listener.Stop(); SqliteConnection.ClearAllPools(); }
    }

    private sealed class Probe : IQuickPingProbe
    {
        public int Resolves; private int _calls; public bool AllLost;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken token) { Resolves++; return Task.FromResult(new[] { IPAddress.Loopback }); }
        public Task<(string Status, long? LatencyMs)> SendAsync(IPAddress address, int port, string protocol, int timeoutMs, CancellationToken token)
        {
            _calls++;
            return Task.FromResult<(string, long?)>(AllLost || _calls == 2 ? ("TIMED_OUT", null) : ("REPLIED", _calls == 1 ? 2 : 4));
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
