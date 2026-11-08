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
        Assert(final is { Sent: 3, Received: 2, Lost: 1, LossPercent: 33.33, MinMs: 2, MaxMs: 4, AverageMs: 3, P95Ms: 4 }, "loss and latency use actual replies only");
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
        NetworkQuickPing.ValidateLongPing(3_600, 1_000, 2_000, "ICMP", 8);
        foreach (var arguments in new[] { (9, 1_000, 2_000, "ICMP", 1), (3_601, 1_000, 2_000, "ICMP", 1),
            (60, 999, 2_000, "ICMP", 1), (60, 1_000, 2_000, "OTHER", 1), (60, 1_000, 2_000, "TCP", 9) })
        {
            try { NetworkQuickPing.ValidateLongPing(arguments.Item1, arguments.Item2, arguments.Item3, arguments.Item4, arguments.Item5); throw new Exception("invalid long ping limits accepted"); }
            catch (ArgumentException) { }
        }
        var longRun = await NetworkQuickPing.RunForDurationAsync("fixture", 22, "TCP", 1, 1_000, 100,
            new Probe(), () => { }, _ => { }, CancellationToken.None);
        Assert(longRun is { RequestedSamples: 1, Sent: 1, P95Ms: 2 }, "duration-based long ping reports the bounded sample plan and reply P95");
        var noReplies = await NetworkQuickPing.RunAsync("fixture", 22, "TCP", 2, 100, 100,
            new Probe { AllLost = true }, () => { }, _ => { }, CancellationToken.None);
        Assert(noReplies is { LossPercent: 100, AverageMs: null, MinMs: null, MaxMs: null, P95Ms: null }, "missing latency remains unknown");
        var ranked = final with { Samples = Enumerable.Range(1, 20).Reverse().Select(value => new QuickPingSample(value, 1, "REPLIED", value == 20 ? 1_000 : value)).ToArray() };
        Assert(ranked.P95Ms == 19 && progress[0].P95Ms is null && progress[1].P95Ms == 2, "nearest-rank P95 excludes the top 5 percent and preserves empty/progress boundaries");
        var legacyJson = System.Text.Json.JsonSerializer.Serialize(final);
        legacyJson = legacyJson.Replace(",\"P95Ms\":4", "");
        Assert(System.Text.Json.JsonSerializer.Deserialize<QuickPingSnapshot>(legacyJson)?.P95Ms == 4, "old snapshots derive P95 from original replies");
        var revoked = 0;
        try
        {
            await NetworkQuickPing.RunAsync("fixture", 22, "TCP", 2, 100, 100, new Probe(),
                () => { if (++revoked == 4) throw new DeviceSiteScopeException("REVOKED", "fixture"); }, _ => { }, CancellationToken.None);
            throw new Exception("revocation expected");
        }
        catch (DeviceSiteScopeException) { }
        await LongPingControlTests.RunAsync();
        await LiveLoopbackHistoryAndScope();
        Console.WriteLine("QUICK_PING_TESTS=9/9 PASS (loss, progress, cancellation, limits, unknown, revocation, real TCP/history/scope, P95 rank, legacy P95)");
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
            string pausedHistoryId;
            using (var service = new NetworkDiagnosticsService(catalog))
            {
                id = service.Start(new([device], ["QUICK_PING"], 500, SampleCount: 3, IntervalMs: 100, QuickPingProtocol: "TCP"), actor, P2B5SiteScopeDecision.Global).Id;
                using var deadline = new CancellationTokenSource(5000);
                while (service.Get(id, P2B5SiteScopeDecision.Global) is not { ProgressPercent: 100 }) await Task.Delay(10, deadline.Token);
                Assert(service.Get(id, P2B5SiteScopeDecision.Global)?.Items.Single() is { State: "SUCCEEDED", QuickPing: { Sent: 3, Received: 3 } }, "real TCP sampling through inventory endpoint");
                var history = service.List(actor, P2B5SiteScopeDecision.Global);
                Assert(history.Single(item => item.Id == id).Targets is
                    [{ DeviceId: var historyDeviceId, DeviceName: var historyDeviceName, SiteId: var historySiteId }] &&
                    historyDeviceId == device && historyDeviceName == device && historySiteId == "local",
                    "diagnostic history summaries retain exact device and Site association");
                var longRun = service.Start(new([device], ["LONG_PING"], 500, IntervalMs: 1_000,
                    QuickPingProtocol: "TCP", LongPingDurationSeconds: 10), actor, P2B5SiteScopeDecision.Global);
                pausedHistoryId = longRun.Id;
                using var longDeadline = new CancellationTokenSource(5_000);
                while (service.Get(longRun.Id, P2B5SiteScopeDecision.Global)?.Items.Single().QuickPing?.Sent is not >= 1)
                    await Task.Delay(10, longDeadline.Token);
                Assert(service.SetLongPingPaused(longRun.Id, true, actor, "SUPERADMIN", P2B5SiteScopeDecision.Global), "long ping accepts pause");
                Assert(service.SetLongPingPaused(longRun.Id, true, actor, "SUPERADMIN", P2B5SiteScopeDecision.Global), "repeated service pause is idempotent");
                while (service.Get(longRun.Id, P2B5SiteScopeDecision.Global)?.PausedChecks != 1) await Task.Delay(10, longDeadline.Token);
                var paused = service.Get(longRun.Id, P2B5SiteScopeDecision.Global)!;
                await Task.Delay(250);
                Assert(paused is { State: "RUNNING", RunningChecks: 0, CompletedChecks: 0 } &&
                    service.Get(longRun.Id, P2B5SiteScopeDecision.Global)?.Items.Single().QuickPing?.Sent == paused.Items.Single().QuickPing?.Sent,
                    "paused task remains active with unchanged actual samples and separate paused count");
                var foreign = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "foreign" }, "fixture");
                Assert(!service.SetLongPingPaused(longRun.Id, false, actor, "SUPERADMIN", foreign) &&
                    !service.SetLongPingPaused(id, false, actor, "SUPERADMIN", P2B5SiteScopeDecision.Global), "foreign scope and terminal non-long tasks cannot be resumed");
                Assert(service.SetLongPingPaused(longRun.Id, false, actor, "SUPERADMIN", P2B5SiteScopeDecision.Global), "current owner can resume exact inventory target");
                while (service.Get(longRun.Id, P2B5SiteScopeDecision.Global)?.Items.Single().QuickPing?.Sent <= paused.Items.Single().QuickPing?.Sent)
                    await Task.Delay(10, longDeadline.Token);
                service.SetLongPingPaused(longRun.Id, true, actor, "SUPERADMIN", P2B5SiteScopeDecision.Global);
                while (service.Get(longRun.Id, P2B5SiteScopeDecision.Global)?.PausedChecks != 1) await Task.Delay(10, longDeadline.Token);
                Assert(service.Cancel(longRun.Id, actor, "SUPERADMIN", P2B5SiteScopeDecision.Global), "long ping remains cancellable during its duration window");
                using var cancelDeadline = new CancellationTokenSource(5_000);
                while (service.Get(longRun.Id, P2B5SiteScopeDecision.Global) is not { ProgressPercent: 100 }) await Task.Delay(10, cancelDeadline.Token);
                Assert(service.Get(longRun.Id, P2B5SiteScopeDecision.Global)?.Items.Single() is { Kind: "LONG_PING", State: "CANCELLED" }, "long ping cancellation is durably published");
                Assert(service.List(actor, P2B5SiteScopeDecision.Global).Single(item => item.Id == longRun.Id).Targets is
                    [{ DeviceId: var longPingDeviceId, SiteId: "local" }] && longPingDeviceId == device,
                    "cancelled Long Ping history retains its device and Site association");
            }
            using var restarted = new NetworkDiagnosticsService(catalog);
            Assert(restarted.Get(pausedHistoryId, actor, P2B5SiteScopeDecision.Global)?.Items.Single() is
                { State: "CANCELLED", QuickPing: { Sent: >= 2, Lost: 0 } } &&
                !restarted.SetLongPingPaused(pausedHistoryId, false, actor, "SUPERADMIN", P2B5SiteScopeDecision.Global),
                "restart retains real paused cancellation samples without replay or resume");
            Assert(restarted.Get(id, actor, P2B5SiteScopeDecision.Global)?.Items.Single().QuickPing?.Samples.Count == 3, "complete samples survive restart");
            var denied = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "foreign" }, "fixture");
            Assert(restarted.Get(id, actor, denied)?.Items.Count == 0 && restarted.List(actor, denied).Count == 0, "hidden samples and summaries do not leak across Sites");
            await PausedTargetChangesStop(catalog, db, actor);
        }
        finally { listener.Stop(); SqliteConnection.ClearAllPools(); }
    }

    private static async Task PausedTargetChangesStop(Catalog catalog, SqliteConnection db, string actor)
    {
        using var sites = db.CreateCommand();
        sites.CommandText = "INSERT OR IGNORE INTO sites(id,name) VALUES('long-other','Long other')";
        sites.ExecuteNonQuery();
        var addressSuffix = 20;
        foreach (var mutation in new[]
        {
            ("device_inventory", "enabled=0"), ("device_inventory", "site_id='long-other'"),
            ("device_inventory", "terminal_profile_id=NULL"), ("h3c_device_profiles", "host='127.0.0.253'"),
            ("h3c_device_profiles", "port=2222"), ("web_users", "disabled=1"), ("web_users", "role='ADMIN'")
        })
        {
            var device = Guid.NewGuid().ToString("N");
            catalog.CreateH3cDeviceProfile(new(device, $"127.0.0.{addressSuffix++}", 22, "fixture", "", "", true, 0, 0));
            using var service = new NetworkDiagnosticsService(catalog, new IcmpNetworkTracerouteProbe(), new Probe());
            var run = service.Start(new([device], ["LONG_PING"], 500, IntervalMs: 1000,
                QuickPingProtocol: "TCP", LongPingDurationSeconds: 10), actor, P2B5SiteScopeDecision.Global);
            service.SetLongPingPaused(run.Id, true, actor, "SUPERADMIN", P2B5SiteScopeDecision.Global);
            using var deadline = new CancellationTokenSource(5_000);
            while (service.Get(run.Id, P2B5SiteScopeDecision.Global)?.PausedChecks != 1) await Task.Delay(10, deadline.Token);
            var before = service.Get(run.Id, P2B5SiteScopeDecision.Global)!.Items.Single().QuickPing!.Sent;
            try
            {
                using var change = db.CreateCommand();
                change.CommandText = $"UPDATE {mutation.Item1} SET {mutation.Item2} WHERE id=$id";
                change.Parameters.AddWithValue("$id", mutation.Item1 == "web_users" ? actor : device);
                change.ExecuteNonQuery();
                var accepted = false;
                try { accepted = service.SetLongPingPaused(run.Id, false, actor, "SUPERADMIN", P2B5SiteScopeDecision.Global); }
                catch (DeviceSiteScopeException) { }
                Assert(!accepted, "resume revalidates current actor, Site, enabled state and exact profile endpoint");
                while (service.Get(run.Id, P2B5SiteScopeDecision.Global)?.ProgressPercent != 100) await Task.Delay(10, deadline.Token);
                var terminal = service.Get(run.Id, P2B5SiteScopeDecision.Global)!.Items.Single();
                Assert(terminal is { State: "CANCELLED", Outcome: "DIAGNOSTIC_SCOPE_REVOKED" } && terminal.QuickPing?.Sent == before,
                    "revoked paused task stops without another probe or invented loss");
            }
            finally
            {
                using var restore = db.CreateCommand();
                restore.CommandText = "UPDATE web_users SET disabled=0,role='SUPERADMIN' WHERE id=$id";
                restore.Parameters.AddWithValue("$id", actor); restore.ExecuteNonQuery();
            }
        }
        Console.WriteLine("LONG_PING_SERVICE_AUTHORITY_TESTS=7/7 PASS (enabled, Site, profile, host, port, disabled actor, role downgrade)");
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
