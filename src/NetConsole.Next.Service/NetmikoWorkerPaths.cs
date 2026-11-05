namespace NetConsole.Next.Service;

internal static class NetmikoWorkerPaths
{
    internal static string FindPython()
    {
        var configured = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "netmiko", "runtime", "python.exe");
        return File.Exists(bundled) ? bundled : "python";
    }

    internal static string FindScript()
    {
        var configured = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_NETMIKO_WORKER");
        var candidates = new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "tools", "netmiko", "netmiko_worker.py"),
            Path.Combine(AppContext.BaseDirectory, "tools", "netmiko_worker.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "netmiko_worker.py")
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!));
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Netmiko worker script was not found.");
    }
}
