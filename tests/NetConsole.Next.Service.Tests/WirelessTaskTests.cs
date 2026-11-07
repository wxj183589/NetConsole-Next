namespace NetConsole.Next.Service.Tests;

internal static class WirelessTaskTests
{
    public static void Run()
    {
        var request = new WirelessTaskCreateRequest(
            new[] { "domain-1" },
            new[] { "display version", "display wlan ap all" }, 30_000);
        Assert(request.DomainIds!.Single() == "domain-1" && request.Commands!.Count == 2 && request.TimeoutMs == 30_000,
            "wireless task request keeps domain scope and read-only command plan");
        Assert(H3cCommandPolicy.IsAllowed("display wlan ap all") && !H3cCommandPolicy.IsAllowed("system-view"),
            "wireless tasks must use the existing read-only command allowlist");
        Assert(WirelessTaskService.IsSupportedDeviceType(" ac ") &&
            !WirelessTaskService.IsSupportedDeviceType("FIT_AP") &&
            !WirelessTaskService.IsSupportedDeviceType("SWITCH") &&
            !WirelessTaskService.IsSupportedDeviceType("NETWORK_DEVICE"),
            "wireless tasks require explicitly classified AC inventory targets");
        Console.WriteLine("WIRELESS_TASK_CONTRACT_TESTS=3/3 PASS");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
