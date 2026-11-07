using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

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
        VerifyReportExport();
        Console.WriteLine("WIRELESS_TASK_CONTRACT_TESTS=4/4 PASS");
    }

    private static void VerifyReportExport()
    {
        var id = "wireless-plan-" + new string('a', 32);
        Assert(WirelessTaskReportBuilder.IsWirelessId(id) && !WirelessTaskReportBuilder.IsWirelessId("wireless-plan-not-valid"), "wireless identifiers must be exact");
        var item = new TaskPlanItemSnapshot(0, "profile-secret", "definition", "run", "FAILED", "password-secret C:\\private");
        var plan = new TaskPlanSnapshot(id, "H3C_SSH_READONLY", "FAILED", "actor", ["display version"], "claims", 10, 20, [item]);
        var command = new TaskCommandSnapshot(0, "display version", "FAILED", "password-secret", "C:\\private", "FAILED", null, 12);
        var report = WirelessTaskReportBuilder.Build(new(plan, [new(item, null, [command])]), new Dictionary<int, string> { [0] = "ac-id" });
        using var archive = new ZipArchive(new MemoryStream(WirelessTaskReportBuilder.Export(report)), ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json")!;
        using var manifestStream = manifestEntry.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        Assert(manifest.RootElement.GetProperty("generatedMs").GetInt64() > 0 &&
            manifest.RootElement.GetProperty("source").GetString() == "TASK_PLAN_REPORT", "manifest identifies generation and source");
        foreach (var name in new[] { "task-summary.json", "ac-execution-results.json", "ap-resources.json", "ap-observations.json", "parser-summary.json", "failure-details.json" })
            Assert(archive.GetEntry(name) is not null, "export includes " + name);
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var entry = archive.GetEntry(file.GetProperty("name").GetString()!)!;
            using var input = entry.Open(); using var bytes = new MemoryStream(); input.CopyTo(bytes);
            var text = Encoding.UTF8.GetString(bytes.ToArray());
            Assert(!text.Contains("password-secret") && !text.Contains("profile-secret") && !text.Contains("C:\\private"), "export excludes secrets and paths");
            Assert(Convert.ToHexString(SHA256.HashData(bytes.ToArray())).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "manifest digest matches file");
        }
        Assert(report.Items.Single().DeviceId == "ac-id" && report.ObservationStatus == "NOT_PUBLISHED", "report retains device identity and truthful observation status");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
