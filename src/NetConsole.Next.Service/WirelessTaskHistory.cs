using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record WirelessTaskSummary(string Id, string State, long CreatedMs, long UpdatedMs,
    int Total, int Succeeded, int Failed);
internal sealed record WirelessTaskCommandResult(string Command, string State, string? Error,
    string? CaptureStatus, string? ParseStatus, long? CapturedMs);
internal sealed record WirelessTaskItemResult(int Ordinal, string DeviceId, string State, string? Error, string? RunId,
    string? CaptureStatus, string? ParseStatus, IReadOnlyList<WirelessTaskCommandResult> Commands);
internal sealed record WirelessTaskReport(WirelessTaskSummary Summary, IReadOnlyList<WirelessTaskItemResult> Items,
    string ObservationStatus);

internal sealed partial class Catalog
{
    public IReadOnlyList<string> GetWirelessTaskPlanIds(string actorUserId, int limit)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT id FROM task_plans WHERE actor_user_id=$actor
            AND id GLOB 'wireless-plan-*' ORDER BY created_ms DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$actor", actorUserId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }
}

internal static class WirelessTaskReportBuilder
{
    public static bool IsWirelessId(string id) => id is { Length: 46 } &&
        id.StartsWith("wireless-plan-", StringComparison.Ordinal) &&
        id.AsSpan(14).ToArray().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static WirelessTaskReport Build(TaskPlanReportSnapshot report, IReadOnlyDictionary<int, string> devices)
    {
        var plan = report.Plan;
        var summary = new WirelessTaskSummary(plan.Id, plan.State, plan.CreatedMs, plan.UpdatedMs,
            plan.Items.Count, plan.Items.Count(item => item.State == "SUCCEEDED"),
            plan.Items.Count(item => item.State is "FAILED" or "TIMED_OUT" or "CANCELLED" or "INTERRUPTED"));
        var items = report.Items.Select(item => new WirelessTaskItemResult(item.Item.Ordinal,
            devices.GetValueOrDefault(item.Item.Ordinal, ""), item.Item.State,
            SafeError(item.Item.Error), item.Item.RunId, item.Run?.CaptureStatus, item.Run?.ParseStatus,
            item.Commands.Select(command => new WirelessTaskCommandResult(command.Command, command.State,
                SafeError(command.Error), command.CaptureStatus, command.ParseStatus, command.CapturedMs)).ToArray())).ToArray();
        return new(summary, items, "NOT_PUBLISHED");
    }

    private static string? SafeError(string? error) => string.IsNullOrEmpty(error) ? null : "EXECUTION_FAILED";

    public static byte[] Export(WirelessTaskReport report)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var files = new Dictionary<string, byte[]>
        {
            ["task-summary.json"] = JsonSerializer.SerializeToUtf8Bytes(report.Summary, options),
            ["ac-execution-results.json"] = JsonSerializer.SerializeToUtf8Bytes(report.Items, options),
            ["ap-resources.json"] = JsonSerializer.SerializeToUtf8Bytes(new { status = report.ObservationStatus, items = Array.Empty<object>() }, options),
            ["ap-observations.json"] = JsonSerializer.SerializeToUtf8Bytes(new { status = report.ObservationStatus, items = Array.Empty<object>() }, options),
            ["parser-summary.json"] = JsonSerializer.SerializeToUtf8Bytes(report.Items.Select(item => new {
                item.DeviceId, item.RunId, item.ParseStatus,
                commands = item.Commands.Select(command => new { command.Command, command.ParseStatus })
            }), options),
            ["failure-details.json"] = JsonSerializer.SerializeToUtf8Bytes(report.Items
                .Where(item => item.Error is not null || item.Commands.Any(command => command.Error is not null))
                .Select(item => new { item.DeviceId, item.State, item.Error,
                    commands = item.Commands.Where(command => command.Error is not null).Select(command => new { command.Command, command.State, command.Error }) }), options)
        };
        var manifest = new { version = 2, taskId = report.Summary.Id, generatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            source = "TASK_PLAN_REPORT", observationStatus = report.ObservationStatus,
            files = files.Select(file => new { name = file.Key, bytes = file.Value.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant() }).ToArray() };
        files["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, options);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Key, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(file.Value);
            }
        return output.ToArray();
    }
}
