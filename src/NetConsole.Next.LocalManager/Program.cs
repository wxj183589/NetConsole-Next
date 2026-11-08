using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using NetConsole.Next.Credentials;
using NetConsole.Next.LocalManager;
using System.Text.Json;

const string serviceName = "NetConsoleNext";

if (args.Length == 0) return await RunManagerMenuAsync();

if (args[0] is "launch" or "portable-start" or "portable-stop" or "portable-restart" or "portable-status" or "diagnose")
    return args.Length <= 2 ? await RunLocalOperationAsync(args[0], args.Length == 2 ? args[1] : null) : InvalidArguments();

if (args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

if (args[0] == "open-web")
    return args.Length <= 2 ? OpenWeb(args.Length > 1 ? args[1] : null) : InvalidArguments();
if (args[0] is "open-data" or "open-raw" or "open-logs" or "open-published")
    return args.Length <= 2 ? OpenManagedDirectory(args[0], args.Length > 1 ? args[1] : null) : InvalidArguments();
if (args[0] is "api-token" or "rotate-api-token")
    return args.Length <= 2 ? ManageApiToken(args[0], args.Length > 1 ? args[1] : null) : InvalidArguments();
if (args[0] == "credential")
    return ManageCredential(args);
if (args[0] == "web-password")
    return ManageWebPassword(args);
if (args[0] == "doctor")
{
    if (args.Length > 3) return InvalidArguments();
    try
    {
        var root = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root) && OperatingSystem.IsWindows())
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetConsoleNext", "data");
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Provide a managed data root.");
        var portText = args.Length > 2 ? args[2] : Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WEB_PORT") ?? "18771";
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535) throw new ArgumentException("Invalid loopback port.");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        var report = await LocalDeploymentDoctor.RunAsync(root, port, client);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return report.State == "PASS" ? 0 : 1;
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Deployment diagnosis failed: " + ex.GetType().Name + ".");
        return 2;
    }
}

if (args.Length != 1 || args[0] is not ("status" or "start" or "stop" or "restart"))
{
    Console.Error.WriteLine("Unknown command or unexpected arguments.");
    PrintHelp();
    return 2;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Service control commands are available only on Windows.");
    return 2;
}

try
{
    using var service = new ServiceController(serviceName);
    service.Refresh();
    if (args[0] == "status")
    {
        Console.WriteLine($"{serviceName}: {service.Status}");
        return 0;
    }

    if (args[0] is "stop" or "restart") StopService(service);
    if (args[0] is "start" or "restart") StartService(service);
    Console.WriteLine($"{serviceName}: {service.Status}");
    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Service '{serviceName}' is not installed or is unavailable: {ex.Message}");
    return 1;
}
catch (System.ComponentModel.Win32Exception ex)
{
    Console.Error.WriteLine($"Windows denied the service operation: {ex.Message}");
    return 1;
}
catch (System.ServiceProcess.TimeoutException ex)
{
    Console.Error.WriteLine($"Service state change timed out: {ex.Message}");
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("NetConsole Next Local Manager");
    Console.WriteLine("  (no arguments)            Open the local start/open/stop/restart/diagnose menu");
    Console.WriteLine("  launch [settings.json]    Start installed service or owned portable instance and open Web");
    Console.WriteLine("  portable-start|portable-stop|portable-restart|portable-status [settings.json]");
    Console.WriteLine("  diagnose [settings.json]  Check the configured instance, worker, log limits and free space");
    Console.WriteLine("  status                    Show local Windows service state");
    Console.WriteLine("  start                     Start the local Windows service");
    Console.WriteLine("  stop                      Stop the local Windows service");
    Console.WriteLine("  restart                   Restart the local Windows service");
    Console.WriteLine("  doctor [data-root] [port] Diagnose managed root, instance, health, SQLite, Raw and Netmiko without device access");
    Console.WriteLine("  open-web [port]           Open the loopback service page in the default browser");
    Console.WriteLine("  open-data [data-root]     Open the isolated local data directory");
    Console.WriteLine("  open-raw [data-root]      Open the Raw collection directory");
    Console.WriteLine("  open-logs [data-root]     Open the service log directory");
    Console.WriteLine("  open-published [data-root] Open the published files directory");
    Console.WriteLine("  api-token [data-root]     Display the local API bearer token");
    Console.WriteLine("  rotate-api-token [data-root] Rotate the local API bearer token");
    Console.WriteLine("  credential set <REF> [data-root] Store/update an H3C DPAPI secret");
    Console.WriteLine("  credential revoke <REF> [data-root] Revoke an H3C DPAPI secret");
    Console.WriteLine("  web-password reset [data-root]  Reset a local Web user password (Windows administrator)");
}

static async Task<int> RunManagerMenuAsync()
{
    while (true)
    {
        Console.WriteLine("NetConsole Next 本机管理：1 启动并打开  2 停止  3 重启并打开  4 诊断  5 状态  0 退出管理器");
        var command = Console.ReadLine()?.Trim();
        if (command is null or "0") return 0;
        var operation = command switch
        {
            "1" => "launch", "2" => "menu-stop", "3" => "menu-restart",
            "4" => "diagnose", "5" => "menu-status", _ => null
        };
        if (operation is null) { Console.WriteLine("请选择 0–5。"); continue; }
        await RunLocalOperationAsync(operation, null);
    }
}

static async Task<int> RunLocalOperationAsync(string command, string? config)
{
    try
    {
        var settings = LocalManagerSettings.Load(config);
        if (command == "diagnose")
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
            var deployment = await LocalDeploymentDoctor.RunAsync(settings.DataRoot, settings.WebPort, client);
            var storage = LocalStorageDiagnostics.Run(settings.DataRoot);
            Console.WriteLine(JsonSerializer.Serialize(new { deployment, storage }, PortableRuntime.JsonOptions));
            return deployment.State == "PASS" && storage.State == "PASS" ? 0 : 1;
        }
        // Explicit portable commands always target a receipt-verified child. The simple
        // entry prefers SCM when installed and never falls back after SCM access denial.
        var useScm = false;
        if (!command.StartsWith("portable-", StringComparison.Ordinal) && OperatingSystem.IsWindows())
        {
            var services = ServiceController.GetServices();
            try
            {
                foreach (var installed in services)
                    if (installed.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase)) useScm = true;
            }
            finally { foreach (var service in services) service.Dispose(); }
        }
        var stop = command is "portable-stop" or "portable-restart" or "menu-stop" or "menu-restart";
        var start = command is "portable-start" or "portable-restart" or "launch" or "menu-restart";
        if (useScm && OperatingSystem.IsWindows())
        {
            using var service = new ServiceController(serviceName);
            if (stop) StopService(service);
            if (start) StartService(service);
            service.Refresh();
            Console.WriteLine($"{serviceName} (SCM): {service.Status}");
        }
        else
        {
            var runtime = new PortableRuntime(settings);
            if (stop) Console.WriteLine(JsonSerializer.Serialize(await runtime.StopAsync(), PortableRuntime.JsonOptions));
            if (start) Console.WriteLine(JsonSerializer.Serialize(await runtime.StartAsync(), PortableRuntime.JsonOptions));
            if (!stop && !start) Console.WriteLine(JsonSerializer.Serialize(await runtime.StatusAsync(), PortableRuntime.JsonOptions));
        }
        return command is "launch" or "menu-restart" ? OpenWeb(settings.WebPort.ToString()) : 0;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
        ArgumentException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException or
        System.ServiceProcess.TimeoutException or PlatformNotSupportedException)
    {
        Console.Error.WriteLine("Local Manager: " + ex.Message);
        return 1;
    }
}

static int InvalidArguments()
{
    Console.Error.WriteLine("Too many arguments.");
    PrintHelp();
    return 2;
}

static void StopService(ServiceController service)
{
    service.Refresh();
    if (service.Status == ServiceControllerStatus.StartPending)
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    if (service.Status == ServiceControllerStatus.StopPending)
    {
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
        return;
    }
    if (service.Status != ServiceControllerStatus.Stopped)
    {
        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
    }
}

static void StartService(ServiceController service)
{
    service.Refresh();
    if (service.Status == ServiceControllerStatus.StopPending)
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
    if (service.Status == ServiceControllerStatus.StartPending)
    {
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        return;
    }
    if (service.Status != ServiceControllerStatus.Running)
    {
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }
}

static int OpenWeb(string? requestedPort)
{
    var portText = requestedPort ?? Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WEB_PORT") ?? "18771";
    if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
    {
        Console.Error.WriteLine("Web port must be an integer between 1 and 65535.");
        return 2;
    }

    var address = $"http://127.0.0.1:{port}/";
    try
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        }
        else
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(address);
            Process.Start(start);
        }
        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine($"Could not open the local service page: {ex.Message}");
        return 1;
    }
}

static int OpenManagedDirectory(string command, string? requestedRoot)
{
    var root = requestedRoot;
    if (string.IsNullOrWhiteSpace(root)) root = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_DATA_ROOT");
    if (string.IsNullOrWhiteSpace(root) && OperatingSystem.IsWindows())
        root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetConsoleNext", "data");
    if (string.IsNullOrWhiteSpace(root))
    {
        Console.Error.WriteLine("Provide a data root or set NETCONSOLE_NEXT_DATA_ROOT.");
        return 2;
    }

    string fullRoot;
    try { fullRoot = ManagedDataRootPath.ResolveExistingDirectory(root); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Directory is not a permitted NetConsole Next data root.");
        return 2;
    }
    var relative = command switch
    {
        "open-data" => null,
        "open-raw" => "raw",
        "open-logs" => "logs",
        "open-published" => "published",
        _ => throw new InvalidOperationException("Unknown directory command.")
    };
    var target = relative is null ? fullRoot : Path.Combine(fullRoot, relative);
    try { target = ManagedDataRootPath.ResolveExistingDirectory(target); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Directory does not exist or traverses a reparse point.");
        return 1;
    }

    try
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open",
            UseShellExecute = false
        };
        start.ArgumentList.Add(target);
        Process.Start(start);
        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine($"Could not open directory: {ex.Message}");
        return 1;
    }
}

static int ManageApiToken(string command, string? requestedRoot)
{
    try
    {
        var root = ResolveManagedRoot(requestedRoot);
        Console.WriteLine(command == "api-token"
            ? LocalApiTokenStore.ReadExisting(root)
            : LocalApiTokenStore.Rotate(root));
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
    {
        Console.Error.WriteLine($"Local API token operation failed: {ex.GetType().Name}.");
        return 1;
    }
}

static int ManageCredential(string[] arguments)
{
    if (arguments.Length is < 3 or > 4 || arguments[1] is not ("set" or "revoke"))
    {
        Console.Error.WriteLine("Use: credential set <REF> [data-root] or credential revoke <REF> [data-root].");
        return 2;
    }
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("H3C credential storage requires Windows DPAPI.");
        return 2;
    }
    try
    {
        var root = ResolveManagedRoot(arguments.Length == 4 ? arguments[3] : null);
        var reference = arguments[2];
        if (arguments[1] == "revoke")
        {
            if (!WindowsDpapiCredentialVault.Revoke(root, reference))
            {
                Console.Error.WriteLine("Credential reference was not present.");
                return 1;
            }
            Console.WriteLine("Credential revoked.");
            return 0;
        }
        Console.Write("H3C password: ");
        var secret = ReadSecret();
        if (secret.Length == 0)
        {
            Console.Error.WriteLine("Credential was empty; no change was made.");
            return 2;
        }
        WindowsDpapiCredentialVault.Set(root, reference, secret);
        Console.WriteLine("Credential stored or updated.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or PlatformNotSupportedException)
    {
        Console.Error.WriteLine($"Credential operation failed: {ex.GetType().Name}.");
        return 1;
    }
}

static int ManageWebPassword(string[] arguments)
{
    if (arguments.Length is < 2 or > 3 || arguments[1] != "reset")
    {
        Console.Error.WriteLine("Use: web-password reset [data-root]. Passwords are entered interactively and are never command-line arguments.");
        return 2;
    }
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("Web password recovery is available only on Windows.");
        return 2;
    }
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    var principal = new System.Security.Principal.WindowsPrincipal(identity);
    if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
    {
        Console.Error.WriteLine("Run this command from an elevated Windows administrator prompt.");
        return 1;
    }
    try
    {
        var root = ResolveManagedRoot(arguments.Length == 3 ? arguments[2] : null);
        Console.WriteLine("NetConsole Next Web password recovery (the service may be running or stopped).");
        Console.Write("Account [1=admin, 2=superadmin, 3=cancel]: ");
        var account = Console.ReadLine()?.Trim();
        var username = account switch { "1" => "admin", "2" => "superadmin", _ => null };
        if (username is null) { Console.WriteLine("No changes were made."); return 0; }
        Console.Write("Password [1=restore default, 2=enter custom, 3=cancel]: ");
        var mode = Console.ReadLine()?.Trim();
        var password = mode switch
        {
            "1" => username,
            "2" => ReadConfirmedSecret(),
            _ => null
        };
        if (password is null) { Console.WriteLine("No changes were made."); return 0; }
        var result = WebPasswordRecovery.Reset(root, username, password, "local-manager");
        Console.WriteLine($"{result.Username} password reset; revoked {result.RevokedSessions} existing Web session(s).");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
    {
        Console.Error.WriteLine($"Web password recovery failed: {ex.GetType().Name}.");
        return 1;
    }
}

static string? ReadConfirmedSecret()
{
    Console.Write("New password: ");
    var first = ReadSecret();
    Console.Write("Repeat new password: ");
    var second = ReadSecret();
    if (first.Length < 8 || !string.Equals(first, second, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Passwords must match and contain at least 8 characters.");
        return null;
    }
    return first;
}

static string ResolveManagedRoot(string? requestedRoot)
{
    var root = requestedRoot;
    if (string.IsNullOrWhiteSpace(root)) root = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_DATA_ROOT");
    if (string.IsNullOrWhiteSpace(root) && OperatingSystem.IsWindows())
        root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetConsoleNext", "data");
    if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Provide a data root or set NETCONSOLE_NEXT_DATA_ROOT.");
    var full = ManagedDataRootPath.ResolveExistingDirectory(root);
    var marker = Path.Combine(full, ".netconsole-next-root");
    if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
        File.ReadAllText(marker).Trim() != "NETCONSOLE_NEXT_V1")
        throw new InvalidOperationException("Data root is not marked as a NetConsole Next managed data directory.");
    return full;
}

static string ReadSecret()
{
    if (Console.IsInputRedirected) return Console.In.ReadLine() ?? string.Empty;
    var value = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (value.Length > 0) value.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
    }
    Console.WriteLine();
    return value.ToString();
}
