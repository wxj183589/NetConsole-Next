using System.Globalization;
using System.Text;

namespace NetConsole.Next.Service;

internal sealed class TaskExportService
{
    private const int MaximumRows = 100;

    public TaskExportResult CreateCsv(Catalog catalog, P2B5SiteScopeDecision? scope)
    {
        return CreateCsv(catalog.RecentTaskRuns(MaximumRows, scope));
    }

    internal TaskExportResult CreateCsv(IReadOnlyList<TaskRunSnapshot> rows)
    {
        var builder = new StringBuilder();
        builder.Append('\uFEFF');
        AppendRow(builder, "任务编号", "任务类型", "状态", "排队时间", "开始时间", "结束时间", "失败原因", "Raw 状态", "解析状态");
        foreach (var row in rows.Take(MaximumRows))
        {
            AppendRow(builder, row.Id, row.TaskType, row.State, FormatTime(row.QueuedMs),
                FormatTime(row.StartedMs), FormatTime(row.EndedMs), row.FailureReason ?? string.Empty,
                row.CaptureStatus ?? string.Empty, row.ParseStatus ?? string.Empty);
        }
        return new(Encoding.UTF8.GetBytes(builder.ToString()), $"tasks-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    private static string FormatTime(long? value) => value is null
        ? string.Empty
        : DateTimeOffset.FromUnixTimeMilliseconds(value.Value).ToString("O", CultureInfo.InvariantCulture);

    private static void AppendRow(StringBuilder builder, params string[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0) builder.Append(',');
            var value = values[index] ?? string.Empty;
            builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        builder.Append("\r\n");
    }
}

internal sealed record TaskExportResult(byte[] Bytes, string Name);
