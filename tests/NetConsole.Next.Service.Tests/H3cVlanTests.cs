using NetConsole.Next.Service;

internal static class H3cVlanTests
{
    // Synthetic Comware detail-format fixture, never evidence of a real device.
    internal static string Block(int id, string tagged = "None", string untagged = "None") => $"""
        VLAN ID: {id}
        VLAN type: Static
        Route interface: Not configured
        Description: 运维 VLAN {id}
        Name: VLAN {id}
        Tagged ports:
          {tagged}
        Untagged ports:
          {untagged}
        """;

    internal static string Transcript(string body, bool closed = true) =>
        "<fixture>display vlan all\n" + body + (closed ? "\n<fixture>" : "");

    public static void Run()
    {
        var result = H3cComwareParser.ParseVlans(Transcript(Block(30, "GigabitEthernet1/0/1\n  GigabitEthernet1/0/2") + "\n" + Block(1)));
        Assert(result.Status == "PARSED" && result.Vlans.Select(row => row.VlanId).SequenceEqual(new[] { 1, 30 }) &&
            result.Vlans[1].TaggedPorts.SequenceEqual(new[] { "GigabitEthernet1/0/1", "GigabitEthernet1/0/2" }) &&
            result.Vlans[0].UntaggedPorts.Count == 0 && result.Vlans[0].Description == "运维 VLAN 1" &&
            result.Vlans[0].RouteInterface == false, "complete blocks preserve Unicode, wrapped members and explicit no-members");

        var variants = Transcript(Block(100, "GE1/0/1 to GE1/0/4", "Bridge-Aggregation10"))
            .Replace("VLAN type", "VLAN Type").Replace("Tagged ports", "Tagged Ports")
            .Replace("Route interface: Not configured", "Route interface: Configured");
        result = H3cComwareParser.ParseVlans("\x1b[32m" + variants + "\x1b[0m");
        Assert(result.Status == "PARSED" && result.Vlans.Single().TaggedPorts.Single() == "GE1/0/1 to GE1/0/4" &&
            result.Vlans.Single().RouteInterface == true, "case/ANSI variants preserve range expressions without inventing PVIDs or interface expansion");
        var routed = Block(3).Replace("Route interface: Not configured", """
            Private VLAN type: Primary
            Route interface: Configured
            IPv4 address: 1.1.1.1
            IPv4 subnet mask: 255.255.255.0
            IPv6 global unicast addresses:
              2001::1, subnet is 2001::/64 [TENTATIVE]
              2001:db8::1, subnet is 2001:db8::/64
            """);
        result = H3cComwareParser.ParseVlans(Transcript(routed));
        Assert(result.Status == "PARSED" && result.Vlans.Single().Ipv4Address == "1.1.1.1" &&
            result.Vlans.Single().Ipv6Addresses.Count == 2 && result.Vlans.Single().TaggedPorts.Count == 0,
            "documented optional routing/private-VLAN fields remain separate from port membership");
        result = H3cComwareParser.ParseVlans(Transcript(Block(1)
            .Replace("Name: VLAN 1", "Name: invalid command")
            .Replace("Description: 运维 VLAN 1", "Description: Permission denied test VLAN ---- More ----")));
        Assert(result.Status == "PARSED" && result.Vlans.Single().Name == "invalid command",
            "free-text names and descriptions are not standalone CLI diagnostics or paging prompts");

        foreach (var body in new[] { Block(0), Block(4095), Block(1) + "\n" + Block(1),
            Block(1).Replace("Name: VLAN 1\n", ""), Block(1).Replace("Untagged ports:\n  None", "Untagged ports:"),
            Block(1).Replace("Name: VLAN 1", "Name: VLAN 1\nName: duplicate"),
            Block(1) + "\nUnexpected truncated tail", Block(1, "None GigabitEthernet1/0/1"),
            Block(1) + "\n IPv6 global unicast addresses:\n    unexpected truncated tail",
            Block(1) + "\n IPv6 global unicast addresses:\n    VLAN ID: not-a-number",
            Block(1) + "\n IPv6 global unicast addresses:\n    2001::1, subnet is 2001::/129",
            Block(1, "GigabitEthernet1/0/1 GigabitEthernet1/0/1") })
        {
            result = H3cComwareParser.ParseVlans(Transcript(body));
            Assert(result.Status == "PARTIAL" && result.Vlans.Count == 0,
                "invalid/duplicate/missing fields and malformed members cannot publish a plausible partial table");
        }

        foreach (var body in new[] { "% Unrecognized command found at '^' position.", "Permission denied", "% Error: Permission denied", "Error: invalid command" })
        {
            result = H3cComwareParser.ParseVlans(Transcript(body));
            Assert(result.Status == "UNRECOGNIZED" && result.Vlans.Count == 0, "command and authorization failures cannot become valid VLANs");
        }
        Assert(H3cComwareParser.ParseVlans(Transcript("Total VLANs: 1\nThe VLANs include:\n1")).Status == "UNRECOGNIZED",
            "summary IDs are not a detailed VLAN table");

        Assert(H3cComwareParser.ParseVlans(Transcript(Block(1) + "\n---- More ----")).Status == "PARTIAL" &&
            H3cComwareParser.ParseVlans(Transcript(Block(1) + " ---- More ----")).Status == "PARTIAL" &&
            H3cComwareParser.ParseVlans(Transcript(Block(1), closed: false)).Status == "PARTIAL" &&
            H3cComwareParser.ParseVlans("").Status == "EMPTY" &&
            H3cComwareParser.ParseVlans(Transcript("")).Status == "EMPTY",
            "paging, missing completion boundary and genuinely empty output remain distinct");
        var pagedInterfaces = "DEVICE#show interface brief\nInterface Attribute Mode BW Admin Phy Prot\n" +
            "gei-0/1/0/1 physical normal 1G up up up ---- More ----\nDEVICE#";
        Assert(CliTranscriptParser.Parse(pagedInterfaces, ZteCommandPolicy.ShowInterfaceBrief).Status == CliTranscriptStatus.Paged &&
            ZteZxr10Parser.ParseInterfaces(pagedInterfaces).Status == "PAGED",
            "shared paging detection retains inline table markers for other vendors even with a closing prompt");
        Assert(H3cComwareParser.ParseVlans(Block(1), completedExec: true).Status == "PARSED" &&
            H3cComwareParser.ParseVlans(Block(1)).Status == "PARTIAL",
            "transport completion proof admits stripped Netmiko output without pretending an unproven offline fragment is complete");
        Assert(H3cComwareParser.ParseVlans(Transcript(Block(1)) + "\n<fixture>display version\n<fixture>").Status == "PARTIAL",
            "additional command boundaries cannot be hidden by stripped terminal prompts");
        foreach (var completedExec in new[] { false, true })
            foreach (var tail in new[] { "\n" + Block(2), "\n" + Block(2) + "\n<fixture>" })
                Assert(H3cComwareParser.ParseVlans(Transcript(Block(1)) + tail, completedExec).Status == "PARTIAL",
                    "body after a closing prompt cannot be merged into a completed table, even with transport completion proof");
        Assert(H3cComwareParser.ParseVlans(Transcript(Block(1)) + "\n\n<fixture>\n").Status == "PARSED",
            "trailing blank lines and repeated bare prompts do not invent an extra command body");
        Assert(H3cCommandPolicy.IsAllowed(H3cCommandPolicy.DisplayVlanAll) &&
            !H3cCommandPolicy.IsAllowed("vlan 100") && !H3cCommandPolicy.IsAllowed("display vlan all;save") &&
            H3cComwareParser.Parse(H3cCommandPolicy.DisplayVlanAll, Transcript(Block(1))).Status == "PARSED",
            "the new operation uses the exact read-only allowlist and existing parser dispatch");
        Console.WriteLine("H3C_VLAN_PARSER_TESTS=6/6 PASS");
    }

    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
