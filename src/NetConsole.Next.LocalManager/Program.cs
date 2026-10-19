using System.Diagnostics;
using System.ServiceProcess;

const string serviceName = "NetConsoleNext";

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

if (args[0] == "open-web")
    return args.Length <= 2 ? OpenWeb(args.Length > 1 ? args[1] : null) : InvalidArguments();
if (args[0] is "open-data" or "open-raw" or "open-logs" or "open-published")
    return args.Length <= 2 ? OpenManagedDirectory(args[0], args.Length > 1 ? args[1] : null) : InvalidArguments();

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
    Console.WriteLine("  status                    Show local Windows service state");
    Console.WriteLine("  start                     Start the local Windows service");
    Console.WriteLine("  stop                      Stop the local Windows service");
    Console.WriteLine("  restart                   Restart the local Windows service");
    Console.WriteLine("  open-web [port]           Open the loopback service page in the default browser");
    Console.WriteLine("  open-data [data-root]     Open the isolated local data directory");
    Console.WriteLine("  open-raw [data-root]      Open the Raw collection directory");
    Console.WriteLine("  open-logs [data-root]     Open the service log directory");
    Console.WriteLine("  open-published [data-root] Open the published files directory");
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
    if (service.Status != ServiceControllerStatus.Stopped)
    {
        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
    }
}

static void StartService(ServiceController service)
{
    service.Refresh();
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

    var fullRoot = Path.GetFullPath(root);
    var relative = command switch
    {
        "open-data" => null,
        "open-raw" => "raw",
        "open-logs" => "logs",
        "open-published" => "published",
        _ => throw new InvalidOperationException("Unknown directory command.")
    };
    var target = relative is null ? fullRoot : Path.Combine(fullRoot, relative);
    if (!Directory.Exists(target))
    {
        Console.Error.WriteLine($"Directory does not exist: {target}");
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
