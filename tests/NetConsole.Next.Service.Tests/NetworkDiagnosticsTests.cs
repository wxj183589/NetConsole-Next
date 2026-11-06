using NetConsole.Next.Service;
using Microsoft.Data.Sqlite;
using System.Net;

internal static class NetworkDiagnosticsTests
{
    public static async Task RunAsync()
    {
        await BatchReportsProgressAndIsolatesFailures();
        await CancellationStopsQueuedChecks();
        await LldpReportsOnlyVerifiedFacts();
        await PersistsCompletedRunAcrossRestart();
        Console.WriteLine("NETWORK_DIAGNOSTICS_TESTS=4/4 PASS");
    }

    private static async Task BatchReportsProgressAndIsolatesFailures()
    {
        using var fixture = new Fixture();
        var first = fixture.Catalog.CreateDeviceInventory(new(null, "Loopback", "127.0.0.1", "", "", "NETWORK_DEVICE", "local", null, "fixture", true, null), "test");
        var second = fixture.Catalog.CreateDeviceInventory(new(null, "Unreachable", "192.0.2.250", "", "", "NETWORK_DEVICE", "local", null, "fixture", true, null), "test");
        var diagnostics = new NetworkDiagnosticsService(fixture.Catalog);
        var run = diagnostics.Start(new(new[] { first.Item.Id, second.Item.Id }, new[] { "PING", "TCP", "DNS" }, 500), "test", P2B5SiteScopeDecision.Global);
        var final = await WaitTerminal(diagnostics, run.Id);
        Assert(final.TotalDevices == 2 && final.TotalChecks == 6 && final.ProgressPercent == 100,
            "diagnostic batch should expand selected devices into bounded per-kind checks and report complete progress");
        Assert(final.Items.All(item => item.State is "SUCCEEDED" or "FAILED" or "NO_EVIDENCE" or "CANCELLED"),
            "every diagnostic child must end in an explicit terminal state");
        Assert(final.Items.Any(item => item.DeviceId == first.Item.Id && item.Kind == "DNS" && item.State == "SUCCEEDED"),
            "a successful device check must remain successful when another device fails");
        Assert(final.Items.Any(item => item.DeviceId == second.Item.Id && item.State == "FAILED"),
            "an unreachable device must be isolated as a failed child task");
        Assert(!final.Items.Any(item => item.Detail?.Contains("password", StringComparison.OrdinalIgnoreCase) == true),
            "diagnostic results must not expose credentials or connection secrets");
    }

    private static async Task CancellationStopsQueuedChecks()
    {
        using var fixture = new Fixture();
        var devices = Enumerable.Range(1, 5).Select(index => fixture.Catalog.CreateDeviceInventory(
            new(null, "Cancel fixture " + index, "127.0.0." + index, "", "", "NETWORK_DEVICE", "local", null, "fixture", true, null), "test").Item.Id).ToArray();
        var probe = new CancellationProbe();
        using var diagnostics = new NetworkDiagnosticsService(fixture.Catalog, probe);
        const string actor = "local-api-token";
        var run = diagnostics.Start(new(devices, new[] { "DNS", "TRACEROUTE" }, 5_000), actor, P2B5SiteScopeDecision.Global);
        using (var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            // Keep the running probes inside this fixture, with literal loopback
            // DNS checks already successful and later checks still queued.
            while (true)
            {
                var progress = diagnostics.Get(run.Id, P2B5SiteScopeDecision.Global)!;
                if (progress.SucceededChecks > 0 && progress.RunningChecks == 4 && progress.QueuedChecks > 0 &&
                    probe.SendCount == 4) break;
                await Task.Delay(10, waiting.Token);
            }
        }
        Assert(diagnostics.Cancel(run.Id, actor, "SUPERADMIN", P2B5SiteScopeDecision.Global),
            "the owning operator should be able to cancel a diagnostic batch");
        var final = await WaitTerminal(diagnostics, run.Id);
        Assert(final.ProgressPercent == 100 && final.Items.All(item => item.State != "RUNNING" && item.State != "QUEUED"),
            "cancellation should drain running and queued diagnostic children");
        Assert(final.State is "CANCELLED" or "PARTIAL", "cancelled diagnostics must remain visibly cancelled or partial");
        Assert(final.State == "PARTIAL" && final.SucceededChecks > 0 && final.CancelledChecks > 0 &&
            final.FailedChecks == 0 && final.NoEvidenceChecks == 0 && probe.SendCount == 4,
            "successful children must survive cancellation without making a mixed batch successful or starting queued probes");
        using var restarted = new NetworkDiagnosticsService(fixture.Catalog);
        var restored = restarted.Get(run.Id, P2B5SiteScopeDecision.Global);
        Assert(restored?.State == "PARTIAL" && restored.CancelledChecks == final.CancelledChecks &&
            restored.SucceededChecks == final.SucceededChecks,
            "mixed cancellation and successful children must retain their actual terminal state across restart");
    }

    private static async Task LldpReportsOnlyVerifiedFacts()
    {
        using var fixture = new Fixture();
        var profile = new H3cDeviceProfileSnapshot(Guid.NewGuid().ToString("N"), "127.0.0.1", 22,
            "reader", "DIAGNOSTIC_FIXTURE", string.Empty, true, 0, 0);
        fixture.Catalog.CreateH3cDeviceProfile(profile);
        var lldp = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", "phase9_lldp_list.txt"));
        fixture.Catalog.ImportH3cOfflineReplay(profile.Id,
            new Dictionary<string, string> { [H3cCommandPolicy.DisplayLldpNeighborInformationList] = lldp },
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var diagnostics = new NetworkDiagnosticsService(fixture.Catalog);
        var run = diagnostics.Start(new(new[] { profile.Id }, new[] { "LLDP" }, 1_000), "test", P2B5SiteScopeDecision.Global);
        var final = await WaitTerminal(diagnostics, run.Id);
        var item = final.Items.Single();
        Assert(item.State == "SUCCEEDED" && item.EvidenceCount > 0 && item.EvidenceSources.Contains(Catalog.H3cOfflineFixtureSource),
            "LLDP diagnostics must report evidence from verified Facts and never synthesize a topology result");
    }

    private static async Task PersistsCompletedRunAcrossRestart()
    {
        using var fixture = new Fixture();
        var device = fixture.Catalog.CreateDeviceInventory(new(null, "Restart durable", "127.0.0.1", "", "", "NETWORK_DEVICE", "local", null, "fixture", true, null), "test");
        var firstService = new NetworkDiagnosticsService(fixture.Catalog);
        var started = firstService.Start(new(new[] { device.Item.Id }, new[] { "DNS" }, 1_000), "test", P2B5SiteScopeDecision.Global);
        var completed = await WaitTerminal(firstService, started.Id);
        Assert(completed.State is "SUCCEEDED" or "PARTIAL", "the restart fixture must finish before persistence is checked");
        firstService.Dispose();

        var restarted = new NetworkDiagnosticsService(fixture.Catalog);
        var restored = restarted.Get(started.Id, P2B5SiteScopeDecision.Global);
        Assert(restored is not null && restored.ProgressPercent == 100 && restored.Items.Count == 1 &&
               restored.Items[0].DeviceId == device.Item.Id,
            "completed diagnostic history must survive service recreation with bounded item data");
        restarted.Dispose();
    }

    private static async Task<NetworkDiagnosticRunSnapshot> WaitTerminal(NetworkDiagnosticsService service, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var run = service.Get(id, P2B5SiteScopeDecision.Global) ?? throw new InvalidOperationException("diagnostic run disappeared");
            if (run.ProgressPercent == 100 && (run.State is "SUCCEEDED" or "PARTIAL" or "FAILED" or "CANCELLED")) return run;
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class CancellationProbe : INetworkTracerouteProbe
    {
        private int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new[] { IPAddress.Loopback });
        public async Task<NetworkTracerouteProbeReply> SendAsync(IPAddress address, int ttl, int timeoutMs,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("A cancelled fixture probe cannot produce a reply.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine("D:\\study\\NetConsole-Next-Workspace\\test-data", "network-diagnostics-" + Guid.NewGuid().ToString("N"));
        public RuntimeSettings Settings { get; }
        public Catalog Catalog { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings);
            Catalog.Initialize();
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
