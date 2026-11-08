using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace NetConsole.Next.LocalManager;

internal sealed record LocalManagerSettings(string ServiceExecutable, string DataRoot, int WebPort = 18771,
    bool EnableH3c = true, bool EnableTerminal = true)
{
    internal static LocalManagerSettings Load(string? path)
    {
        var config = path ?? Path.Combine(AppContext.BaseDirectory, "local-manager.json");
        LocalManagerSettings result;
        if (File.Exists(config))
        {
            ManagedDataRootPath.ResolveExistingDirectory(Path.GetDirectoryName(Path.GetFullPath(config))!);
            if (new FileInfo(config).Length > 16 * 1024 || new FileInfo(config).LinkTarget is not null)
                throw new InvalidDataException("Manager settings are invalid.");
            result = JsonSerializer.Deserialize<LocalManagerSettings>(File.ReadAllBytes(config), PortableRuntime.JsonOptions)
                ?? throw new InvalidDataException("Manager settings are invalid.");
        }
        else if (path is not null) throw new FileNotFoundException("Manager settings file was not found.");
        else result = ReadInstalledSettings() ?? new(Path.Combine(AppContext.BaseDirectory, "NetConsole.Next.Service.exe"),
            Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_DATA_ROOT") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetConsoleNext", "data"),
            int.TryParse(Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WEB_PORT"), out var port) ? port : 18771);
        if (result.WebPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(result.ServiceExecutable) ||
            string.IsNullOrWhiteSpace(result.DataRoot) || !Path.IsPathFullyQualified(result.ServiceExecutable) ||
            !Path.IsPathFullyQualified(result.DataRoot) || result.ServiceExecutable.StartsWith(@"\\", StringComparison.Ordinal) ||
            result.DataRoot.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Absolute local program/data paths and a valid Web port are required.");
        var executable = Path.GetFullPath(result.ServiceExecutable);
        ManagedDataRootPath.ResolveExistingDirectory(Path.GetDirectoryName(executable)!);
        if (!File.Exists(executable) || new FileInfo(executable).LinkTarget is not null ||
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Published service executable was not found.");
        var root = ManagedDataRootPath.Normalize(result.DataRoot).TrimEnd(Path.DirectorySeparatorChar);
        var binaryRoot = Path.GetDirectoryName(executable)!;
        if (root.Equals(binaryRoot, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(binaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            binaryRoot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            root.Equals(Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mutable data must be separate from program files and volume roots.");
        return result with { ServiceExecutable = executable, DataRoot = root };
    }

    private static LocalManagerSettings? ReadInstalledSettings()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NetConsoleNext");
        if (key is null) return null;
        if (key.GetValue("ImagePath") is not string image || image.Length < 3 || image[0] != '"' || image[^1] != '"' ||
            image[1..^1].Contains('"') || key.GetValue("Environment") is not string[] environment)
            throw new InvalidDataException("Installed service has unsupported executable/environment settings; inspect SCM configuration.");
        string? Value(string name)
        {
            var matches = environment.Where(item => item.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("Installed service has duplicate instance settings.");
            return matches.Length == 1 ? matches[0][(name.Length + 1)..] : null;
        }
        var root = Value("NETCONSOLE_NEXT_DATA_ROOT") ?? throw new InvalidDataException("Installed service data root is missing.");
        if (!int.TryParse(Value("NETCONSOLE_NEXT_WEB_PORT"), out var port)) throw new InvalidDataException("Installed service port is invalid.");
        return new(image[1..^1], root, port, Value("NETCONSOLE_NEXT_H3C_ENABLED") == "1", Value("NETCONSOLE_NEXT_TERMINAL_ENABLED") == "1");
    }
}

internal sealed record PortableRuntimeState(string State, int? ProcessId, int WebPort, string Detail);
internal sealed record PortableRuntimeReceipt(int ProcessId, long StartUtcTicks, string Executable, string PipeName, int WebPort);

internal sealed class PortableRuntime(LocalManagerSettings settings)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string ReceiptPath => Path.Combine(settings.DataRoot, ".local-manager-runtime.json");
    private string GatePath => Path.Combine(settings.DataRoot, ".local-manager-runtime.lock");

    internal async Task<PortableRuntimeState> StartAsync()
    {
        EnsureRoot(create: true);
        using var gate = EnterGate();
        using var existing = FindOwnedProcess();
        if (existing is not null)
        {
            await RequireHealthAsync(existing, TimeSpan.FromSeconds(10));
            return new("RUNNING", existing.Id, settings.WebPort, "Existing owned instance is healthy.");
        }
        // Bind test before spawning; never adopt or stop an unrelated port owner.
        var listener = new TcpListener(IPAddress.Loopback, settings.WebPort);
        try { listener.Start(); }
        catch (SocketException) { throw new InvalidOperationException("PORT_IN_USE: choose another Web port or inspect its owner; no process was stopped."); }
        finally { listener.Stop(); }
        var pipe = "NetConsoleNext-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
        var start = new ProcessStartInfo(settings.ServiceExecutable)
        { WorkingDirectory = Path.GetDirectoryName(settings.ServiceExecutable)!, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["NETCONSOLE_NEXT_DATA_ROOT"] = settings.DataRoot;
        start.Environment["NETCONSOLE_NEXT_WEB_PORT"] = settings.WebPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["NETCONSOLE_NEXT_SYSLOG_PORT"] = "0";
        start.Environment["NETCONSOLE_NEXT_H3C_ENABLED"] = settings.EnableH3c ? "1" : "0";
        start.Environment["NETCONSOLE_NEXT_TERMINAL_ENABLED"] = settings.EnableTerminal ? "1" : "0";
        start.Environment["NETCONSOLE_NEXT_LOCAL_CONTROL_PIPE"] = pipe;
        using var process = DetachedWindowsProcess.Start(start);
        var receipt = new PortableRuntimeReceipt(process.Id, process.StartTime.ToUniversalTime().Ticks,
            settings.ServiceExecutable, pipe, settings.WebPort);
        try
        {
            WriteReceipt(receipt);
            await RequireHealthAsync(process, TimeSpan.FromSeconds(40));
            return new("RUNNING", process.Id, settings.WebPort, "Owned interactive service and built Web UI are ready; this is not SCM mode.");
        }
        catch
        {
            // Only this Process object, created above, is eligible for failed-start cleanup.
            if (!process.HasExited)
            {
                try { await RequestStopAsync(receipt, TimeSpan.FromSeconds(3)); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
                { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            }
            if (process.HasExited) DeleteReceipt();
            throw;
        }
    }

    internal async Task<PortableRuntimeState> StopAsync()
    {
        if (!EnsureRoot(create: false)) return new("STOPPED", null, settings.WebPort, "Data root does not yet exist.");
        using var gate = EnterGate();
        var receipt = ReadReceipt();
        using var process = FindOwnedProcess();
        if (process is null)
        {
            DeleteReceipt();
            return new("STOPPED", null, settings.WebPort, "No owned running process; data retained.");
        }
        await RequestStopAsync(receipt!, TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50)); }
        catch (TimeoutException) { throw new InvalidOperationException("STOP_TIMEOUT: service did not exit; no forced termination, retain data and inspect logs."); }
        DeleteReceipt();
        return new("STOPPED", null, settings.WebPort, "Graceful application shutdown completed; data retained.");
    }

    internal async Task<PortableRuntimeState> StatusAsync()
    {
        if (!EnsureRoot(create: false)) return new("STOPPED", null, settings.WebPort, "Data root does not yet exist.");
        using var gate = EnterGate();
        using var process = FindOwnedProcess();
        if (process is null) return new("STOPPED", null, settings.WebPort, "No owned running process.");
        await RequireHealthAsync(process, TimeSpan.FromSeconds(5));
        return new("RUNNING", process.Id, settings.WebPort, "Owned process is healthy.");
    }

    private bool EnsureRoot(bool create)
    {
        var root = ManagedDataRootPath.Normalize(settings.DataRoot);
        var parent = root;
        while (!Directory.Exists(parent)) parent = Path.GetDirectoryName(parent) ?? throw new InvalidDataException();
        ManagedDataRootPath.ResolveExistingDirectory(parent);
        if (!Directory.Exists(root))
        {
            if (!create) return false;
            Directory.CreateDirectory(root);
        }
        ManagedDataRootPath.ResolveExistingDirectory(root);
        var marker = Path.Combine(root, ".netconsole-next-root");
        RejectLink(marker);
        if (File.Exists(marker))
        {
            if (new FileInfo(marker).Length > 128 || File.ReadAllText(marker).Trim() != "NETCONSOLE_NEXT_V1")
                throw new InvalidDataException("Invalid Next data-root marker; data retained.");
        }
        else
        {
            if (!create || Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidDataException("Refusing an unmarked data root; data retained.");
            // Initialize only an empty root before creating the manager's own state files.
            // Catalog startup uses this same marker and refuses foreign nonempty roots.
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write("NETCONSOLE_NEXT_V1"u8);
            stream.Flush(flushToDisk: true);
        }
        return true;
    }

    private FileStream EnterGate()
    {
        RejectLink(GatePath);
        try { return new FileStream(GatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another Local Manager operation is active; retry when it finishes."); }
    }

    private PortableRuntimeReceipt? ReadReceipt()
    {
        RejectLink(ReceiptPath);
        if (!File.Exists(ReceiptPath)) return null;
        if (new FileInfo(ReceiptPath).Length > 4096) throw new InvalidDataException("Invalid runtime receipt; no process was controlled.");
        var receipt = JsonSerializer.Deserialize<PortableRuntimeReceipt>(File.ReadAllBytes(ReceiptPath), JsonOptions);
        if (receipt is null || receipt.ProcessId <= 0 || receipt.StartUtcTicks <= 0 || receipt.WebPort != settings.WebPort ||
            !string.Equals(receipt.Executable, settings.ServiceExecutable, StringComparison.OrdinalIgnoreCase) ||
            receipt.PipeName is not { Length: 55 } || !receipt.PipeName.StartsWith("NetConsoleNext-", StringComparison.Ordinal) ||
            !receipt.PipeName[15..].All(Uri.IsHexDigit))
            throw new InvalidDataException("Runtime receipt does not match this deployment; no process was controlled.");
        return receipt;
    }

    private Process? FindOwnedProcess()
    {
        var receipt = ReadReceipt();
        if (receipt is null) return null;
        Process process;
        try { process = Process.GetProcessById(receipt.ProcessId); }
        catch (ArgumentException) { return null; }
        if (process.HasExited) { process.Dispose(); return null; }
        if (process.StartTime.ToUniversalTime().Ticks != receipt.StartUtcTicks ||
            !string.Equals(process.MainModule?.FileName, receipt.Executable, StringComparison.OrdinalIgnoreCase))
        { process.Dispose(); throw new InvalidDataException("Process identity changed; no unrelated process was controlled."); }
        return process;
    }

    private async Task RequireHealthAsync(Process process, TimeSpan timeout)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        using var deadline = new CancellationTokenSource(timeout);
        while (!deadline.IsCancellationRequested)
        {
            if (process.HasExited) throw new InvalidOperationException($"Service exited during startup (exit {process.ExitCode}); inspect its data-root logs and installed .NET runtime.");
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{settings.WebPort}/api/system/health",
                    HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                    var bytes = new byte[65_537];
                    var count = 0;
                    while (count < bytes.Length)
                    {
                        var read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count == bytes.Length) throw new InvalidDataException("Health response exceeds its limit.");
                    using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
                    var value = json.RootElement;
                    if (value.ValueKind == JsonValueKind.Object &&
                        value.TryGetProperty("processId", out var pid) && pid.TryGetInt32(out var id) && id == process.Id &&
                        value.TryGetProperty("healthState", out var health) && health.ValueKind == JsonValueKind.String && health.GetString() == "HEALTHY" &&
                        value.TryGetProperty("serviceState", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "running") return;
                    throw new InvalidOperationException("Service health does not match the owned healthy instance; inspect logs and port configuration.");
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
            try { await Task.Delay(200, deadline.Token); }
            catch (OperationCanceledException) { break; }
        }
        throw new InvalidOperationException("STARTUP_TIMEOUT: service did not become healthy; inspect data-root logs and available disk space.");
    }

    private static async Task RequestStopAsync(PortableRuntimeReceipt receipt, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        await using var pipe = new NamedPipeClientStream(".", receipt.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        await pipe.WriteAsync("STOP"u8.ToArray(), deadline.Token);
        await pipe.FlushAsync(deadline.Token);
        var reply = new byte[2];
        await pipe.ReadExactlyAsync(reply, deadline.Token);
        if (!reply.AsSpan().SequenceEqual("OK"u8)) throw new IOException("Service rejected graceful stop.");
    }

    private void WriteReceipt(PortableRuntimeReceipt receipt)
    {
        RejectLink(ReceiptPath);
        var part = ReceiptPath + ".part";
        RejectLink(part);
        using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions)); stream.Flush(flushToDisk: true); }
        File.Move(part, ReceiptPath, overwrite: true);
    }
    private void DeleteReceipt() { RejectLink(ReceiptPath); if (File.Exists(ReceiptPath)) File.Delete(ReceiptPath); }
    private static void RejectLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Manager state cannot traverse links.");
    }
}
