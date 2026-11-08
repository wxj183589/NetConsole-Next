using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

// Windows tracert uses ICMP. It runs under the Service's ordinary token;
// host restrictions must remain explicit failures rather than fabricated hops.
internal static class WindowsSystemTraceroute
{
    internal static ProcessStartInfo Command(IPAddress destination, int timeoutMs)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = OperatingSystem.IsWindows() ? Encoding.GetEncoding((int)GetOEMCP()) : Encoding.UTF8;
        var command = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tracert.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = encoding, StandardErrorEncoding = encoding };
        foreach (var arg in new[] { "-d", destination.AddressFamily == AddressFamily.InterNetwork ? "-4" : "-6",
                     "-h", NetworkTraceroute.MaximumHops.ToString(CultureInfo.InvariantCulture), "-w",
                     Math.Clamp(timeoutMs, 1, NetworkTraceroute.MaximumHopTimeoutMs).ToString(CultureInfo.InvariantCulture), destination.ToString() })
            command.ArgumentList.Add(arg);
        return command;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    internal static Task<NetworkTracerouteSnapshot> RunAsync(IPAddress destination, int timeoutMs,
        Action requireCurrentTarget, Action<NetworkTracerouteSnapshot> progress, CancellationToken token) =>
        RunProcessAsync(Command(destination, timeoutMs), destination, timeoutMs, requireCurrentTarget, progress, token);

    internal static async Task<NetworkTracerouteSnapshot> RunProcessAsync(ProcessStartInfo command, IPAddress destination,
        int timeoutMs, Action requireCurrentTarget, Action<NetworkTracerouteSnapshot> progress, CancellationToken token,
        Action<int>? processStarted = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeoutMs);
        using var process = new Process { StartInfo = command };
        var hops = new List<NetworkDiagnosticHopSnapshot>();
        NetworkTracerouteSnapshot Snapshot() => new(destination.ToString(), hops.LastOrDefault()?.Status == "REACHED",
            NetworkTraceroute.MaximumHops, hops.ToArray(), "WINDOWS_SYSTEM_ICMP_TRACERT");
        Task? stderr = null;
        Task<string?>? pendingLine = null;
        var launched = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            requireCurrentTarget();
            if (!process.Start()) throw new PingException("System ICMP traceroute could not start.");
            launched = true;
            processStarted?.Invoke(process.Id);
            stderr = DrainErrorsAsync(process.StandardError, deadline.Token);
            var characters = 0;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                requireCurrentTarget();
                if (stderr.IsFaulted) await stderr;
                pendingLine ??= ReadLineBoundedAsync(process.StandardOutput, deadline.Token);
                if (await Task.WhenAny(pendingLine, Task.Delay(100, deadline.Token)) != pendingLine) continue;
                var line = await pendingLine;
                pendingLine = null;
                if (line is null) break;
                characters += line.Length;
                if (characters > 65536) throw new PingException("System ICMP traceroute output exceeded its limit.");
                var hop = ParseHop(line, destination);
                if (hop is null) continue;
                if (hop.Ttl != hops.Count + 1) throw new PingException("System ICMP traceroute returned an invalid hop sequence.");
                deadline.Token.ThrowIfCancellationRequested();
                requireCurrentTarget();
                hops.Add(hop);
                progress(Snapshot());
                if (hop.Status is "REACHED" or "UNREACHABLE" || hops.Count == NetworkTraceroute.MaximumHops)
                    return Snapshot();
            }
            await process.WaitForExitAsync(deadline.Token);
            await stderr;
            requireCurrentTarget();
            if (process.ExitCode != 0 || hops.Count < NetworkTraceroute.MaximumHops)
                throw new PingException("System ICMP traceroute ended without a complete path.");
            return Snapshot();
        }
        catch (Win32Exception error)
        {
            throw new PingException("System ICMP traceroute could not start.", error);
        }
        finally
        {
            // Only our child is terminated. Revocation cannot leave a probe running.
            if (launched)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            deadline.Cancel();
            if (pendingLine is not null) try { await pendingLine; } catch (OperationCanceledException) { } catch (PingException) { }
            if (stderr is not null) try { await stderr; } catch (OperationCanceledException) { } catch (PingException) { }
        }
    }

    private static async Task<string?> ReadLineBoundedAsync(StreamReader stream, CancellationToken token)
    {
        var line = new List<byte>();
        var buffer = new byte[1];
        while (await stream.BaseStream.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == 10) return DecodeOutputLine(line.ToArray()).TrimEnd('\r');
            line.Add(buffer[0]);
            if (line.Count > 16384) throw new PingException("System ICMP traceroute line exceeded its limit.");
        }
        return line.Count == 0 ? null : DecodeOutputLine(line.ToArray());
    }

    internal static string DecodeOutputLine(byte[] bytes)
    {
        // Published apphost and dotnet may select different native code pages.
        // ASCII is identical; prefer strictly valid UTF-8, otherwise use OEM.
        string line;
        try { line = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            line = Encoding.GetEncoding((int)GetOEMCP()).GetString(bytes);
        }
        if (line.Length > 4096) throw new PingException("System ICMP traceroute line exceeded its limit.");
        return line;
    }

    private static async Task DrainErrorsAsync(StreamReader stream, CancellationToken token)
    {
        var buffer = new char[1024];
        var total = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), token)) != 0)
            if ((total += count) > 65536) throw new PingException("System ICMP traceroute error output exceeded its limit.");
    }

    internal static NetworkDiagnosticHopSnapshot? ParseHop(string line, IPAddress destination)
    {
        var match = Regex.Match(line, @"^\s*(\d{1,2})\s+(.+)$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var ttl) || ttl is < 1 or > NetworkTraceroute.MaximumHops) return null;
        var body = match.Groups[2].Value.Trim();
        // tracert can exit 0 after an ICMP transmit failure. Preserve the native
        // error so the product can classify code 5 as permission denied.
        var nativeFailure = Regex.Match(body,
            @"^(?:Transmit error\s*:\s*code|传输错误\s*[:：]\s*代码为)\s*(\d{1,5})\s*[.。]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (nativeFailure.Success && int.TryParse(nativeFailure.Groups[1].Value, out var nativeCode))
            throw new PingException("System ICMP traceroute transmit failed.", new Win32Exception(nativeCode));
        // TryParse also accepts integer latency tokens as IPv4; require dot/colon.
        var addressMatch = Regex.Matches(body, @"(?<!\S)[^\s]+").Cast<Match>().LastOrDefault(m =>
            (m.Value.Contains('.') || m.Value.Contains(':')) && IPAddress.TryParse(m.Value, out _));
        IPAddress? address = addressMatch is null ? null : IPAddress.Parse(addressMatch.Value);
        var timings = Regex.Matches(body, @"(?<!\S)(<)?\s*(\d+)\s*(?:ms|毫秒)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var latency = timings.Cast<Match>().Select(m => long.TryParse(m.Groups[2].Value, out var value) ? (long?)(m.Groups[1].Success ? 0 : value) : null)
            .Where(value => value is not null).DefaultIfEmpty().Min();
        var cleanReply = addressMatch is not null && addressMatch.Index + addressMatch.Length == body.Length && timings.Count > 0;
        var unreachable = Regex.IsMatch(body, "unreachable|无法访问|不可达", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var status = unreachable ? "UNREACHABLE" : cleanReply && destination.Equals(address) ? "REACHED" :
            cleanReply ? "TTL_EXPIRED" : address is null && body.Count(ch => ch == '*') == 3 ? "TIMED_OUT" : "OTHER";
        return new(ttl, address?.ToString(), status, latency);
    }
}
