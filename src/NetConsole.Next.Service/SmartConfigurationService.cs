namespace NetConsole.Next.Service;

internal sealed record SmartConfigurationVariable(string Name, string Label, string Example, bool Required);
internal sealed record SmartConfigurationTemplate(string Id, string Name, string Description,
    IReadOnlyList<SmartConfigurationVariable> Variables);
internal sealed record SmartConfigurationPreviewRequest(string TemplateId,
    IReadOnlyDictionary<string, string>? Variables, string? SiteId = null);
internal sealed record SmartConfigurationCommand(int Index, string Command, string Risk);
internal sealed record SmartConfigurationPreview(string PreviewId, string TemplateId, string TemplateName,
    string State, string ExecutionMode, bool DeviceWriteAllowed, bool WriteAttempted, string? SiteId,
    IReadOnlyList<SmartConfigurationCommand> Commands, IReadOnlyList<string> Errors, long CreatedMs);

internal sealed class SmartConfigurationValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class SmartConfigurationService
{
    private static readonly IReadOnlyDictionary<string, SmartConfigurationTemplate> TemplateCatalog =
        new Dictionary<string, SmartConfigurationTemplate>(StringComparer.OrdinalIgnoreCase)
        {
            ["h3c-interface-description"] = new(
                "h3c-interface-description", "H3C 接口描述", "为接口生成安全的描述配置预览。",
                [new("interface", "接口名称", "GigabitEthernet1/0/1", true),
                 new("description", "描述", "车站上联", true)]),
            ["h3c-access-vlan"] = new(
                "h3c-access-vlan", "H3C Access VLAN", "为接口生成 Access VLAN 配置预览。",
                [new("interface", "接口名称", "GigabitEthernet1/0/1", true),
                 new("vlanId", "VLAN ID", "120", true),
                 new("description", "描述（可选）", "设备管理", false)])
        };

    public IReadOnlyList<SmartConfigurationTemplate> ListTemplates() => TemplateCatalog.Values
        .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();

    public SmartConfigurationPreview Preview(SmartConfigurationPreviewRequest request,
        WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        if (!string.Equals(actor.Role, "SUPERADMIN", StringComparison.OrdinalIgnoreCase))
            throw new SmartConfigurationValidationException("SMART_CONFIG_PERMISSION_DENIED", "智能配置预览需要 SUPERADMIN 权限。");
        var templateId = request.TemplateId?.Trim() ?? string.Empty;
        if (!TemplateCatalog.TryGetValue(templateId, out var template))
            throw new SmartConfigurationValidationException("SMART_CONFIG_TEMPLATE_UNKNOWN", "模板不存在或尚未开放。");
        var siteId = string.IsNullOrWhiteSpace(request.SiteId) ? null : request.SiteId.Trim();
        if (siteId is not null && !DeviceSiteScope.Allows(scope, siteId))
            throw new SmartConfigurationValidationException("SMART_CONFIG_SITE_SCOPE_DENIED", "目标 Site 不在当前用户授权范围内。");

        var variables = request.Variables ?? new Dictionary<string, string>();
        var errors = ValidateVariables(template, variables);
        if (errors.Count > 0)
            throw new SmartConfigurationValidationException("SMART_CONFIG_VARIABLES_INVALID", string.Join(" ", errors));

        var commands = BuildCommands(templateId, variables);
        return new(Guid.NewGuid().ToString("N"), template.Id, template.Name, "PREVIEWED", "DRY_RUN_ONLY",
            DeviceWriteAllowed: false, WriteAttempted: false, siteId, commands, Array.Empty<string>(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static List<string> ValidateVariables(SmartConfigurationTemplate template,
        IReadOnlyDictionary<string, string> variables)
    {
        var errors = new List<string>();
        var knownVariables = template.Variables.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var unknown in variables.Keys.Where(key => !knownVariables.Contains(key)).Order(StringComparer.Ordinal))
            errors.Add($"模板不支持变量 {unknown}。");
        foreach (var variable in template.Variables)
        {
            if (!variables.TryGetValue(variable.Name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                if (variable.Required) errors.Add($"变量 {variable.Label} 不能为空。");
                continue;
            }
            if (value.Length > 128 || value.Contains('\r') || value.Contains('\n') || value.Contains(';'))
                errors.Add($"变量 {variable.Label} 包含非法字符或超出长度限制。");
        }

        if (variables.TryGetValue("interface", out var interfaceName) &&
            !string.IsNullOrWhiteSpace(interfaceName) &&
            !System.Text.RegularExpressions.Regex.IsMatch(interfaceName.Trim(),
                "^(GigabitEthernet|Ten-GigabitEthernet|Twenty-FiveGigE|FortyGigE|Bridge-Aggregation)[0-9/.-]+$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            errors.Add("接口名称不是受支持的 H3C 接口格式。");

        if (variables.TryGetValue("vlanId", out var vlanId) &&
            (!int.TryParse(vlanId, out var parsed) || parsed is < 1 or > 4094))
            errors.Add("VLAN ID 必须是 1 到 4094 的整数。");
        return errors;
    }

    private static IReadOnlyList<SmartConfigurationCommand> BuildCommands(string templateId,
        IReadOnlyDictionary<string, string> variables)
    {
        var interfaceName = variables["interface"].Trim();
        var commands = new List<string> { "system-view", $"interface {interfaceName}" };
        if (templateId.Equals("h3c-interface-description", StringComparison.OrdinalIgnoreCase))
            commands.Add($"description {variables["description"].Trim()}");
        else
        {
            var vlan = int.Parse(variables["vlanId"], System.Globalization.CultureInfo.InvariantCulture);
            commands.Add($"port access vlan {vlan}");
            if (variables.TryGetValue("description", out var description) && !string.IsNullOrWhiteSpace(description))
                commands.Add($"description {description.Trim()}");
        }
        commands.Add("quit");
        commands.Add("return");
        return commands.Select((command, index) => new SmartConfigurationCommand(index, command, "WRITE_PREVIEW_ONLY")).ToArray();
    }
}
