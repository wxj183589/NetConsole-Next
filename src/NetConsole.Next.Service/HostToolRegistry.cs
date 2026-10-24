using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed class HostToolRegistry
{
    private static readonly (string Id, string Name, string Product)[] KnownTools =
    [
        ("securecrt", "SecureCRT", "SecureCRT"),
        ("xshell", "Xshell", "Xshell"),
        ("putty", "PuTTY", "PuTTY")
    ];
    private readonly RuntimeSettings _settings;
    private readonly object _gate = new();
    private Dictionary<string, HostToolConfiguration> _configuration = new(StringComparer.OrdinalIgnoreCase);

    public HostToolRegistry(RuntimeSettings settings)
    {
        _settings = settings;
        Load();
    }

    public IReadOnlyList<HostToolDefinition> List()
    {
        lock (_gate)
        {
            return KnownTools.Select(tool => Describe(tool.Id, tool.Name, tool.Product)).ToArray();
        }
    }

    public HostToolDefinition Configure(string id, string? executablePath, string actor)
    {
        var tool = Find(id) ?? throw new ArgumentException("Unknown host tool.", nameof(id));
        var normalized = string.IsNullOrWhiteSpace(executablePath) ? null : ValidatePath(executablePath.Trim());
        lock (_gate)
        {
            _configuration[tool.Id] = new HostToolConfiguration(normalized);
            Save();
        }
        Audit(actor, "CONFIGURE", tool.Id, normalized is null ? "cleared" : "path-updated");
        return Describe(tool.Id, tool.Name, tool.Product);
    }

    public HostToolConfiguration? GetConfiguration(string id)
    {
        lock (_gate) return _configuration.TryGetValue(id, out var value) ? value : null;
    }

    private HostToolDefinition Describe(string id, string name, string product)
    {
        _configuration.TryGetValue(id, out var configured);
        var path = configured?.ExecutablePath;
        var status = "NOT_CONFIGURED";
        string? detail = "Configure the installed executable on the host.";
        if (!OperatingSystem.IsWindows())
        {
            status = "HOST_WINDOWS_REQUIRED";
            detail = "External GUI tools are launched only in the installed Windows host session.";
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path)) { status = "EXECUTABLE_NOT_FOUND"; detail = "Configured executable was not found."; }
            else if (IsReparse(path)) { status = "REPARSE_POINT_BLOCKED"; detail = "Executable path cannot be a symbolic link or reparse point."; }
            else if (!IsExpectedExecutable(id, path)) { status = "EXECUTABLE_NOT_ALLOWED"; detail = "Only the vendor executable for this registered tool is allowed."; }
            else { status = "READY"; detail = null; }
        }
        return new(id, name, product, path, status, path is not null, OperatingSystem.IsWindows(), detail);
    }

    private static (string Id, string Name, string Product)? Find(string id)
    {
        foreach (var tool in KnownTools)
            if (string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase)) return tool;
        return null;
    }

    private static string ValidatePath(string path)
    {
        if (!Path.IsPathRooted(path) || path.Any(char.IsControl) || path.IndexOfAny(['\0', '"']) >= 0)
            throw new ArgumentException("Executable path must be an absolute path without control characters.", nameof(path));
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full)) throw new ArgumentException("Executable path must name a file.", nameof(path));
        return full;
    }

    private void Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path)) return;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, HostToolConfiguration>>(File.ReadAllText(path));
            if (parsed is not null) _configuration = new(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { /* invalid config is reported as not configured and can be replaced by SUPERADMIN */ }
    }

    private void Save()
    {
        Directory.CreateDirectory(_settings.DataRoot);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_configuration, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, ConfigPath, true);
    }

    private string ConfigPath => Path.Combine(_settings.DataRoot, "host-tools.json");
    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    internal static bool IsExpectedExecutable(string id, string path) => (id.ToLowerInvariant(), Path.GetFileName(path).ToLowerInvariant()) switch
    {
        ("securecrt", "securecrt.exe") => true,
        ("xshell", "xshell.exe") => true,
        ("putty", "putty.exe") => true,
        _ => false
    };

    private void Audit(string actor, string operation, string toolId, string detail)
    {
        try
        {
            var directory = Path.Combine(_settings.DataRoot, "audit"); Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(new { atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), actor, operation, toolId, detail });
            File.AppendAllText(Path.Combine(directory, "host-tools.jsonl"), line + Environment.NewLine);
        }
        catch { /* audit failure cannot turn a successful safe configuration into a secret leak */ }
    }
}
