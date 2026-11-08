using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

internal static class NetworkTracerouteTests
{
    public static async Task RunAsync(bool includeLiveLoopback = true)
    {
        await WindowsSystemTracerouteTests.RunAsync();
        await HopsResolveOnceAndRequireTheActualDestination();
        await StopsAtThirtyHops();
        await StopsOnUnreachable();
        await SupportsIpv6WithoutGuessingAnIpv4Address();
        using var fixture = new Fixture();
        if (includeLiveLoopback) await LoopbackAndRestartRetainStructuredHistory(fixture);
        await TotalDeadlineRetainsPartialHops(fixture);
        await DnsSharesTheTotalDeadline(fixture);
        await CancellationDrainsRunningAndQueuedChecks(fixture);
        await CurrentTargetChangesStopFurtherProbes(fixture);
        await CurrentActorChangesStopFurtherProbes(fixture, "disabled=1", "disabled=0");
        await CurrentActorChangesStopFurtherProbes(fixture, "role='ADMIN'", "role='SUPERADMIN'");
        AdmissionRejectsUnknownActorAndForeignSite(fixture);
        await ProbeFailuresRemainSanitized(fixture);
        await ProbePermissionDenialRetainsPartialHistory(fixture);
        await AllFiveKindsUseTheExistingBatchLimit(fixture);
        Console.WriteLine(includeLiveLoopback ? "NETWORK_TRACEROUTE_TESTS=15/15 PASS" :
            "NETWORK_TRACEROUTE_FIXTURE_TESTS=14/14 PASS (live ICMP loopback excluded; separate required gate)");
        Console.WriteLine("NETWORK_TRACEROUTE_FIXTURE=" + fixture.Root);
    }

    private static async Task HopsResolveOnceAndRequireTheActualDestination()
    {
        var destination = IPAddress.Parse("192.0.2.10");
        var router = IPAddress.Parse("192.0.2.1");
        var probe = new Probe
        {
            Resolve = (_, _) => Task.FromResult(new[] { destination }),
            Send = (_, ttl, _, _) => Task.FromResult(ttl switch
            {
                1 => new NetworkTracerouteProbeReply(IPStatus.TtlExpired, router, 4),
                2 => new NetworkTracerouteProbeReply(IPStatus.TimedOut, IPAddress.Any, null),
                3 => new NetworkTracerouteProbeReply(IPStatus.Success, router, 2),
                _ => new NetworkTracerouteProbeReply(IPStatus.Success, destination, 3)
            })
        };
        var boundaries = 0;
        var snapshots = new List<NetworkTracerouteSnapshot>();
        var trace = await NetworkTraceroute.RunAsync("fixture.invalid", 5_000, probe, () => boundaries++,
            snapshots.Add, CancellationToken.None);
        Assert(trace.DestinationReached && trace.Hops.Count == 4 && trace.DestinationAddress == destination.ToString(),
            "only a successful response from the resolved destination proves arrival");
        Assert(trace.Hops.Select(hop => hop.Status).SequenceEqual(new[] { "TTL_EXPIRED", "TIMED_OUT", "OTHER", "REACHED" }) &&
            trace.Hops[1].Address is null, "timed-out hops must remain gaps instead of invented router addresses");
        Assert(probe.ResolveCalls == 1 && probe.Calls.All(call => call.Address.Equals(destination)) &&
            probe.Calls.Select(call => call.Ttl).SequenceEqual(new[] { 1, 2, 3, 4 }), "one resolution pins every probe to the admitted destination");
        Assert(boundaries == 10 && snapshots.Count == 5 && snapshots[1].Hops.Count == 1,
            "authority is checked around DNS and every probe; published progress snapshots remain immutable");
        Assert(probe.Calls.All(call => call.TimeoutMs is > 0 and <= 1_000), "each hop has a bounded timeout");
    }

    private static async Task StopsAtThirtyHops()
    {
        var probe = new Probe();
        var trace = await NetworkTraceroute.RunAsync("fixture.invalid", 5_000, probe, () => { }, _ => { }, CancellationToken.None);
        Assert(!trace.DestinationReached && trace.MaxHops == 30 && trace.Hops.Count == 30 && probe.Calls.Count == 30 &&
            trace.Hops.Last().Ttl == 30, "an unconfirmed path must stop at exactly thirty probes");
    }

    private static async Task StopsOnUnreachable()
    {
        var probe = new Probe { Send = (_, _, _, _) => Task.FromResult(new NetworkTracerouteProbeReply(
            IPStatus.DestinationHostUnreachable, IPAddress.Parse("192.0.2.1"), null)) };
        var trace = await NetworkTraceroute.RunAsync("fixture.invalid", 5_000, probe, () => { }, _ => { }, CancellationToken.None);
        Assert(!trace.DestinationReached && trace.Hops.Single().Status == "UNREACHABLE" && probe.Calls.Count == 1,
            "an explicit unreachable response stops the path without claiming arrival");
    }

    private static async Task SupportsIpv6WithoutGuessingAnIpv4Address()
    {
        var probe = new Probe
        {
            Resolve = (_, _) => Task.FromResult(new[] { IPAddress.IPv6Loopback }),
            Send = (address, _, _, _) => Task.FromResult(new NetworkTracerouteProbeReply(IPStatus.Success, address, 0))
        };
        var trace = await NetworkTraceroute.RunAsync("fixture.invalid", 5_000, probe, () => { }, _ => { }, CancellationToken.None);
        Assert(trace.DestinationReached && trace.DestinationAddress == "::1" && probe.Calls.Single().Address.Equals(IPAddress.IPv6Loopback),
            "an IPv6-only resolution retains its exact address");
    }

    private static async Task LoopbackAndRestartRetainStructuredHistory(Fixture fixture)
    {
        var device = fixture.Device();
        string id;
        NetworkDiagnosticRunSnapshot completed;
        using (var service = new NetworkDiagnosticsService(fixture.Catalog))
        {
            id = fixture.Start(service, device, timeoutMs: 2_000).Id;
            completed = await Terminal(service, id);
            var item = completed.Items.Single();
            Assert(item.State == "SUCCEEDED" && item.TraceRoute is { DestinationReached: true } trace &&
                trace.Hops.Count == 1 && trace.Hops[0] is { Ttl: 1, Status: "REACHED", Address: "127.0.0.1" },
                "production ICMP loopback must reach its destination at TTL1");
        }
        // Simulate an older on-disk item without the optional traceRoute property.
        var legacyId = Guid.NewGuid().ToString("N");
        var legacy = completed with { Id = legacyId, Items = completed.Items.Select(item => item with { Kind = "DNS", TraceRoute = null }).ToArray() };
        var legacyJson = JsonSerializer.Serialize(legacy, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        File.WriteAllText(Path.Combine(fixture.Root, "diagnostics", legacyId + ".json"), legacyJson);
        using var restarted = new NetworkDiagnosticsService(fixture.Catalog);
        var restoredLegacy = restarted.Get(legacyId, P2B5SiteScopeDecision.Global);
        Assert(restarted.Get(id, P2B5SiteScopeDecision.Global)?.Items.Single().TraceRoute?.DestinationReached == true &&
            restoredLegacy?.Items.Single() is { Kind: "DNS", TraceRoute: null },
            "new hop history and legacy snapshots both survive restart");
        var foreign = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "foreign" }, "fixture");
        Assert(restarted.Get(id, foreign)?.Items.Count == 0, "persisted hop data remains inside the current Site filter");
    }

    private static async Task TotalDeadlineRetainsPartialHops(Fixture fixture)
    {
        var probe = new Probe { Send = async (_, ttl, _, token) =>
        {
            if (ttl > 1) await Task.Delay(Timeout.Infinite, token);
            return new(IPStatus.TtlExpired, IPAddress.Parse("192.0.2.1"), 1);
        } };
        using var service = new NetworkDiagnosticsService(fixture.Catalog, probe);
        var started = Stopwatch.GetTimestamp();
        var final = await Terminal(service, fixture.Start(service, fixture.Device(), timeoutMs: 500).Id);
        Assert(final.Items.Single() is { State: "FAILED", Outcome: "TIMEOUT", TraceRoute.Hops.Count: 1 } &&
            Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3) && probe.Calls.Count == 2,
            "one total deadline stops an in-flight hop and preserves only previously observed replies");
        Assert(probe.Calls.Last().TimeoutMs < 1_000, "a later probe receives only the remaining total budget");
    }

    private static async Task DnsSharesTheTotalDeadline(Fixture fixture)
    {
        var probe = new Probe { Resolve = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return []; } };
        using var service = new NetworkDiagnosticsService(fixture.Catalog, probe);
        var final = await Terminal(service, fixture.Start(service, fixture.Device(), timeoutMs: 100).Id);
        Assert(final.Items.Single() is { Outcome: "TIMEOUT", TraceRoute: null } && probe.Calls.Count == 0,
            "DNS cannot consume an unbounded timeout before the first TTL probe");
    }

    private static async Task CancellationDrainsRunningAndQueuedChecks(Fixture fixture)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new Probe { Send = async (_, _, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        } };
        using var service = new NetworkDiagnosticsService(fixture.Catalog, probe);
        var ids = Enumerable.Range(0, 5).Select(_ => fixture.Device()).ToArray();
        var run = service.Start(new(ids, ["TRACEROUTE"], 5_000), fixture.ActorId, P2B5SiteScopeDecision.Global);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var active = service.Get(run.Id, P2B5SiteScopeDecision.Global)!;
        Assert(active.RunningChecks <= 4 && active.QueuedChecks >= 1, "Traceroute reuses the existing four-check batch concurrency");
        Assert(service.Cancel(run.Id, fixture.ActorId, "SUPERADMIN", P2B5SiteScopeDecision.Global), "owning actor can cancel a trace batch");
        var final = await Terminal(service, run.Id);
        Assert(final.State == "CANCELLED" && final.CancelledChecks == 5 && final.ProgressPercent == 100 && probe.Calls.Count <= 4,
            "cancellation stops probes, drains queued work and retains explicit terminal progress");
    }

    private static async Task CurrentTargetChangesStopFurtherProbes(Fixture fixture)
    {
        fixture.Execute("INSERT OR IGNORE INTO sites(id,name) VALUES('trace-other','Trace other')");
        foreach (var mutation in new[] { "enabled=0", "management_ip='127.0.0.253'", "site_id='trace-other'" })
        {
            var device = fixture.Device();
            var probe = new Probe { Send = (_, ttl, _, _) =>
            {
                if (ttl == 2) fixture.Execute($"UPDATE device_inventory SET {mutation} WHERE id=$id", device);
                return Task.FromResult(new NetworkTracerouteProbeReply(IPStatus.TtlExpired, IPAddress.Parse("192.0.2.1"), 1));
            } };
            using var service = new NetworkDiagnosticsService(fixture.Catalog, probe);
            var final = await Terminal(service, fixture.Start(service, device).Id);
            Assert(final.Items.Single() is { State: "CANCELLED", Outcome: "DIAGNOSTIC_SCOPE_REVOKED", TraceRoute.Hops.Count: 1 } &&
                probe.Calls.Count == 2, "current enabled state, endpoint and Site are checked before continuing or publishing a hop");
        }
    }

    private static async Task CurrentActorChangesStopFurtherProbes(Fixture fixture, string mutation, string restore)
    {
        var probe = new Probe { Send = (_, ttl, _, _) =>
        {
            if (ttl == 2) fixture.Execute($"UPDATE web_users SET {mutation} WHERE id=$id", fixture.ActorId);
            return Task.FromResult(new NetworkTracerouteProbeReply(IPStatus.TtlExpired, IPAddress.Parse("192.0.2.1"), 1));
        } };
        using var service = new NetworkDiagnosticsService(fixture.Catalog, probe);
        try
        {
            var final = await Terminal(service, fixture.Start(service, fixture.Device()).Id);
            Assert(final.Items.Single() is { State: "CANCELLED", Outcome: "DIAGNOSTIC_SCOPE_REVOKED", TraceRoute.Hops.Count: 1 } && probe.Calls.Count == 2,
                "current actor disable or role downgrade stops further trace probes");
        }
        finally { fixture.Execute($"UPDATE web_users SET {restore} WHERE id=$id", fixture.ActorId); }
    }

    private static void AdmissionRejectsUnknownActorAndForeignSite(Fixture fixture)
    {
        var device = fixture.Device();
        var probe = new Probe();
        using var service = new NetworkDiagnosticsService(fixture.Catalog, probe);
        ExpectDenied(() => service.Start(new([device], ["TRACEROUTE"], 1_000), "unknown-actor", P2B5SiteScopeDecision.Global));
        var foreign = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "foreign" }, "fixture");
        ExpectDenied(() => service.Start(new([device], ["TRACEROUTE"], 1_000), fixture.ActorId, foreign));
        Assert(probe.ResolveCalls == 0 && probe.Calls.Count == 0, "denied admissions must not even resolve a target");
    }

    private static async Task ProbeFailuresRemainSanitized(Fixture fixture)
    {
        var probe = new Probe { Send = (_, _, _, _) => throw new PingException("fixture-secret-password") };
        using var service = new NetworkDiagnosticsService(fixture.Catalog, probe);
        var final = await Terminal(service, fixture.Start(service, fixture.Device()).Id);
        Assert(final.Items.Single().Outcome == "TRACEROUTE_PROBE_FAILED" &&
            !JsonSerializer.Serialize(final).Contains("fixture-secret-password", StringComparison.Ordinal),
            "native probe exceptions are classified without exposing raw exception text");
    }

    private static async Task AllFiveKindsUseTheExistingBatchLimit(Fixture fixture)
    {
        var probe = new Probe { Send = (address, _, _, _) => Task.FromResult(new NetworkTracerouteProbeReply(IPStatus.Success, address, 0)) };
        using var service = new NetworkDiagnosticsService(fixture.Catalog, probe);
        var run = service.Start(new([fixture.Device()], ["PING", "TCP", "DNS", "LLDP", "TRACEROUTE"], 500),
            fixture.ActorId, P2B5SiteScopeDecision.Global);
        var final = await Terminal(service, run.Id);
        Assert(final.TotalChecks == 5 && final.ProgressPercent == 100 && final.Items.Single(item => item.Kind == "TRACEROUTE").State == "SUCCEEDED",
            "the fifth kind reuses the existing independent child results and batch progress");
        try
        {
            service.Start(new([fixture.Device()], ["TRACEROUTE"], 30_001), fixture.ActorId, P2B5SiteScopeDecision.Global);
            throw new InvalidOperationException("unbounded timeout accepted");
        }
        catch (ArgumentException) { }
        var callsBeforeRejectedBatch = probe.Calls.Count;
        var oversized = Enumerable.Range(0, 103).Select(_ => fixture.Device()).ToArray();
        try
        {
            service.Start(new(oversized, ["PING", "TCP", "DNS", "LLDP", "TRACEROUTE"], 500),
                fixture.ActorId, P2B5SiteScopeDecision.Global);
            throw new InvalidOperationException("a 515-check batch bypassed the existing 512-check limit");
        }
        catch (ArgumentException) { }
        Assert(probe.Calls.Count == callsBeforeRejectedBatch, "rejecting a batch beyond the shared check budget must not start any probes");
    }

    private static async Task ProbePermissionDenialRetainsPartialHistory(Fixture fixture)
    {
        Exception[] denied = [new System.ComponentModel.Win32Exception(5, "sensitive fixture path"),
            new SocketException((int)SocketError.AccessDenied), new UnauthorizedAccessException("sensitive fixture path")];
        foreach (var error in denied)
        {
            var probe = new Probe { Send = (_, ttl, _, _) => ttl == 1
                ? Task.FromResult(new NetworkTracerouteProbeReply(IPStatus.TtlExpired, IPAddress.Parse("192.0.2.1"), 1))
                : throw new PingException("sensitive fixture path", error) };
            string id;
            using (var service = new NetworkDiagnosticsService(fixture.Catalog, probe))
            {
                id = fixture.Start(service, fixture.Device()).Id;
                var run = await Terminal(service, id);
                Assert(run.State == "PARTIAL" && run.FailedChecks == 1 && run.Items.Single() is
                    { State: "FAILED", Outcome: "ICMP_PERMISSION_DENIED", TraceRoute.DestinationReached: false, TraceRoute.Hops.Count: 1 },
                    "host permission denial must remain a failed check while retaining observed partial hops");
                Assert(run.Items.Single().Detail == "Service 主机拒绝 ICMP 探测，请检查服务账户或主机策略" && probe.Calls.Count == 2,
                    "permission denial is sanitized and does not retry the host operation");
            }
            using var restarted = new NetworkDiagnosticsService(fixture.Catalog, probe);
            Assert(restarted.Get(id, P2B5SiteScopeDecision.Global)?.Items.Single() is
                { Outcome: "ICMP_PERMISSION_DENIED", TraceRoute.Hops.Count: 1 },
                "the failure classification and partial path survive a service restart");
        }
    }

    private sealed class Probe : INetworkTracerouteProbe
    {
        private readonly object _gate = new();
        private readonly List<(IPAddress Address, int Ttl, int TimeoutMs)> _calls = [];
        public int ResolveCalls;
        public IReadOnlyList<(IPAddress Address, int Ttl, int TimeoutMs)> Calls { get { lock (_gate) return _calls.ToArray(); } }
        public Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; init; } = (_, _) => Task.FromResult(new[] { IPAddress.Loopback });
        public Func<IPAddress, int, int, CancellationToken, Task<NetworkTracerouteProbeReply>> Send { get; init; } =
            (_, _, _, _) => Task.FromResult(new NetworkTracerouteProbeReply(IPStatus.TtlExpired, IPAddress.Parse("192.0.2.1"), 1));
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        { Interlocked.Increment(ref ResolveCalls); return Resolve(host, cancellationToken); }
        public Task<NetworkTracerouteProbeReply> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken cancellationToken)
        { lock (_gate) _calls.Add((address, ttl, timeoutMs)); return Send(address, ttl, timeoutMs, cancellationToken); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine("D:\\study\\NetConsole-Next-Workspace\\test-data", "network-traceroute-" + Guid.NewGuid().ToString("N"));
        public Catalog Catalog { get; }
        public string ActorId { get; }
        private int _deviceSequence;
        public Fixture()
        {
            var settings = new RuntimeSettings { DataRoot = Root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(settings); Catalog.Initialize();
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT id FROM web_users WHERE username='superadmin'";
            ActorId = (string)command.ExecuteScalar()!;
        }
        public string Device()
        {
            var sequence = ++_deviceSequence;
            return Catalog.CreateDeviceInventory(new(null, "Trace fixture " + sequence, "127.0.0." + sequence, "", "", "NETWORK_DEVICE",
                "local", null, "fixture", true, null), "fixture").Item.Id;
        }
        public NetworkDiagnosticRunSnapshot Start(NetworkDiagnosticsService service, string device, int timeoutMs = 2_000) =>
            service.Start(new([device], ["TRACEROUTE"], timeoutMs), ActorId, P2B5SiteScopeDecision.Global);
        public void Execute(string sql, string? id = null)
        {
            using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
            if (id is not null) command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        private SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "catalog.db"), Pooling = false, ForeignKeys = true }.ToString());
            db.Open(); return db;
        }
        // Retain this one minimal SQLite fixture according to the Workspace rule.
        public void Dispose() => SqliteConnection.ClearAllPools();
    }

    private static async Task<NetworkDiagnosticRunSnapshot> Terminal(NetworkDiagnosticsService service, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var run = service.Get(id, P2B5SiteScopeDecision.Global)!;
            if (run.ProgressPercent == 100 && run.State is "SUCCEEDED" or "PARTIAL" or "FAILED" or "CANCELLED") return run;
            await Task.Delay(10, timeout.Token);
        }
    }
    private static void ExpectDenied(Action action)
    { try { action(); throw new InvalidOperationException("scope denial expected"); } catch (DeviceSiteScopeException) { } }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
