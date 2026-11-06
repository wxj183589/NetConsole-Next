using NetConsole.Next.Service;

internal static class SmartConfigurationTests
{
    public static void Run()
    {
        var service = new SmartConfigurationService();
        var actor = new WebUserIdentity("admin", "admin", "SUPERADMIN", "session");
        var templates = service.ListTemplates();
        Assert(templates.Count == 2 && templates.Any(item => item.Id == "h3c-access-vlan"),
            "smart configuration templates should be discoverable");

        var preview = service.Preview(new SmartConfigurationPreviewRequest("h3c-access-vlan",
            new Dictionary<string, string> { ["interface"] = "GigabitEthernet1/0/1", ["vlanId"] = "120", ["description"] = "设备管理" }, "site-a"),
            actor, P2B5SiteScopeDecision.Global);
        Assert(preview.State == "PREVIEWED" && preview.ExecutionMode == "DRY_RUN_ONLY" &&
            !preview.DeviceWriteAllowed && !preview.WriteAttempted &&
            preview.Commands.Any(item => item.Command == "port access vlan 120"),
            "valid preview must return commands and remain write-disabled");

        ExpectCode(() => service.Preview(new("h3c-access-vlan",
            new Dictionary<string, string> { ["interface"] = "GigabitEthernet1/0/1\nquit", ["vlanId"] = "120" }),
            actor, P2B5SiteScopeDecision.Global), "SMART_CONFIG_VARIABLES_INVALID");
        ExpectCode(() => service.Preview(new("h3c-access-vlan",
            new Dictionary<string, string> { ["interface"] = "GigabitEthernet1/0/1", ["vlanId"] = "4095" }),
            actor, P2B5SiteScopeDecision.Global), "SMART_CONFIG_VARIABLES_INVALID");
        ExpectCode(() => service.Preview(new("h3c-access-vlan", new Dictionary<string, string>(), "site-a"),
            new WebUserIdentity("operator", "operator", "ADMIN", "session"), P2B5SiteScopeDecision.Global),
            "SMART_CONFIG_PERMISSION_DENIED");
        ExpectCode(() => service.Preview(new("h3c-access-vlan",
            new Dictionary<string, string> { ["interface"] = "GigabitEthernet1/0/1", ["vlanId"] = "120" }, "site-b"),
            actor, new("AUTHORIZED", false, new HashSet<string> { "site-a" }, "fixture")),
            "SMART_CONFIG_SITE_SCOPE_DENIED");

        Console.WriteLine("SMART_CONFIG_TESTS=5/5 PASS");
    }

    private static void ExpectCode(Action action, string expected)
    {
        try { action(); }
        catch (SmartConfigurationValidationException ex) when (ex.Code == expected) { return; }
        throw new InvalidOperationException($"expected {expected}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
