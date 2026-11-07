using System.Text;

namespace NetConsole.Next.Service.Tests;

internal static class TaskExportTests
{
    public static void Run()
    {
        var service = new TaskExportService();
        var result = service.CreateCsv(new[]
        {
            new TaskRunSnapshot("run-1", "source-1", "H3C_SSH_READONLY", "FAILED", 1_700_000_000_000,
                1_700_000_000_100, null, 1_700_000_000_200, "命令,失败\"原因", "definition-1", "/never-export-path",
                "secret-sha", 12, "{}", "FAILED", "FAILED")
        });
        var csv = Encoding.UTF8.GetString(result.Bytes);
        Assert(csv.StartsWith("\uFEFF\"任务编号\"", StringComparison.Ordinal), "CSV must include UTF-8 BOM and headers");
        Assert(csv.Contains("\"命令,失败\"\"原因\"", StringComparison.Ordinal), "CSV must escape commas and quotes");
        Assert(!csv.Contains("never-export-path", StringComparison.Ordinal) && !csv.Contains("secret-sha", StringComparison.Ordinal),
            "CSV must not expose raw paths or output hashes");
        Assert(result.Name.EndsWith(".csv", StringComparison.Ordinal), "CSV download must use a csv name");
        Console.WriteLine("TASK_EXPORT_TESTS=4/4 PASS");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
