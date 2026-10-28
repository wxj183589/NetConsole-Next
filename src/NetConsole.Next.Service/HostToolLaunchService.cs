namespace NetConsole.Next.Service;

internal sealed record HostToolLaunchResult(string Status, string? Detail, string ToolId, string DeviceId);

internal sealed class HostToolLaunchService
{
    private readonly HostToolRegistry _registry;
    private readonly Catalog _catalog;
    private readonly DesktopHelperIpc _ipc;

    public HostToolLaunchService(HostToolRegistry registry, Catalog catalog, DesktopHelperIpc ipc)
    { _registry = registry; _catalog = catalog; _ipc = ipc; }

    public async Task<HostToolLaunchResult> LaunchAsync(string toolId, string deviceId, CancellationToken cancellationToken)
    {
        var tool = _registry.List().FirstOrDefault(item => string.Equals(item.Id, toolId, StringComparison.OrdinalIgnoreCase));
        if (tool is null) return new("INVALID_ARGUMENTS", "Unknown tool.", toolId, deviceId);
        var profile = _catalog.GetH3cDeviceProfile(deviceId);
        if (profile is null || !profile.Enabled) return new("INVALID_DEVICE", "Device is not an enabled Next device profile.", tool.Id, deviceId);
        if (tool.Status == "NOT_CONFIGURED" || string.IsNullOrWhiteSpace(tool.ExecutablePath)) return new("NOT_CONFIGURED", tool.Detail, tool.Id, deviceId);
        if (tool.Status != "READY") return new(tool.Status, tool.Detail, tool.Id, deviceId);
        if (!IsSafeHost(profile.Host) || profile.Port is < 1 or > 65535 || !IsSafeToken(profile.Username))
            return new("INVALID_DEVICE", "Device profile contains an invalid host, port or username.", tool.Id, deviceId);

        var args = tool.Id.ToLowerInvariant() switch
        {
            "securecrt" => new[] { "/SSH2", "/L", profile.Username, "/P", profile.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), profile.Host },
            "xshell" => new[] { "-url", $"ssh://{profile.Username}@{profile.Host}:{profile.Port}" },
            "putty" => new[] { "-ssh", "-P", profile.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{profile.Username}@{profile.Host}" },
            _ => Array.Empty<string>()
        };
        if (args.Length == 0) return new("INVALID_ARGUMENTS", "Tool argument policy is missing.", tool.Id, deviceId);
        var response = await _ipc.SendAsync(new
        {
            operation = "OPEN_EXTERNAL_TERMINAL",
            toolId = tool.Id,
            executablePath = tool.ExecutablePath,
            arguments = args,
            deviceId,
            host = profile.Host,
            port = profile.Port,
            username = profile.Username
        }, cancellationToken);
        return new(response.Status, response.Detail, tool.Id, deviceId);
    }

    private static bool IsSafeHost(string value) => value.Length is > 0 and <= 253 &&
        value.All(c => char.IsLetterOrDigit(c) || c is '.' or ':' or '-' or '[' or ']');
    private static bool IsSafeToken(string value) => value.Length is > 0 and <= 128 && value.All(c => !char.IsControl(c) && c is not '"' and not '\'' and not ' ');
}
