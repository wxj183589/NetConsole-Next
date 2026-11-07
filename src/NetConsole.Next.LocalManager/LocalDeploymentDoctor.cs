using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.LocalManager;

internal sealed record DeploymentCheck(string Key, string State, string Detail, string Action);
internal sealed record DeploymentDiagnosis(string State, string DataRoot, int WebPort, int? ProcessId,
    IReadOnlyList<DeploymentCheck> Checks);

internal static class LocalDeploymentDoctor
{
    public static async Task<DeploymentDiagnosis> RunAsync(string dataRoot, int port, HttpClient client,
        Func<CancellationToken, Task<bool>>? workerProbe = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var root = ManagedDataRootPath.Normalize(dataRoot);
        var checks = new List<DeploymentCheck>();
        DeploymentDiagnosis Finish(int? pid = null) => new(
            checks.Any(c => c.State == "FAIL") ? "FAILED" : checks.Any(c => c.State == "UNKNOWN") ? "PARTIAL" : "PASS",
            root, port, pid, checks);
        try { root = ManagedDataRootPath.ResolveExistingDirectory(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            checks.Add(new("DATA_ROOT", "FAIL", "Data root is missing, inaccessible or traverses a reparse point.",
                "Choose the existing NetConsole Next managed data root and check directory permissions."));
            return Finish();
        }
        string? instance = null;
        try
        {
            var marker = Path.Combine(root, ".netconsole-next-root");
            if (!SafeFile(marker) || new FileInfo(marker).Length > 128 || File.ReadAllText(marker).Trim() != "NETCONSOLE_NEXT_V1")
                throw new InvalidDataException();
            checks.Add(new("DATA_ROOT", "PASS", "Existing managed root; no initialization performed.", ""));
            var database = Path.Combine(root, "catalog.db");
            if (!SafeFile(database)) throw new InvalidDataException();
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT instance_id FROM instance_identity WHERE id=1";
            instance = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(instance)) throw new InvalidDataException();
            checks.Add(new("CATALOG", "PASS", "Catalog is readable in read-only mode; instance identity is present.", ""));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException)
        {
            checks.Add(new("CATALOG", "FAIL", "Managed marker or catalog could not be read (" + ex.GetType().Name + ").",
                "Check the data-root marker, database availability and service-account permissions; retain all data."));
            return Finish();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int? processId = null;
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/api/system/health",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[65_537];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token);
                if (read == 0) break;
                count += read;
            }
            if (count == buffer.Length) throw new InvalidDataException();
            using var health = JsonDocument.Parse(buffer.AsMemory(0, count));
            var value = health.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            string? Text(string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String
                ? item.GetString() : null;
            var matchingInstance = string.Equals(Text("instanceId"), instance, StringComparison.Ordinal);
            checks.Add(new("INSTANCE", matchingInstance ? "PASS" : "FAIL",
                matchingInstance ? "Loopback service belongs to this data root." : "Port serves a different or unidentifiable instance.",
                matchingInstance ? "" : "Check NETCONSOLE_NEXT_DATA_ROOT and NETCONSOLE_NEXT_WEB_PORT; do not stop an unrelated process."));
            if (!matchingInstance) return Finish();
            if (value.TryGetProperty("processId", out var pid) && pid.TryGetInt32(out var numericPid) && numericPid > 0)
                processId = numericPid;
            checks.Add(new("HEALTH", Text("healthState") == "HEALTHY" && Text("serviceState") == "running" && processId.HasValue ? "PASS" : "FAIL",
                "Service state: " + (Text("serviceState") ?? "UNKNOWN") + "; health: " + (Text("healthState") ?? "UNKNOWN") + ".",
                "Inspect Local Manager open-logs and the service health page when degraded."));
            checks.Add(new("SQLITE_STARTUP", Text("sqliteIntegrityAtStartup") == "ok" ? "PASS" : "FAIL",
                "Reported startup integrity: " + (Text("sqliteIntegrityAtStartup") ?? "UNKNOWN") + ".",
                "Retain the database and inspect service logs before recovery; no automatic database rewrite is performed."));
            checks.Add(new("RAW_WRITER", value.TryGetProperty("rawWriteAvailable", out var raw) && raw.ValueKind == JsonValueKind.True ? "PASS" : "FAIL",
                "Raw writer readiness reported by the matching service.", "Check free space, permissions and logs if unavailable."));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidDataException)
        {
            checks.Add(new("LOOPBACK_HTTP", "FAIL", "Health response unavailable or invalid (" + ex.GetType().Name + ").",
                "Run Local Manager status; verify the configured port and start the installed NetConsoleNext service if stopped."));
        }
        using var workerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var ready = await (workerProbe ?? ProbeWorkerAsync)(workerTimeout.Token);
            checks.Add(new("WORKER", ready ? "PASS" : "FAIL", ready ? "Netmiko worker imports and close handshake succeeded; no device connected."
                : "Netmiko runtime or worker handshake failed.", "Verify the offline Python/Netmiko bundle and configured worker paths."));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            checks.Add(new("WORKER", "FAIL", "Worker probe failed (" + ex.GetType().Name + ").",
                "Verify NETCONSOLE_NEXT_NETMIKO_PYTHON, NETCONSOLE_NEXT_NETMIKO_WORKER and bundled dependencies."));
        }
        return Finish(processId);
    }

    private static bool SafeFile(string path) => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static async Task<bool> ProbeWorkerAsync(CancellationToken token)
    {
        var configuredPython = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_PYTHON");
        var bundledPython = Path.Combine(AppContext.BaseDirectory, "tools", "netmiko", "runtime", "python.exe");
        var python = !string.IsNullOrWhiteSpace(configuredPython) ? configuredPython : File.Exists(bundledPython) ? bundledPython : "python";
        var script = new[] { Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_WORKER"),
            Path.Combine(AppContext.BaseDirectory, "tools", "netmiko", "netmiko_worker.py"),
            Path.Combine(AppContext.BaseDirectory, "tools", "netmiko_worker.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "netmiko_worker.py") }
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (script is null) return false;
        var start = new ProcessStartInfo(python!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        try
        {
            // Drain stderr without displaying it; worker responses never include credentials.
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            await process.StandardInput.WriteLineAsync("{\"op\":\"close\"}".AsMemory(), token);
            process.StandardInput.Close();
            var bytes = new byte[4097];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(bytes.AsMemory(count), token);
                if (read == 0) break;
                count += read;
            }
            if (count == bytes.Length) return false;
            await process.WaitForExitAsync(token);
            await stderr;
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            return process.ExitCode == 0 && json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
                json.RootElement.TryGetProperty("op", out var operation) && operation.ValueKind == JsonValueKind.String &&
                operation.GetString() == "close";
        }
        catch (JsonException) { return false; }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
