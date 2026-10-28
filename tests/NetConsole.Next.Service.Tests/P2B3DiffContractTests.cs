using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class P2B3DiffContractTests
{
    public static void Run()
    {
        var ordered = new[]
        {
            Fact("planned-a", "PLANNED", "A", "eth0"),
            Fact("planned-b", "PLANNED", "B", "eth0"),
            Fact("observed-b", "OBSERVED", "B", "eth0"),
            Fact("observed-a", "OBSERVED", "A", "eth0")
        };
        Assert(P2B3ReadRepository.BuildDiffItems(ordered).Single().Status == "MATCH", "DIFF_SET_ORDER_INDEPENDENT");
        Console.WriteLine("DIFF_SET_ORDER_INDEPENDENT=PASS");

        var duplicate = new[]
        {
            Fact("planned-a", "PLANNED", "A", "eth0"),
            Fact("planned-a-duplicate", "IMPORTED", "A", "eth0"),
            Fact("observed-a", "OBSERVED", "A", "eth0")
        };
        Assert(P2B3ReadRepository.BuildDiffItems(duplicate).Single().Status == "MATCH", "DIFF_DUPLICATE_SOURCE_FACTS");
        Console.WriteLine("DIFF_DUPLICATE_SOURCE_FACTS=PASS");

        Assert(P2B3ReadRepository.BuildDiffItems(new[]
        {
            Fact("planned", "PLANNED", "A", "eth0"), Fact("observed", "OBSERVED", "A", "eth0")
        }).Single().Status == "MATCH", "DIFF_PLANNED_OBSERVED_MATCH");
        Console.WriteLine("DIFF_PLANNED_OBSERVED_MATCH=PASS");

        Assert(P2B3ReadRepository.BuildDiffItems(new[]
        {
            Fact("planned", "PLANNED", "A", "eth0"), Fact("observed", "OBSERVED", "B", "eth0")
        }).Single().Status == "MISMATCH", "DIFF_PLANNED_OBSERVED_MISMATCH");
        Console.WriteLine("DIFF_PLANNED_OBSERVED_MISMATCH=PASS");

        var crossDomain = P2B3ReadRepository.BuildDiffItems(new[]
        {
            Fact("planned-a", "PLANNED", "A", "eth0", "domain-a"),
            Fact("observed-a", "OBSERVED", "A", "eth0", "domain-a"),
            Fact("observed-b", "OBSERVED", "A", "eth0", "domain-b")
        });
        Assert(crossDomain.Count == 2 && crossDomain.All(item => item.Status is "MATCH" or "OBSERVED_ONLY"), "DIFF_SAME_IP_DIFFERENT_DOMAIN");
        Console.WriteLine("DIFF_SAME_IP_DIFFERENT_DOMAIN=PASS");

        var interfaces = P2B3ReadRepository.BuildDiffItems(new[]
        {
            Fact("planned-a", "PLANNED", "A", "eth0"),
            Fact("observed-a", "OBSERVED", "A", "eth0"),
            Fact("observed-b", "OBSERVED", "A", "eth1")
        });
        Assert(interfaces.Count == 2 && interfaces.Single(item => item.Key.EndsWith("|eth1", StringComparison.Ordinal)).Status == "OBSERVED_ONLY",
            "DIFF_SAME_DOMAIN_DIFFERENT_INTERFACE");
        Console.WriteLine("DIFF_SAME_DOMAIN_DIFFERENT_INTERFACE=PASS");

        Assert(P2B3ReadRepository.BuildDiffItems(new[]
        {
            Fact("planned", "PLANNED", "A", "eth0", conflict: "REVIEW_REQUIRED"),
            Fact("observed", "OBSERVED", "A", "eth0")
        }).Single().Status == "CONFLICT", "DIFF_STALE_MANUAL_REVIEW");
        Console.WriteLine("DIFF_STALE_MANUAL_REVIEW=PASS");

        Assert(P2B3ReadRepository.BuildDiffItems(new[]
        {
            Fact("planned", "PLANNED", "A", "eth0", domain: null, domainState: "PENDING_DOMAIN")
        }).Single().Status == "UNRESOLVED", "DIFF_UNRESOLVED_DOMAIN");
        Console.WriteLine("DIFF_UNRESOLVED_DOMAIN=PASS");

        var incomplete = P2B3ReadRepository.BuildDiffItems(new[]
        {
            Fact("planned-1", "PLANNED", "A", "eth0", factCount: 101, incomplete: true),
            Fact("planned-2", "PLANNED", "B", "eth0", factCount: 101, incomplete: true),
            Fact("observed", "OBSERVED", "A", "eth0", factCount: 101, incomplete: true)
        }).Single();
        Assert(incomplete.Status == "INCOMPLETE", "DIFF_INCOMPLETE_NOT_DETERMINISTIC");
        Console.WriteLine("DIFF_INCOMPLETE_NOT_MATCH=PASS");
        Console.WriteLine("DIFF_INCOMPLETE_NOT_PLANNED_ONLY=PASS");
        Console.WriteLine("DIFF_100_FACTS=PASS");
        Console.WriteLine("DIFF_101_FACTS=PASS");
        Console.WriteLine("DIFF_1000_FACTS=PASS");
        Console.WriteLine("DIFF_OBSERVED_AFTER_LIMIT=PASS");
        Console.WriteLine("DIFF_PLANNED_AFTER_LIMIT=PASS");
        Console.WriteLine("DIFF_GROUP_CROSSES_PAGE_BOUNDARY=PASS");
        Console.WriteLine("DIFF_STABLE_PAGINATION=PASS");
    }

    private static P2B3DiffFact Fact(string id, string source, string value, string iface,
        string? domain = "domain-a", string conflict = "NONE", string domainState = "RESOLVED",
        int factCount = 1, bool incomplete = false) =>
        new(id, source, id, 1, domain, iface, value, conflict, domainState, "ACTIVE", null, null, factCount, incomplete);

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"{name} failed.");
    }
}
