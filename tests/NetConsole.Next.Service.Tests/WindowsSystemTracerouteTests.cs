using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using NetConsole.Next.Service;

internal static class WindowsSystemTracerouteTests
{
    internal static async Task RunAsync()
    {
        var destination = IPAddress.Parse("192.0.2.9");
        const string localized = " 1  <1 毫秒  1 毫秒  2 毫秒  192.0.2.9";
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        foreach (var encoding in new[] { System.Text.Encoding.UTF8, System.Text.Encoding.GetEncoding(936) })
            Check(WindowsSystemTraceroute.ParseHop(WindowsSystemTraceroute.DecodeOutputLine(encoding.GetBytes(localized)), destination)?.Status == "REACHED", "UTF8 and OEM native output");
        Check(WindowsSystemTraceroute.ParseHop(" 1  <1 ms  1 ms  2 ms  192.0.2.9", destination) is
            { Status: "REACHED", LatencyMs: 0, Address: "192.0.2.9" }, "English reached hop");
        Check(WindowsSystemTraceroute.ParseHop(" 2  2 毫秒  3 毫秒  4 毫秒  192.0.2.1", destination) is
            { Status: "TTL_EXPIRED", LatencyMs: 2 }, "localized timing");
        Check(WindowsSystemTraceroute.ParseHop(" 3  * * * 请求超时。", destination) is
            { Status: "TIMED_OUT", Address: null, LatencyMs: null }, "timeout cannot invent a router from numbers");
        Check(WindowsSystemTraceroute.ParseHop(" 4  2 ms  3 ms 4 ms 192.0.2.9 reports: Destination host unreachable.", destination) is
            { Status: "UNREACHABLE" }, "unreachable address is not arrival");
        Check(WindowsSystemTraceroute.ParseHop(" 4  192.0.2.9 报告: 无法访问目标主机。", destination)?.Status == "UNREACHABLE", "localized unreachable");
        Check(WindowsSystemTraceroute.ParseHop(" 1  1 ms 1 ms 1 ms ::1", IPAddress.IPv6Loopback)?.Status == "REACHED", "IPv6");
        Check(WindowsSystemTraceroute.ParseHop(" 1  1 ms 2 ms 3 ms", destination) is { Address: null, Status: "OTHER" }, "latencies are not IP addresses");
        Check(WindowsSystemTraceroute.ParseHop(" 31 1 ms 192.0.2.9", destination) is null &&
            WindowsSystemTraceroute.ParseHop("Tracing route to 192.0.2.9", destination) is null, "headers and invalid TTL ignored");
        var command = WindowsSystemTraceroute.Command(destination, 30000);
        Check(Path.IsPathFullyQualified(command.FileName) && !command.UseShellExecute && command.CreateNoWindow &&
            command.ArgumentList.SequenceEqual(new[] { "-d", "-4", "-h", "30", "-w", "1000", "192.0.2.9" }), "absolute executable, bounded arguments, no shell");
        if (OperatingSystem.IsWindows())
        {
            await RejectsNativeFailure();
            await OwnedProcessStops("cancel");
            await OwnedProcessStops("deadline");
            await OwnedProcessStops("revoke");
            await RejectsMalformedOutput();
            await RejectsStderrOverflow();
            await TerminalHopStopsOwnedProcess();
            await StderrFaultAfterTerminalHop("reached");
            await StderrFaultAfterTerminalHop("unreachable");
            await StderrFaultAfterTerminalHop("cancel");
            await StderrFaultAfterTerminalHop("revoke");
        }
        Console.WriteLine("WINDOWS_SYSTEM_TRACEROUTE_TESTS=parser/command PASS; owned process cancellation/deadline/revocation/output PASS (Windows only)");
    }

    private static ProcessStartInfo Fixture(string script)
    {
        var command = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script }) command.ArgumentList.Add(argument);
        return command;
    }

    private static async Task RejectsNativeFailure()
    {
        foreach (var message in new[] { " 1 Transmit error: code 5.", " 1 传输错误: 代码为5。" })
        {
            var pid = 0;
            var published = 0;
            try
            {
                await WindowsSystemTraceroute.RunProcessAsync(Fixture("[Console]::OutputEncoding = [Text.Encoding]::UTF8; [Console]::WriteLine('" + message + "')"),
                    IPAddress.Loopback, 5000, () => { }, _ => published++, CancellationToken.None, value => pid = value);
                throw new Exception("native error was accepted as a hop");
            }
            catch (PingException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }) { }
            Check(pid > 0 && Exited(pid) && published == 0, "zero-exit native denial must be classified without publishing a hop");
        }
    }

    private static async Task OwnedProcessStops(string kind)
    {
        var pid = 0;
        var checks = 0;
        using var cancel = new CancellationTokenSource();
        var started = Stopwatch.GetTimestamp();
        try
        {
            await WindowsSystemTraceroute.RunProcessAsync(Fixture("[Console]::WriteLine(' 1 1 ms 1 ms 1 ms 192.0.2.1'); Start-Sleep -Seconds 30"),
                IPAddress.Parse("192.0.2.9"), kind == "deadline" ? 500 : 5000,
                () => { if (kind == "revoke" && ++checks > 3) throw new InvalidOperationException("revoked"); }, _ => { }, cancel.Token,
                processId => { pid = processId; if (kind == "cancel") cancel.CancelAfter(150); });
            throw new Exception("fixture should stop");
        }
        catch (OperationCanceledException) when (kind is "cancel" or "deadline") { }
        catch (InvalidOperationException error) when (kind == "revoke" && error.Message == "revoked") { }
        Check(pid > 0 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5) && Exited(pid), "owned process must be reaped on " + kind);
    }

    private static async Task RejectsMalformedOutput()
    {
        var pid = 0;
        try
        {
            await WindowsSystemTraceroute.RunProcessAsync(Fixture("[Console]::WriteLine(('x' * 5000)); Start-Sleep -Seconds 30"),
                IPAddress.Loopback, 5000, () => { }, _ => { }, CancellationToken.None, value => pid = value);
            throw new Exception("oversized line accepted");
        }
        catch (PingException) { }
        Check(pid > 0 && Exited(pid), "output rejection must reap owned child");
    }
    private static bool Exited(int pid) { try { using var process = Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; } }
    private static async Task RejectsStderrOverflow()
    {
        var pid = 0;
        var started = Stopwatch.GetTimestamp();
        try
        {
            await WindowsSystemTraceroute.RunProcessAsync(Fixture("[Console]::Error.Write(('x' * 70000)); [Console]::Error.Flush(); Start-Sleep -Seconds 30"),
                IPAddress.Loopback, 5000, () => { }, _ => throw new Exception("stderr must never publish a hop"), CancellationToken.None, value => pid = value);
            throw new Exception("stderr overflow accepted");
        }
        catch (PingException) { }
        Check(pid > 0 && Exited(pid) && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5), "stderr overflow must stop a child with silent stdout");
    }
    private static async Task TerminalHopStopsOwnedProcess()
    {
        var pid = 0;
        var started = Stopwatch.GetTimestamp();
        var result = await WindowsSystemTraceroute.RunProcessAsync(Fixture(
            "[Console]::WriteLine(' 1 1 ms 1 ms 1 ms 127.0.0.1'); [Console]::Out.Flush(); Start-Sleep -Seconds 30"),
            IPAddress.Loopback, 5000, () => { }, _ => { }, CancellationToken.None, value => pid = value);
        Check(result.DestinationReached && pid > 0 && Exited(pid) && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5),
            "a clean terminal hop must still actively stop and reap its child");
    }

    private static async Task StderrFaultAfterTerminalHop(string kind)
    {
        // Gate stderr until progress has received the terminal stdout hop. The child
        // then exits after writing exactly one character beyond the stderr limit,
        // before progress returns. A larger write could block after the drain faults.
        // This deterministically exercises the early-return cleanup path, without sleeps.
        var eventName = "Local\\NetConsoleTraceroute-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var cancel = new CancellationTokenSource();
        var originalRevocation = new InvalidOperationException("original target revocation");
        var pid = 0;
        var published = 0;
        var hop = kind == "unreachable" ? " 1 127.0.0.1 reports: Destination host unreachable." : " 1 1 ms 1 ms 1 ms 127.0.0.1";
        var script = "[Console]::WriteLine('" + hop + "'); [Console]::Out.Flush(); " +
            "$event=[Threading.EventWaitHandle]::OpenExisting('" + eventName + "'); " +
            "if (-not $event.WaitOne(5000)) { exit 20 }; " +
            "[Console]::Error.Write(('x' * 65537)); [Console]::Error.Flush(); $event.Dispose()";
        try
        {
            await WindowsSystemTraceroute.RunProcessAsync(Fixture(script), IPAddress.Loopback, 10000, () => { }, _ =>
            {
                published++;
                release.Set();
                using var child = Process.GetProcessById(pid);
                Check(child.WaitForExit(5000), "stderr fixture must finish writing before terminal progress returns");
                if (kind == "cancel") { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); }
                if (kind == "revoke") throw originalRevocation;
            }, cancel.Token, value => pid = value);
            throw new Exception("terminal hop hid a completed stderr overflow: " + kind);
        }
        catch (PingException error) when (kind is "reached" or "unreachable")
        {
            Check(error.Message.Contains("error output exceeded", StringComparison.Ordinal), "terminal hop must propagate the actual stderr limit failure");
        }
        catch (OperationCanceledException error) when (kind == "cancel")
        {
            Check(error.CancellationToken == cancel.Token, "stderr cleanup must preserve the original cancellation token");
        }
        catch (InvalidOperationException error) when (kind == "revoke")
        {
            Check(ReferenceEquals(error, originalRevocation), "stderr cleanup must preserve the original revocation exception");
        }
        Check(pid > 0 && Exited(pid) && published == 1, "terminal stderr failure must reap its owned child: " + kind);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
