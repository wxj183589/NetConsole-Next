using System.Text.Json;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class R67LiveParser
{
    public static void Test()
    {
        const string output = "<AC1>display wlan ap all\nAP name APID State Model Serial ID\n" +
            "4c48-cb6e-0400 1 R/M WA6320-HCL H3C_4C-48-CB-6E-04-00\n" +
            "other 2 R/B WA6320-HCL serial-2 default-group 12 days\n<AC1>";
        var rows = H3cComwareParser.ParseWlanApAll(output);
        if (rows.Count != 2 || rows[0].State != "R/M" || rows[0].Serial != "H3C_4C-48-CB-6E-04-00" ||
            rows[0].Group is not null || rows[1].Group != "default-group" || rows[1].OnlineTime != "12 days")
            throw new InvalidOperationException("AP parser did not preserve the five-column and extended formats.");
        Console.WriteLine("R6.7 AP parser: PASS");
    }

    public static void Run(string directory)
    {
        var commands = new[] { (0, H3cCommandPolicy.DisplayVersion), (2, H3cCommandPolicy.DisplayInterfaceBrief),
            (4, H3cCommandPolicy.DisplayWlanApAll), (7, H3cCommandPolicy.DisplayWlanMeshLink),
            (8, H3cCommandPolicy.DisplayWlanApAllAddress), (10, H3cCommandPolicy.DisplayLldpNeighborInformationList) };
        var results = new List<object>();
        foreach (var device in new[] { "AC1", "AC2", "SW" })
        foreach (var (index, command) in commands)
        {
            var path = Path.Combine(directory, $"{device}-{index}.txt");
            if (!File.Exists(path)) continue;
            var parsed = H3cComwareParser.Parse(command, File.ReadAllText(path), completedExec: true);
            results.Add(new { device, command, parsed.Status, data = JsonSerializer.Deserialize<JsonElement>(parsed.Json) });
        }
        File.WriteAllText(Path.Combine(directory, "parser-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Parsed {results.Count} command outputs; see parser-results.json for statuses and data.");
    }
}
