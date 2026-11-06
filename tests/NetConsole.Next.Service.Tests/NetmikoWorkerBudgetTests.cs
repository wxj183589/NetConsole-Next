using System.Diagnostics;
using System.Text.Json;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class NetmikoWorkerBudgetTests
{
    private const string WorkerMode = "NETCONSOLE_NEXT_TEST_BUDGET_WORKER";
    private const string Key = "SHA256:ERERERERERERERERERERERERERERERERERERERERERE";

    internal static bool IsWorker => Environment.GetEnvironmentVariable(WorkerMode) == "1";

    internal static async Task RunWorkerAsync()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var host = request.RootElement.GetProperty("host").GetString();
            if (host == "silent") { await Task.Delay(Timeout.Infinite); return; }
            if (host == "exit") return;
            if (host == "fail")
                Console.WriteLine("{\"ok\":false,\"error\":\"NetmikoAuthenticationException\"}");
            else
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true,
                    result = new { driver = "synthetic", host_key_type = "ssh-ed25519", host_key_sha256 = Key } }));
            await Console.Out.FlushAsync();
            if (host == "response-exit") return;
        }
    }

    public static async Task RunAsync()
    {
        var names = new[] { WorkerMode, "NETCONSOLE_NEXT_NETMIKO_PYTHON", "NETCONSOLE_NEXT_NETMIKO_WORKER" };
        var before = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            var entry = typeof(NetmikoWorkerBudgetTests).Assembly.Location;
            var executable = Environment.ProcessPath!;
            // The production transport supplies exactly one script argument.
            // When tests run as an apphost, use dotnet to execute this DLL.
            if (!Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                executable = "dotnet";
            Environment.SetEnvironmentVariable(WorkerMode, "1");
            Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_PYTHON", executable);
            Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_WORKER", entry);
            await SharedLimitAndCancelledWaiters();
            await DisposedWaiterCannotStart();
            await LiveWorkerRetainsSlotUntilExit();
            await StartupFailuresReturnSlots(executable);
            await ConnectionFailureAndCancellationReturnSlots();
            await ProbeCompletionAndFailureReturnSlots();
            Console.WriteLine("NETMIKO_WORKER_BUDGET_TESTS=PASS (6 groups, shared limit 16)");
        }
        finally
        {
            foreach (var pair in before) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static H3cDeviceProfileSnapshot Profile(string host = "success") =>
        new("a1111111111111111111111111111111", host, 22, "reader", "synthetic", Key, true, 0, 0);

    private static SiteSshJumpHostSnapshot Jump(string host = "success") =>
        new("local", host, 22, "reader", "SYNTHETIC", "synthetic", "", true, 0);

    private static async Task<List<NetmikoWorkerBudget.Lease>> FillAsync(int count = NetmikoWorkerBudget.Limit)
    {
        var result = new List<NetmikoWorkerBudget.Lease>();
        for (var index = 0; index < count; index++)
            result.Add(await NetmikoWorkerBudget.AcquireAsync(CancellationToken.None));
        return result;
    }

    private static void Release(IEnumerable<NetmikoWorkerBudget.Lease> slots)
    {
        foreach (var slot in slots) { slot.Dispose(); slot.Dispose(); }
    }

    private static async Task SharedLimitAndCancelledWaiters()
    {
        var held = await FillAsync();
        try
        {
            using var cancelled = new CancellationTokenSource();
            using var transport = new NetmikoWorkerH3cSshTransport(Profile(), "synthetic");
            var connection = transport.ConnectAsync((_, _) => true, cancelled.Token);
            var probe = new NetmikoJumpHostProbe().ProbeAsync(Jump(), cancelled.Token);
            Assert(!connection.IsCompleted && !probe.IsCompleted && NetmikoWorkerBudget.AvailableSlots == 0,
                "collection/connection and Jump probe must share the saturated process limit");
            cancelled.Cancel();
            await Cancelled(connection);
            await Cancelled(probe);
            Assert(NetmikoWorkerBudget.AvailableSlots == 0, "cancelled waiters must not release slots they never acquired");
        }
        finally { Release(held); }
        await ExpectAllSlots();
    }

    private static async Task DisposedWaiterCannotStart()
    {
        var held = await FillAsync();
        try
        {
            using var transport = new NetmikoWorkerH3cSshTransport(Profile(), "synthetic");
            var connection = transport.ConnectAsync((_, _) => true, CancellationToken.None);
            Assert(!connection.IsCompleted, "a transport must wait before creating its worker");
            transport.Dispose();
            await Cancelled(connection);
            Assert(NetmikoWorkerBudget.AvailableSlots == 0, "disposing a waiter must leave held slots untouched");
        }
        finally { Release(held); }
        await ExpectAllSlots();
    }

    private static async Task LiveWorkerRetainsSlotUntilExit()
    {
        var held = await FillAsync(NetmikoWorkerBudget.Limit - 1);
        try
        {
            using var transport = new NetmikoWorkerH3cSshTransport(Profile(), "synthetic");
            await transport.ConnectAsync((_, _) => true, CancellationToken.None);
            Assert(transport.IsConnected && NetmikoWorkerBudget.AvailableSlots == 0,
                "a connected process must retain its slot while idle");
            var repeated = false;
            try { await transport.ConnectAsync((_, _) => true, CancellationToken.None); }
            catch (InvalidOperationException) { repeated = true; }
            Assert(repeated && transport.IsConnected, "one transport must never replace its live worker with another");
            using var waitingCancellation = new CancellationTokenSource();
            var next = NetmikoWorkerBudget.AcquireAsync(waitingCancellation.Token);
            Assert(!next.IsCompleted, "a live worker must prevent the seventeenth admission");
            transport.Abort();
            using var admitted = await next.WaitAsync(TimeSpan.FromSeconds(10));
            transport.Abort(); transport.Dispose(); transport.Dispose();
            Assert(NetmikoWorkerBudget.AvailableSlots == 0, "repeated cleanup must not release the newly admitted owner's slot");
        }
        finally { Release(held); }
        await ExpectAllSlots();
    }

    private static async Task StartupFailuresReturnSlots(string executable)
    {
        Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_PYTHON",
            Path.Combine(AppContext.BaseDirectory, "missing-budget-worker-executable"));
        try
        {
            using var transport = new NetmikoWorkerH3cSshTransport(Profile(), "synthetic");
            var failed = false;
            try { await transport.ConnectAsync((_, _) => true, CancellationToken.None); }
            catch (System.ComponentModel.Win32Exception) { failed = true; }
            Assert(failed, "transport must preserve a process startup failure");
            try { await new NetmikoJumpHostProbe().ProbeAsync(Jump(), CancellationToken.None); }
            catch (NetmikoJumpHostProbeException error) when (error.Code == "JUMP_HOST_PROBE_FAILED") { }
            await ExpectAllSlots();
        }
        finally { Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_PYTHON", executable); }
    }

    private static async Task ConnectionFailureAndCancellationReturnSlots()
    {
        foreach (var host in new[] { "fail", "exit", "silent" })
        {
            using var transport = new NetmikoWorkerH3cSshTransport(Profile(host), "synthetic");
            using var cancelled = new CancellationTokenSource();
            var connection = transport.ConnectAsync((_, _) => true, cancelled.Token);
            if (host == "silent") { cancelled.Cancel(); await Cancelled(connection); }
            else
            {
                var failed = false;
                try { await connection; }
                catch (H3cAuthenticationFailedException) when (host == "fail") { failed = true; }
                catch (H3cConnectionLostException) when (host == "exit") { failed = true; }
                Assert(failed, "a failed worker must not report a connection");
            }
            transport.Abort(); transport.Dispose();
            await ExpectAllSlots();
        }
        using var rapid = new NetmikoWorkerH3cSshTransport(Profile("response-exit"), "synthetic");
        await rapid.ConnectAsync((_, _) => true, CancellationToken.None);
        Assert(rapid.Driver == "synthetic", "an immediate worker exit must preserve its already written response");
        await ExpectAllSlots();
    }

    private static async Task ProbeCompletionAndFailureReturnSlots()
    {
        var probe = new NetmikoJumpHostProbe();
        var result = await probe.ProbeAsync(Jump(), CancellationToken.None);
        Assert(result.HostKeySha256 == Key, "a successful probe must return its synthetic authenticated key");
        await ExpectAllSlots();
        var failed = false;
        try { await probe.ProbeAsync(Jump("fail"), CancellationToken.None); }
        catch (NetmikoJumpHostProbeException error) when (error.Code == "JUMP_HOST_AUTHENTICATION_FAILED") { failed = true; }
        Assert(failed, "probe authentication failure must retain its category");
        await ExpectAllSlots();
        using var cancelled = new CancellationTokenSource();
        var pending = probe.ProbeAsync(Jump("silent"), cancelled.Token);
        cancelled.Cancel();
        await Cancelled(pending);
        await ExpectAllSlots();
    }

    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("The cancelled worker request must stop.");
    }

    private static async Task ExpectAllSlots()
    {
        var timer = Stopwatch.StartNew();
        while (NetmikoWorkerBudget.AvailableSlots != NetmikoWorkerBudget.Limit && timer.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10);
        Assert(NetmikoWorkerBudget.AvailableSlots == NetmikoWorkerBudget.Limit, "every worker process slot must return after exit");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
