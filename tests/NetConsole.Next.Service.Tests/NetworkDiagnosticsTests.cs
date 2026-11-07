using NetConsole.Next.Service;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

internal static class NetworkDiagnosticsTests
{
    public static async Task RunAsync()
    {
        CompletionProgressWaitsForRunPublication();
        var historyIntegrity = Task.WhenAll(CorruptHistoryDoesNotBlockHealthyRuns(),
            FullCapacityCompletionRetainsEveryRun());
        try { await historyIntegrity; }
        catch { throw historyIntegrity.Exception!; }
        await BatchReportsProgressAndIsolatesFailures();
        await CancellationStopsQueuedChecks();
        await LldpReportsOnlyVerifiedFacts();
        await PersistsCompletedRunAcrossRestart();
        await ListsLiveAndCompletedWithoutNewChecks();
        await HistoryListBoundsAndRestoresSafeSummaries();
        await HistoryReadsRespectCurrentAccountAndSiteScope();
        Console.WriteLine("NETWORK_DIAGNOSTICS_TESTS=10/10 PASS");
    }

    private static void CompletionProgressWaitsForRunPublication()
    {
        using var fixture = new Fixture();
        var device = fixture.Catalog.CreateDeviceInventory(new(null, "Publication fixture", "127.0.0.1", "", "", "NETWORK_DEVICE", "local", null, "fixture", true, null), "test");
        var probe = new ControlledCompletionProbe();
        using var service = new NetworkDiagnosticsService(fixture.Catalog, probe);
        var context = new QueuedCompletionContext();
        var previous = SynchronizationContext.Current;
        NetworkDiagnosticRunSnapshot started;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            started = service.Start(new([device.Item.Id], ["TRACEROUTE"], 5_000), "local-api-token", P2B5SiteScopeDecision.Global);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        probe.CompleteNext();
        NetworkDiagnosticRunSnapshot current;
        do
        {
            context.RunOne();
            current = service.Get(started.Id, P2B5SiteScopeDecision.Global)!;
        } while (current.Items.Single().State != "SUCCEEDED");
        Assert(current.State == "RUNNING" && current.CompletedChecks == 1 && current.ProgressPercent < 100,
            "completed children must not announce 100% before terminal run/history publication");
        Assert(service.List("local-api-token", P2B5SiteScopeDecision.Global).Single().ProgressPercent < 100,
            "summary filtering must preserve the pending publication boundary");
        while (current.State == "RUNNING")
        {
            context.RunOne();
            current = service.Get(started.Id, P2B5SiteScopeDecision.Global)!;
        }
        Assert(current.ProgressPercent == 100 && current.State == "SUCCEEDED", "durably published completion must reach 100%");
        service.Dispose();
        using var restarted = new NetworkDiagnosticsService(fixture.Catalog);
        Assert(restarted.Get(started.Id, P2B5SiteScopeDecision.Global)?.Items.Single().TraceRoute?.DestinationReached == true,
            "a caller observing 100% may immediately recreate the service and read complete history");
    }

    private sealed class QueuedCompletionContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public void RunOne()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            (SendOrPostCallback Callback, object? State) callback;
            while (!_callbacks.TryDequeue(out callback))
            {
                timeout.Token.ThrowIfCancellationRequested();
                Thread.Sleep(1);
            }
            // Keep this runner outside the captured context, so child completion
            // and the run's awaiting continuation remain separate queued steps.
            callback.Callback(callback.State);
        }
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

    private static async Task ListsLiveAndCompletedWithoutNewChecks()
    {
        using var fixture = new Fixture();
        var device = fixture.Catalog.CreateDeviceInventory(new(null, "History live", "127.0.0.1", "", "", "NETWORK_DEVICE", "local", null, "fixture", true, null), "test");
        var probe = new CancellationProbe();
        using var diagnostics = new NetworkDiagnosticsService(fixture.Catalog, probe);
        var run = diagnostics.Start(new([device.Item.Id], ["TRACEROUTE"], 5_000), "local-api-token", P2B5SiteScopeDecision.Global);
        using (var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            while (probe.SendCount == 0) await Task.Delay(10, waiting.Token);
        for (var index = 0; index < 3; index++)
        {
            var listed = diagnostics.List("local-api-token", P2B5SiteScopeDecision.Global).Single();
            Assert(listed.Id == run.Id && listed.State == "RUNNING" && listed.TotalChecks == 1 &&
                listed.Kinds.SequenceEqual(["TRACEROUTE"]) && probe.SendCount == 1,
                "listing an active diagnostic must be read-only and cannot start another check");
        }
        Assert(diagnostics.Cancel(run.Id, "local-api-token", "SUPERADMIN", P2B5SiteScopeDecision.Global), "fixture run must cancel");
        await WaitTerminal(diagnostics, run.Id);
        using var restarted = new NetworkDiagnosticsService(fixture.Catalog, probe);
        var completed = restarted.List("local-api-token", P2B5SiteScopeDecision.Global).Single();
        Assert(completed.Id == run.Id && completed.State == "CANCELLED" && completed.ProgressPercent == 100 &&
            probe.SendCount == 1, "a persisted cancelled batch must be discoverable after restart without a new probe");
    }

    private static async Task HistoryListBoundsAndRestoresSafeSummaries()
    {
        using var fixture = new Fixture();
        var device = fixture.Catalog.CreateDeviceInventory(new(null, "History seed", "127.0.0.1", "", "", "NETWORK_DEVICE", "local", null, "fixture", true, null), "test");
        using var diagnostics = new NetworkDiagnosticsService(fixture.Catalog);
        var started = diagnostics.Start(new([device.Item.Id], ["DNS"], 1_000), "test", P2B5SiteScopeDecision.Global);
        var seed = await WaitTerminal(diagnostics, started.Id);
        for (var index = 1; index <= 130; index++)
        {
            var snapshot = seed with { Id = Guid.NewGuid().ToString("N"), CreatedMs = seed.CreatedMs + index };
            File.WriteAllText(Path.Combine(fixture.Root, "diagnostics", snapshot.Id + ".json"), JsonSerializer.Serialize(snapshot));
        }
        using var restarted = new NetworkDiagnosticsService(fixture.Catalog);
        var summaries = restarted.List("local-api-token", P2B5SiteScopeDecision.Global, 128);
        Assert(summaries.Count == 128 && summaries[0].CreatedMs == seed.CreatedMs + 130 &&
            summaries.Zip(summaries.Skip(1), (left, right) => left.CreatedMs >= right.CreatedMs).All(value => value),
            "history discovery must honor the existing 128-run bound and list newest first after restart");
        Assert(restarted.List("local-api-token", P2B5SiteScopeDecision.Global).Count == 50 &&
            restarted.List("local-api-token", P2B5SiteScopeDecision.Global, 1).Single().Id == summaries[0].Id,
            "summary response limits must apply before serialization");
        foreach (var limit in new[] { 0, -1, 129, int.MaxValue })
        {
            try { restarted.List("local-api-token", P2B5SiteScopeDecision.Global, limit); throw new InvalidOperationException("history limit accepted"); }
            catch (ArgumentException) { }
        }
        var serialized = JsonSerializer.Serialize(summaries);
        Assert(!serialized.Contains("Items", StringComparison.Ordinal) && !serialized.Contains("Actor", StringComparison.Ordinal) &&
            !serialized.Contains("127.0.0.", StringComparison.Ordinal), "history summaries must not contain child output, actors or endpoints");
    }

    private static async Task HistoryReadsRespectCurrentAccountAndSiteScope()
    {
        using var fixture = new Fixture();
        fixture.Execute("INSERT INTO sites(id,name) VALUES('history-other','History other')");
        var local = fixture.Catalog.CreateDeviceInventory(new(null, "History local", "127.0.0.1", "", "", "NETWORK_DEVICE", "local", null, "fixture", true, null), "test");
        var foreign = fixture.Catalog.CreateDeviceInventory(new(null, "History foreign", "127.0.0.2", "", "", "NETWORK_DEVICE", "history-other", null, "fixture", true, null), "test");
        using var diagnostics = new NetworkDiagnosticsService(fixture.Catalog);
        var mixed = diagnostics.Start(new([local.Item.Id, foreign.Item.Id], ["DNS"], 1_000), "test", P2B5SiteScopeDecision.Global);
        await WaitTerminal(diagnostics, mixed.Id);
        var hidden = diagnostics.Start(new([foreign.Item.Id], ["DNS"], 1_000), "test", P2B5SiteScopeDecision.Global);
        await WaitTerminal(diagnostics, hidden.Id);
        var reader = fixture.Scalar("SELECT id FROM web_users WHERE username='superadmin'");
        fixture.Execute("UPDATE web_users SET role='ADMIN' WHERE id=$id", reader);
        var scope = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "local" }, "fixture");
        var summary = diagnostics.List(reader, scope).Single();
        var detail = diagnostics.Get(mixed.Id, reader, scope);
        Assert(summary.Id == mixed.Id && summary.TotalDevices == 1 && summary.TotalChecks == 1 &&
            summary.CompletedChecks == 1 && detail?.Items.Single().DeviceId == local.Item.Id && detail.TotalDevices == 1,
            "existing read roles retain history access while all child counts and targets remain inside their current Site scope");
        var empty = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "history-empty" }, "fixture");
        Assert(diagnostics.List(reader, empty).Count == 0, "a batch with no visible child must not leak into history discovery");
        // Historical evidence keeps its captured Site identity when inventory later moves.
        fixture.Execute("UPDATE device_inventory SET site_id='history-other' WHERE id=$id", local.Item.Id);
        Assert(diagnostics.List(reader, scope).Single().Id == mixed.Id,
            "history discovery must preserve the existing captured-Site read contract after an inventory move");
        ExpectHistoryDenied(() => diagnostics.List(reader, P2B5SiteScopeDecision.Denied("fixture")));
        ExpectHistoryDenied(() => diagnostics.List("unknown-account", scope));
        fixture.Execute("UPDATE web_users SET disabled=1 WHERE id=$id", reader);
        ExpectHistoryDenied(() => diagnostics.List(reader, scope));
        ExpectHistoryDenied(() => diagnostics.Get(mixed.Id, reader, scope));
    }

    private static void ExpectHistoryDenied(Action read)
    {
        try { read(); }
        catch (DeviceSiteScopeException) { return; }
        throw new InvalidOperationException("History access must reject a revoked actor or denied Site scope.");
    }

    private static async Task CorruptHistoryDoesNotBlockHealthyRuns()
    {
        using var fixture = new Fixture();
        var device = fixture.Catalog.CreateDeviceInventory(new(null, "Corrupt history fixture", "127.0.0.1", "", "", "NETWORK_DEVICE", "local", null, "fixture", true, null), "test");
        NetworkDiagnosticRunSnapshot seed;
        using (var diagnostics = new NetworkDiagnosticsService(fixture.Catalog))
            seed = await WaitTerminal(diagnostics, diagnostics.Start(new([device.Item.Id], ["DNS"], 1_000),
                "test", P2B5SiteScopeDecision.Global).Id);
        NetworkDiagnosticRunSnapshot[] malformed = [
            seed with { Id = null! },
            seed with { Id = Guid.NewGuid().ToString("N"), Items = null! },
            seed with { Id = Guid.NewGuid().ToString("N"), Items = [null!] }
        ];
        var corruptFiles = malformed.Select(snapshot =>
        {
            var path = Path.Combine(fixture.Root, "diagnostics", Guid.NewGuid().ToString("N") + ".json");
            var content = JsonSerializer.Serialize(snapshot);
            File.WriteAllText(path, content);
            return (Path: path, Content: content);
        }).ToArray();

        using var restarted = new NetworkDiagnosticsService(fixture.Catalog);
        Assert(restarted.List("local-api-token", P2B5SiteScopeDecision.Global).Single().Id == seed.Id,
            "structurally corrupt history must be ignored while valid completed history remains readable");
        Assert(corruptFiles.All(file => File.ReadAllText(file.Path) == file.Content),
            "ignoring corrupt history must preserve the original files for diagnosis");
        var fresh = await WaitTerminal(restarted, restarted.Start(new([device.Item.Id], ["DNS"], 1_000),
            "test", P2B5SiteScopeDecision.Global).Id);
        Assert(fresh.State == "SUCCEEDED" && restarted.List("local-api-token", P2B5SiteScopeDecision.Global).Count == 2,
            "corrupt historical files must not block a new diagnostic or its persistence");
    }

    private static async Task FullCapacityCompletionRetainsEveryRun()
    {
        using var fixture = new Fixture();
        var device = fixture.Catalog.CreateDeviceInventory(new(null, "Capacity fixture", "127.0.0.1", "", "", "NETWORK_DEVICE", "local", null, "fixture", true, null), "test");
        var probe = new ControlledCompletionProbe();
        using var diagnostics = new NetworkDiagnosticsService(fixture.Catalog, probe);
        var runs = Enumerable.Range(0, 128).Select(_ => diagnostics.Start(
            new([device.Item.Id], ["TRACEROUTE"], 30_000), "local-api-token", P2B5SiteScopeDecision.Global)).ToArray();
        try
        {
            Assert(probe.PendingCount == 128, "the capacity fixture must hold all admitted batches in flight");
            probe.CompleteNext();
            var completed = await WaitTerminal(diagnostics, runs[0].Id);
            Assert(completed.State == "SUCCEEDED" &&
                diagnostics.List("local-api-token", P2B5SiteScopeDecision.Global, 128).Count == 128,
                "completing one of 128 admitted batches must preserve that result and every remaining batch");
            using var restarted = new NetworkDiagnosticsService(fixture.Catalog, probe);
            Assert(restarted.Get(completed.Id, P2B5SiteScopeDecision.Global)?.State == "SUCCEEDED",
                "a batch completed at capacity must remain durable while other batches are still active");
        }
        finally
        {
            while (probe.PendingCount > 0) probe.CompleteNext();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (diagnostics.List("local-api-token", P2B5SiteScopeDecision.Global, 128)
                .Any(run => run.State is "QUEUED" or "RUNNING"))
                await Task.Delay(10, timeout.Token);
        }
        using var finalRestart = new NetworkDiagnosticsService(fixture.Catalog, probe);
        Assert(finalRestart.List("local-api-token", P2B5SiteScopeDecision.Global, 128).Count == 128,
            "all 128 admitted batches must survive completion and restart without an extra retention eviction");
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

    private sealed class ControlledCompletionProbe : INetworkTracerouteProbe
    {
        private readonly ConcurrentQueue<(IPAddress Address, TaskCompletionSource<NetworkTracerouteProbeReply> Completion)> _pending = new();
        public int PendingCount => _pending.Count;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new[] { IPAddress.Loopback });
        public Task<NetworkTracerouteProbeReply> SendAsync(IPAddress address, int ttl, int timeoutMs,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<NetworkTracerouteProbeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((address, completion));
            return completion.Task.WaitAsync(cancellationToken);
        }
        public void CompleteNext()
        {
            if (!_pending.TryDequeue(out var pending)) throw new InvalidOperationException("No fixture probe is pending.");
            pending.Completion.SetResult(new(IPStatus.Success, pending.Address, 0));
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
        public void Execute(string sql, string? id = null)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "catalog.db"), Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
            if (id is not null) command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        public string Scalar(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "catalog.db"), Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
            return (string)command.ExecuteScalar()!;
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
