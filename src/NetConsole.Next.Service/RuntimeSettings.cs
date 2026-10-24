using System.Net;

namespace NetConsole.Next.Service;

internal sealed class RuntimeSettings
{
    public required string DataRoot { get; init; }
    public required int WebPort { get; init; }
    public required int SyslogPort { get; init; }
    public required IPAddress SyslogBind { get; init; }

    public static RuntimeSettings FromEnvironment()
    {
        var root = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Set NETCONSOLE_NEXT_DATA_ROOT to an isolated new directory.");
        var full = Path.GetFullPath(root);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Data root cannot be a filesystem root.");
        var bindText = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_SYSLOG_BIND") ?? "127.0.0.1";
        if (!IPAddress.TryParse(bindText, out var bind))
            throw new InvalidOperationException("NETCONSOLE_NEXT_SYSLOG_BIND must be a valid IP address.");
        if (!IPAddress.IsLoopback(bind) &&
            Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_ALLOW_REMOTE_SYSLOG") != "1")
            throw new InvalidOperationException("Remote UDP reception requires explicit opt-in.");
        return new RuntimeSettings
        {
            DataRoot = full,
            WebPort = ParsePort("NETCONSOLE_NEXT_WEB_PORT", 18771, false),
            SyslogPort = ParsePort("NETCONSOLE_NEXT_SYSLOG_PORT", 0, true),
            SyslogBind = bind
        };
    }

    private static int ParsePort(string key, int fallback, bool zeroAllowed)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, out var p) || p > 65535 || p < (zeroAllowed ? 0 : 1))
            throw new InvalidOperationException(key + " is invalid.");
        return p;
    }
}
