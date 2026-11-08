using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetConsole.Next.Service;

internal sealed record NetworkDiagnosticHopSnapshot(int Ttl, string? Address, string Status, long? LatencyMs);

internal sealed record NetworkTracerouteSnapshot(string DestinationAddress, bool DestinationReached, int MaxHops,
    IReadOnlyList<NetworkDiagnosticHopSnapshot> Hops, string ProbeKind = "DOTNET_ICMP");

internal sealed record NetworkTracerouteProbeReply(IPStatus Status, IPAddress? Address, long? LatencyMs);

// A small probe seam keeps TTL/deadline tests independent of the surrounding network.
internal interface INetworkTracerouteProbe
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
    Task<NetworkTracerouteProbeReply> SendAsync(IPAddress address, int ttl, int timeoutMs,
        CancellationToken cancellationToken);
}

internal sealed class IcmpNetworkTracerouteProbe : INetworkTracerouteProbe
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    public async Task<NetworkTracerouteProbeReply> SendAsync(IPAddress address, int ttl, int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var started = Stopwatch.GetTimestamp();
        var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), new byte[32],
            new PingOptions(ttl, false), cancellationToken);
        // A timed-out Ping can carry 0.0.0.0; it is not an observed router.
        var observed = reply.Status == IPStatus.TimedOut || reply.Address is null ||
            reply.Address.Equals(IPAddress.Any) || reply.Address.Equals(IPAddress.IPv6Any) ? null : reply.Address;
        return new(reply.Status, observed, reply.Status == IPStatus.TimedOut ? null :
            reply.Status == IPStatus.Success ? reply.RoundtripTime : (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}

internal static class NetworkTraceroute
{
    public const int MaximumHops = 30;
    public const int MaximumHopTimeoutMs = 1_000;

    public static async Task<NetworkTracerouteSnapshot> RunAsync(string host, int timeoutMs,
        INetworkTracerouteProbe probe, Action requireCurrentTarget,
        Action<NetworkTracerouteSnapshot> progress, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        requireCurrentTarget();
        var addresses = await probe.ResolveAsync(host, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        requireCurrentTarget();
        // Resolve once: later DNS changes cannot redirect an admitted trace midway.
        var destination = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6)
            ?? throw new SocketException((int)SocketError.HostNotFound);
        var hops = new List<NetworkDiagnosticHopSnapshot>(MaximumHops);
        var systemTrace = OperatingSystem.IsWindows() && probe is IcmpNetworkTracerouteProbe;
        NetworkTracerouteSnapshot Snapshot(bool reached = false) => new(destination.ToString(), reached,
            MaximumHops, hops.ToArray(), systemTrace ? "WINDOWS_SYSTEM_ICMP_TRACERT" : "DOTNET_ICMP");
        progress(Snapshot());
        if (systemTrace)
        {
            var remaining = timeoutMs - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining <= 0) throw new OperationCanceledException(cancellationToken);
            return await WindowsSystemTraceroute.RunAsync(destination, remaining, requireCurrentTarget, progress, cancellationToken);
        }
        for (var ttl = 1; ttl <= MaximumHops; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requireCurrentTarget();
            var remaining = timeoutMs - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining <= 0) throw new OperationCanceledException(cancellationToken);
            var reply = await probe.SendAsync(destination, ttl, Math.Min(MaximumHopTimeoutMs, remaining), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            requireCurrentTarget();
            var reached = reply.Status == IPStatus.Success && destination.Equals(reply.Address);
            var status = reached ? "REACHED" : reply.Status switch
            {
                IPStatus.TtlExpired or IPStatus.TimeExceeded => "TTL_EXPIRED",
                IPStatus.TimedOut => "TIMED_OUT",
                IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationHostUnreachable or
                    IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationPortUnreachable or
                    IPStatus.DestinationUnreachable or IPStatus.BadRoute => "UNREACHABLE",
                _ => "OTHER"
            };
            hops.Add(new(ttl, reply.Status == IPStatus.TimedOut ? null : reply.Address?.ToString(), status,
                reply.LatencyMs is >= 0 ? reply.LatencyMs : null));
            var snapshot = Snapshot(reached);
            progress(snapshot);
            if (reached || status == "UNREACHABLE") return snapshot;
        }
        return Snapshot();
    }
}
