using System.Diagnostics;
using System.ServiceProcess;

const string serviceName = "NetConsoleNext";

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("NetConsole Next Local Manager");
    Console.WriteLine("  status                 Show local Windows service state");
    Console.WriteLine("  start                  Start the local Windows service");
    Console.WriteLine("  stop                   Stop the local Windows service");
    Console.WriteLine("  open-data [directory]  Open the isolated local data directory");
    return 0;
}

if (args[0] == "open-data")
    return OpenDataDirectory(args.Length > 1 ? args[1] : null);

if (args[0] is not ("status" or "start" or "stop"))
{
    Console.Error.WriteLine($"Unknown command: {args[0]}");
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

    if (args[0] == "start")
    {
        if (service.Status != ServiceControllerStatus.Running)
        {
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
        Console.WriteLine($"{serviceName}: Running");
        return 0;
    }

    if (service.Status != ServiceControllerStatus.Stopped)
    {
        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
    }
    Console.WriteLine($"{serviceName}: Stopped");
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

static int OpenDataDirectory(string? requestedPath)
{
    var root = requestedPath;
    if (string.IsNullOrWhiteSpace(root)) root = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_DATA_ROOT");
    if (string.IsNullOrWhiteSpace(root) && OperatingSystem.IsWindows())
        root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetConsoleNext", "data");
    if (string.IsNullOrWhiteSpace(root))
    {
        Console.Error.WriteLine("Provide a data directory or set NETCONSOLE_NEXT_DATA_ROOT.");
        return 2;
    }

    var fullPath = Path.GetFullPath(root);
    if (!Directory.Exists(fullPath))
    {
        Console.Error.WriteLine($"Data directory does not exist: {fullPath}");
        return 1;
    }

    try
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open",
            UseShellExecute = false
        };
        start.ArgumentList.Add(fullPath);
        Process.Start(start);
        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine($"Could not open the data directory: {ex.Message}");
        return 1;
    }
}
