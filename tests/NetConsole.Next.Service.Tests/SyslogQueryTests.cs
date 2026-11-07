using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SyslogQueryTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine("D:\\study\\NetConsole-Next-Workspace\\test-data", "r71-syslog-" + Guid.NewGuid().ToString("N"));
        var catalog = new Catalog(new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = 0,
            SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 65536 });
        catalog.Initialize();
        try
        {
            var writer = new RawSegmentWriter(catalog);
            await writer.AppendAsync(new(1000, "192.0.2.1", 514, Encoding.UTF8.GetBytes("<131>link down")));
            await writer.AppendAsync(new(2000, "192.0.2.2", 515, Encoding.UTF8.GetBytes("<134>link up")));
            await writer.AppendAsync(new(3000, "192.0.2.1", 514, Encoding.UTF8.GetBytes("unknown priority")));
            var query = new SyslogQuery(0, 4000);
            Assert(catalog.QuerySyslog(query, P2B5SiteScopeDecision.Global).Items.Count == 0, "active files excluded");
            await writer.SealAsync();
            var all = catalog.QuerySyslog(query, P2B5SiteScopeDecision.Global);
            Assert(all.Items.Count == 3 && all.Items[0].Severity is null && all.ScannedSegments == 1 && !all.Partial, "real sealed Raw ordered and unknown priority retained");
            var filtered = catalog.QuerySyslog(query with { SenderIp = "192.0.2.1", MaximumSeverity = 3, Text = "DOWN" }, P2B5SiteScopeDecision.Global);
            Assert(filtered.Items is [{ Severity: 3, Facility: 16, Line: 1 }], "exact source severity text and Raw line filters");
            Assert(catalog.QuerySyslog(query with { Limit = 1 }, P2B5SiteScopeDecision.Global) is { Partial: true, Items.Count: 1 }, "bounded results reported partial");
            try { catalog.QuerySyslog(query, P2B5SiteScopeDecision.Denied("fixture")); throw new Exception("scope accepted"); }
            catch (TaskScopeRevokedException) { }
            try { catalog.QuerySyslog(query with { Limit = 501 }, P2B5SiteScopeDecision.Global); throw new Exception("limit accepted"); }
            catch (ArgumentException) { }
            await File.AppendAllTextAsync(Path.Combine(root, writer.RelativePath), "tampered");
            Assert(catalog.QuerySyslog(query, P2B5SiteScopeDecision.Global) is { Partial: true, UnavailableSegments: 1, Items.Count: 0 }, "tampered Raw excluded");
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            socket.Close();
            var liveSettings = new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = port,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 65536 };
            var status = new RuntimeStatus();
            catalog.ProbeRawWritable();
            using var worker = new UdpSyslogWorker(liveSettings, catalog, new RawSegmentWriterFactory(), new IngressStats(), status,
                NullLogger<UdpSyslogWorker>.Instance);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                var readyDeadline = DateTime.UtcNow.AddSeconds(3);
                while (status.CollectionState == "starting" && DateTime.UtcNow < readyDeadline) await Task.Delay(25);
                Assert(status.CollectionState == "listening", "UDP listener readiness");
                using var sender = new UdpClient();
                var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await sender.SendAsync(Encoding.UTF8.GetBytes("<134>live low volume fixture"), new IPEndPoint(IPAddress.Loopback, port));
                var deadline = DateTime.UtcNow.AddSeconds(9);
                SyslogQueryResult? received = null;
                while (DateTime.UtcNow < deadline)
                {
                    received = catalog.QuerySyslog(new(sentAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), P2B5SiteScopeDecision.Global);
                    if (received.Items.Count > 0) break;
                    await Task.Delay(100);
                }
                Assert(received?.Items is [{ Message: "<134>live low volume fixture", Severity: 6 }] && status.CollectionState == "listening", $"real UDP low-volume segment sealed while service remains listening: state={status.CollectionState}, error={status.CollectionError}, health={status.HealthError}, count={received?.Items.Count}");
            }
            finally { await worker.StopAsync(CancellationToken.None); }
            Console.WriteLine("SyslogQuery: 8/8 PASS (including actual UDP timed seal)");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
