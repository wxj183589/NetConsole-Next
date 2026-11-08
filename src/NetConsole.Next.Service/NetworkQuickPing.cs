using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetConsole.Next.Service;

internal sealed record QuickPingSample(int Sequence, long CapturedMs, string Status, long? LatencyMs);
internal sealed record QuickPingSnapshot(string Protocol, string DestinationAddress, int? Port,
    int RequestedSamples, int Sent, int Received, int Lost, double? LossPercent,
    long? MinMs, long? MaxMs, double? AverageMs, IReadOnlyList<QuickPingSample> Samples)
{
    // Nearest-rank P95 uses replies only; deriving it also supports persisted snapshots from before this field existed.
    public long? P95Ms
    {
        get
        {
            var replied = Samples.Where(sample => sample.Status == "REPLIED" && sample.LatencyMs is >= 0)
                .Select(sample => sample.LatencyMs!.Value).Order().ToArray();
            return replied.Length == 0 ? null : replied[(int)Math.Ceiling(replied.Length * .95) - 1];
        }
    }
}

internal interface IQuickPingProbe
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken token);
    Task<(string Status, long? LatencyMs)> SendAsync(IPAddress address, int port, string protocol,
        int timeoutMs, CancellationToken token);
}

internal sealed class QuickPingProbe : IQuickPingProbe
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken token) => Dns.GetHostAddressesAsync(host, token);
    public async Task<(string Status, long? LatencyMs)> SendAsync(IPAddress address, int port, string protocol,
        int timeoutMs, CancellationToken token)
    {
        if (protocol == "TCP")
        {
            using var client = new TcpClient(address.AddressFamily);
            var started = Stopwatch.GetTimestamp();
            await client.ConnectAsync(address, port, token);
            return ("REPLIED", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), new byte[32], new PingOptions(), token);
        return reply.Status == IPStatus.Success && address.Equals(reply.Address)
            ? ("REPLIED", reply.RoundtripTime) : (reply.Status == IPStatus.TimedOut ? "TIMED_OUT" : "UNREACHABLE", null);
    }
}

internal static class NetworkQuickPing
{
    public const int MaximumLongPingDurationSeconds = 3_600;

    public static void Validate(int samples, int intervalMs, int timeoutMs, string protocol, int devices)
    {
        if (samples is < 2 or > 60 || intervalMs is < 100 or > 5_000 || timeoutMs is < 100 or > 5_000 ||
            protocol is not ("ICMP" or "TCP") || devices > 8 ||
            (long)samples * timeoutMs + (long)(samples - 1) * intervalMs > 120_000)
            throw new ArgumentException("Quick Ping: 2-60 samples, 100-5000 ms interval/timeout, ICMP or TCP, at most 8 devices and 120 seconds per device.");
    }

    public static void ValidateLongPing(int durationSeconds, int intervalMs, int timeoutMs, string protocol, int devices)
    {
        if (durationSeconds is < 10 or > MaximumLongPingDurationSeconds || intervalMs is < 1_000 or > 5_000 ||
            timeoutMs is < 100 or > 5_000 || protocol is not ("ICMP" or "TCP") || devices > 8)
            throw new ArgumentException("Long Ping: 10-3600 seconds, 1000-5000 ms interval, 100-5000 ms timeout, ICMP or TCP, at most 8 devices.");
    }

    public static async Task<QuickPingSnapshot> RunAsync(string host, int port, string protocol, int count,
        int intervalMs, int timeoutMs, IQuickPingProbe probe, Action requireCurrentTarget,
        Action<QuickPingSnapshot> progress, CancellationToken token)
    {
        requireCurrentTarget();
        using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        dnsTimeout.CancelAfter(timeoutMs);
        var addresses = await probe.ResolveAsync(host, dnsTimeout.Token);
        token.ThrowIfCancellationRequested();
        requireCurrentTarget();
        var address = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ??
            addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6) ??
            throw new SocketException((int)SocketError.HostNotFound);
        var samples = new List<QuickPingSample>();
        QuickPingSnapshot Snapshot()
        {
            var latencies = samples.Where(s => s.Status == "REPLIED" && s.LatencyMs.HasValue).Select(s => s.LatencyMs!.Value).ToArray();
            var lost = samples.Count - latencies.Length;
            return new(protocol, address.ToString(), protocol == "TCP" ? port : null, count, samples.Count,
                latencies.Length, lost, samples.Count == 0 ? null : Math.Round(100d * lost / samples.Count, 2),
                latencies.Length == 0 ? null : latencies.Min(), latencies.Length == 0 ? null : latencies.Max(),
                latencies.Length == 0 ? null : Math.Round(latencies.Average(), 2), samples.ToArray());
        }
        progress(Snapshot());
        for (var index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            requireCurrentTarget();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeoutMs);
            (string Status, long? LatencyMs) reply;
            try { reply = await probe.SendAsync(address, port, protocol, timeoutMs, deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { reply = ("TIMED_OUT", null); }
            catch (SocketException) { reply = ("UNREACHABLE", null); }
            token.ThrowIfCancellationRequested();
            requireCurrentTarget();
            samples.Add(new(index, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), reply.Status,
                reply.Status == "REPLIED" && reply.LatencyMs is >= 0 ? reply.LatencyMs : null));
            progress(Snapshot());
            if (index < count) await Task.Delay(intervalMs, token);
        }
        return Snapshot();
    }

    public static async Task<QuickPingSnapshot> RunForDurationAsync(string host, int port, string protocol,
        int durationSeconds, int intervalMs, int timeoutMs, IQuickPingProbe probe, Action requireCurrentTarget,
        Action<QuickPingSnapshot> progress, CancellationToken token, LongPingControl? control = null,
        Action<string>? stateChanged = null)
    {
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(token);
        duration.CancelAfter(TimeSpan.FromSeconds(checked(durationSeconds + 5)));
        requireCurrentTarget();
        using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(duration.Token);
        dnsTimeout.CancelAfter(timeoutMs);
        var addresses = await probe.ResolveAsync(host, dnsTimeout.Token);
        var address = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ??
            addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6) ??
            throw new SocketException((int)SocketError.HostNotFound);
        var samples = new List<QuickPingSample>();
        var sampleLimit = Math.Min(3_600, Math.Max(1, durationSeconds * 1_000 / intervalMs));
        QuickPingSnapshot Snapshot()
        {
            var latencies = samples.Where(s => s.Status == "REPLIED" && s.LatencyMs.HasValue).Select(s => s.LatencyMs!.Value).ToArray();
            var lost = samples.Count - latencies.Length;
            return new(protocol, address.ToString(), protocol == "TCP" ? port : null, sampleLimit, samples.Count,
                latencies.Length, lost, samples.Count == 0 ? null : Math.Round(100d * lost / samples.Count, 2),
                latencies.Length == 0 ? null : latencies.Min(), latencies.Length == 0 ? null : latencies.Max(),
                latencies.Length == 0 ? null : Math.Round(latencies.Average(), 2), samples.ToArray());
        }
        progress(Snapshot());
        var started = Stopwatch.GetTimestamp();
        TimeSpan Remaining() => TimeSpan.FromSeconds(durationSeconds) - Stopwatch.GetElapsedTime(started);
        for (var index = 1; index <= sampleLimit && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(durationSeconds); index++)
        {
            if (control is not null && !await control.WaitToProbeAsync(Remaining, requireCurrentTarget,
                    stateChanged ?? (_ => { }), duration.Token)) break;
            duration.Token.ThrowIfCancellationRequested();
            requireCurrentTarget();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(duration.Token);
            deadline.CancelAfter(timeoutMs);
            (string Status, long? LatencyMs) reply;
            try { reply = await probe.SendAsync(address, port, protocol, timeoutMs, deadline.Token); }
            catch (OperationCanceledException) when (!duration.IsCancellationRequested) { reply = ("TIMED_OUT", null); }
            catch (SocketException) { reply = ("UNREACHABLE", null); }
            duration.Token.ThrowIfCancellationRequested();
            requireCurrentTarget();
            samples.Add(new(index, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), reply.Status,
                reply.Status == "REPLIED" && reply.LatencyMs is >= 0 ? reply.LatencyMs : null));
            progress(Snapshot());
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(durationSeconds)) break;
            var remaining = TimeSpan.FromSeconds(durationSeconds) - Stopwatch.GetElapsedTime(started);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(intervalMs, Math.Max(1, remaining.TotalMilliseconds))), duration.Token);
        }
        return Snapshot();
    }
}
