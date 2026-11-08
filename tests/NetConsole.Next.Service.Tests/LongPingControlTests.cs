using System.Collections.Concurrent;
using System.Net;
using NetConsole.Next.Service;

internal static class LongPingControlTests
{
    public static async Task RunAsync()
    {
        var control = new LongPingControl();
        var states = new ConcurrentQueue<string>();
        var probe = new BlockingProbe();
        var samples = new ConcurrentQueue<QuickPingSnapshot>();
        using var stop = new CancellationTokenSource(5_000);
        var run = NetworkQuickPing.RunForDurationAsync("fixture", 22, "TCP", 4, 100, 2_000, probe,
            () => { }, samples.Enqueue, stop.Token, control, states.Enqueue);
        await probe.FirstStarted.Task.WaitAsync(stop.Token);
        Assert(control.SetPaused(true, states.Enqueue), "pause requested");
        Assert(states.Last() == "PAUSING", "inflight probe must drain before acknowledgement");
        probe.FirstReply.TrySetResult();
        await Until(() => states.Contains("PAUSED"));
        var frozen = samples.Last();
        await Task.Delay(250);
        Assert(probe.Calls == 1 && samples.Last() == frozen && frozen is { Sent: 1, Lost: 0 },
            "pause keeps actual reply and sends no probes or invented failures");
        Assert(!control.SetPaused(true, states.Enqueue), "repeated pause is idempotent");
        control.SetPaused(false, states.Enqueue);
        await Until(() => samples.Last().Received >= 2);
        Assert(samples.Last().Received >= 2, "resume admits actual next probe");
        stop.Cancel();
        try { await run; throw new Exception("cancel expected"); } catch (OperationCanceledException) { }

        control = new LongPingControl();
        control.SetPaused(true, _ => { });
        probe = new BlockingProbe();
        var ended = await NetworkQuickPing.RunForDurationAsync("fixture", 22, "TCP", 1, 1000, 100, probe,
            () => { }, _ => { }, CancellationToken.None, control);
        Assert(probe.Calls == 0 && ended is { Sent: 0, Lost: 0, LossPercent: null },
            "initial pause expires inside original window without samples or fabricated loss");

        var revoked = false;
        var wait = control.WaitToProbeAsync(() => TimeSpan.FromSeconds(3),
            () => { if (revoked) throw new DeviceSiteScopeException("REVOKED", "fixture"); }, _ => { }, CancellationToken.None);
        revoked = true;
        try { await wait; throw new Exception("revocation expected"); } catch (DeviceSiteScopeException) { }
        Console.WriteLine("LONG_PING_CONTROL_TESTS=3/3 PASS (drain/pause/resume/cancel, bounded empty window, paused authority revocation)");
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(3_000);
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private sealed class BlockingProbe : IQuickPingProbe
    {
        public int Calls;
        public TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken token) => Task.FromResult(new[] { IPAddress.Loopback });
        public async Task<(string Status, long? LatencyMs)> SendAsync(IPAddress address, int port, string protocol, int timeoutMs, CancellationToken token)
        {
            if (Interlocked.Increment(ref Calls) == 1) { FirstStarted.TrySetResult(); await FirstReply.Task.WaitAsync(token); }
            return ("REPLIED", 2);
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
