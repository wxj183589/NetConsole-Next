using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NetConsole.Next.LocalManager;

internal static class PortableRuntimeTests
{
    internal static async Task RunAsync(string fixture)
    {
        static void Check(bool result, string message) { if (!result) throw new Exception(message); }
        static async Task Rejected(Func<Task> run, string message)
        {
            try { await run(); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or JsonException or ArgumentException) { return; }
            throw new Exception(message);
        }
        var runtimeRoot = Path.Combine(fixture, "portable-new");
        var executable = Environment.ProcessPath!;
        var settings = new LocalManagerSettings(executable, runtimeRoot, 18779, false, false);
        var runtime = new PortableRuntime(settings);
        Check((await runtime.StatusAsync()).State == "STOPPED" && (await runtime.StopAsync()).State == "STOPPED" &&
            !Directory.Exists(runtimeRoot), "status/stop must not initialize a missing root");
        var foreign = Path.Combine(fixture, "foreign-root");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "unrelated");
        await Rejected(() => new PortableRuntime(settings with { DataRoot = foreign }).StartAsync(),
            "nonempty unmarked root must fail before process creation");
        Check(Directory.GetFiles(foreign).Length == 1 && File.ReadAllText(Path.Combine(foreign, "keep.txt")) == "unrelated",
            "foreign root contents must be preserved");
        Check(LocalStorageDiagnostics.Run(foreign, _ => throw new Exception("Foreign-root capacity callback must not run")).State == "FAILED",
            "storage/log diagnosis must reject a foreign unmarked root before capacity or log access");
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        try
        {
            await Rejected(() => new PortableRuntime(settings with { WebPort = port }).StartAsync(), "port conflict must prevent spawn");
            Check(File.ReadAllText(Path.Combine(runtimeRoot, ".netconsole-next-root")) == "NETCONSOLE_NEXT_V1" &&
                !File.Exists(Path.Combine(runtimeRoot, ".local-manager-runtime.json")), "first launch marker precedes manager state; no receipt on port conflict");
            Check(occupied.Server.IsBound, "unrelated port owner must remain running");
        }
        finally { occupied.Stop(); }
        var receiptPath = Path.Combine(runtimeRoot, ".local-manager-runtime.json");
        File.WriteAllText(receiptPath, "{\"processId\":123,\"startUtcTicks\":1,\"executable\":null,\"pipeName\":null,\"webPort\":18779}");
        await Rejected(() => runtime.StopAsync(), "corrupt receipt must fail closed without null dereference or process control");
        using var own = Process.GetCurrentProcess();
        var mismatch = new PortableRuntimeReceipt(own.Id, own.StartTime.ToUniversalTime().Ticks + 1, executable,
            "NetConsoleNext-" + new string('A', 40), settings.WebPort);
        File.WriteAllBytes(receiptPath, JsonSerializer.SerializeToUtf8Bytes(mismatch, PortableRuntime.JsonOptions));
        await Rejected(() => runtime.StopAsync(), "PID reuse/start-time mismatch must prevent process control");
        Check(!own.HasExited, "identity mismatch never stops current test process");
        File.Delete(receiptPath);
        var logs = Path.Combine(runtimeRoot, "logs");
        Directory.CreateDirectory(logs);
        var log = Path.Combine(logs, "service-20261108.log");
        File.WriteAllText(log, "unchanged");
        var ample = LocalStorageDiagnostics.Run(runtimeRoot, _ => (20L << 30, 100L << 30));
        Check(ample.State == "PASS" && ample.InspectedLogFiles == 1, "normal capacity and bounded log set passes");
        var low = LocalStorageDiagnostics.Run(runtimeRoot, _ => (512L << 20, 100L << 30));
        Check(low.State == "WARNING" && File.ReadAllText(log) == "unchanged", "low capacity warns without deleting or rewriting data");
        for (var i = 0; i < 15; i++) File.WriteAllText(Path.Combine(logs, $"service-extra-{i}.log"), "x");
        var over = LocalStorageDiagnostics.Run(runtimeRoot, _ => (20L << 30, 100L << 30));
        Check(over.State == "WARNING" && over.InspectedLogFiles == 15 && Directory.GetFiles(logs).Length == 16,
            "retention inspection is bounded to 15 files and does not prune");
        var config = Path.Combine(fixture, "settings.json");
        File.WriteAllBytes(config, JsonSerializer.SerializeToUtf8Bytes(settings, PortableRuntime.JsonOptions));
        Check(LocalManagerSettings.Load(config).DataRoot == runtimeRoot, "explicit portable config is validated and reusable");
        File.WriteAllBytes(config, JsonSerializer.SerializeToUtf8Bytes(settings with { DataRoot = Path.GetDirectoryName(executable)! }, PortableRuntime.JsonOptions));
        await Rejected(() => Task.FromResult(LocalManagerSettings.Load(config)), "program/data overlap must be rejected");
        Console.WriteLine("PORTABLE_RUNTIME_BOUNDARY_TESTS=9/9 PASS: missing roots, foreign roots, occupied port, marker init, corrupt receipt, PID identity, capacity warning, bounded logs, settings boundaries");
    }
}
