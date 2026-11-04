using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record H3cFactRawProvenance(int Version, string RunId, string Command,
    string OutputSha256, string SegmentSha256, long CapturedMs,
    IReadOnlyList<H3cFactRawCommandProvenance>? Commands = null);
internal sealed record H3cFactRawCommandProvenance(int Index, string Command,
    string OutputSha256, string SegmentSha256, long CapturedMs);
internal sealed record H3cTaskFactPublication(string Status, int FactCount);

internal sealed partial class Catalog
{
    // Facts are a rebuildable projection. Raw completion is committed first and
    // remains successful if projection parsing/publication needs later repair.
    public H3cTaskFactPublication PublishH3cTaskFacts(string runId)
    {
        using var db = Open();
        using (var multi = db.CreateCommand())
        {
            multi.CommandText = "SELECT COUNT(*) FROM task_run_command_outputs WHERE task_run_id=$run";
            multi.Parameters.AddWithValue("$run", runId);
            if (Convert.ToInt32(multi.ExecuteScalar()) > 0)
                return PublishMultiH3cTaskFacts(db, runId);
        }
        using var transaction = db.BeginTransaction();
        string profileId, requestJson, relativePath, commandText, outputSha, segmentSha;
        long capturedMs;
        using (var query = db.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = @"SELECT d.target,d.request_json,o.relative_path,o.command,o.sha256,o.raw_segment_sha256,o.captured_ms
                FROM task_runs t JOIN task_definitions d ON d.id=t.definition_id
                JOIN task_run_raw_outputs o ON o.task_run_id=t.id JOIN raw_segments r ON r.relative_path=o.relative_path
                JOIN h3c_device_profiles p ON p.id=d.target AND p.source_id=t.source_id
                WHERE t.id=$run AND t.task_type='H3C_SSH_READONLY' AND t.state='SUCCEEDED'
                    AND o.capture_status='COMPLETE' AND r.state='FINALIZED' AND r.record_count=1
                    AND r.task_run_id=t.id AND r.source_id=t.source_id AND r.sha256=o.raw_segment_sha256";
            query.Parameters.AddWithValue("$run", runId);
            using var reader = query.ExecuteReader();
            if (!reader.Read()) return new("NO_COMPLETE_SUCCEEDED_RAW", 0);
            profileId = reader.GetString(0); requestJson = reader.GetString(1); relativePath = reader.GetString(2);
            commandText = reader.GetString(3); outputSha = reader.GetString(4); segmentSha = reader.GetString(5); capturedMs = reader.GetInt64(6);
        }
        using var request = JsonDocument.Parse(requestJson);
        var creation = ReadTaskScope(request.RootElement);
        var target = CaptureH3cFactScope(db, transaction, profileId);
        if (target is null || creation?.Endpoint is null || creation.Version != 1 || creation.DeviceId != target.DeviceId ||
            creation.ProfileId != profileId || creation.SiteId != target.SiteId || creation.Endpoint.Host != target.Host || creation.Endpoint.Port != target.Port)
            return new("CAPTURE_SCOPE_NOT_PROVEN", 0);
        if (!TryResolveRawPath(relativePath, out var fullPath) || !File.Exists(fullPath) || ContainsReparsePoint(fullPath))
            return new("RAW_UNAVAILABLE", 0);
        var info = new FileInfo(fullPath);
        if (info.Length is < 2 or > MaximumLineBytes) throw new InvalidDataException("H3C fact Raw evidence exceeds its record bound.");
        var bytes = File.ReadAllBytes(fullPath);
        if (bytes.Length is < 2 or > MaximumLineBytes || bytes[^1] != (byte)'\n' || bytes.AsSpan(0, bytes.Length - 1).Contains((byte)'\n') ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), segmentSha, StringComparison.OrdinalIgnoreCase) ||
            !TryReadRecord(bytes.AsSpan(0, bytes.Length - 1).ToArray(), out _, out var source, out var host, out var recordRun, out var capture) ||
            source != target.SourceId || host != target.Host || recordRun != runId || capture != "COMPLETE")
            throw new InvalidDataException("H3C fact Raw evidence does not match its sealed identity.");
        using var raw = JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 1));
        var record = raw.RootElement;
        if (!H3cCommandPolicy.IsAllowed(commandText) || record.GetProperty("device_profile_id").GetString() != profileId ||
            record.GetProperty("command").GetString() != commandText || record.GetProperty("captured_ms").GetInt64() != capturedMs ||
            !string.Equals(record.GetProperty("output_sha256").GetString(), outputSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("H3C fact command evidence does not match its task manifest.");
        var output = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(record.GetProperty("output_utf8_base64").GetString()!));
        var facts = BuildFacts(profileId, new Dictionary<string, string> { [commandText] = output }, capturedMs,
            "H3C_TASK_RAW:" + runId, target, new(1, runId, commandText, outputSha, segmentSha, capturedMs));
        if (facts.Count == 0) return new("NO_SUPPORTED_FACT_PROJECTION", 0);
        WriteH3cFacts(db, transaction, facts);
        transaction.Commit();
        return new("PUBLISHED", facts.Count);
    }

    private H3cTaskFactPublication PublishMultiH3cTaskFacts(SqliteConnection db, string runId)
    {
        using var transaction = db.BeginTransaction();
        string profileId, requestJson;
        using (var query = db.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = @"SELECT d.target,d.request_json FROM task_runs t JOIN task_definitions d ON d.id=t.definition_id
                WHERE t.id=$run AND t.task_type='H3C_SSH_READONLY' AND t.state='SUCCEEDED'";
            query.Parameters.AddWithValue("$run", runId);
            using var reader = query.ExecuteReader();
            if (!reader.Read()) return new("NO_COMPLETE_SUCCEEDED_RAW", 0);
            profileId = reader.GetString(0); requestJson = reader.GetString(1);
        }
        using var request = JsonDocument.Parse(requestJson);
        var commands = ReadTaskCommands(request.RootElement);
        var scope = CaptureH3cFactScope(db, transaction, profileId);
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var provenance = new List<H3cFactRawCommandProvenance>();
        using (var query = db.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = @"SELECT o.command_index,o.command,o.relative_path,o.sha256,o.raw_segment_sha256,o.captured_ms
                FROM task_run_command_outputs o JOIN raw_segments r ON r.relative_path=o.relative_path
                WHERE o.task_run_id=$run AND o.capture_status='COMPLETE' AND r.state='FINALIZED' AND r.record_count=1
                    AND r.task_run_id=$run AND r.sha256=o.raw_segment_sha256 ORDER BY o.command_index";
            query.Parameters.AddWithValue("$run", runId);
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var index = reader.GetInt32(0); var command = reader.GetString(1); var relative = reader.GetString(2);
                if (index < 0 || index >= commands.Count || !string.Equals(commands[index], command, StringComparison.Ordinal))
                    return new("COMMAND_PLAN_MISMATCH", 0);
                if (!TryResolveRawPath(relative, out var fullPath) || !File.Exists(fullPath) || ContainsReparsePoint(fullPath))
                    return new("RAW_UNAVAILABLE", 0);
                var bytes = File.ReadAllBytes(fullPath);
                if (bytes.Length < 2 || bytes[^1] != (byte)'\n' || bytes.AsSpan(0, bytes.Length - 1).Contains((byte)'\n') ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), reader.GetString(4), StringComparison.OrdinalIgnoreCase) ||
                    !TryReadRecord(bytes.AsSpan(0, bytes.Length - 1).ToArray(), out _, out _, out _, out var recordRun, out var capture) ||
                    recordRun != runId || capture != "COMPLETE") return new("RAW_UNAVAILABLE", 0);
                using var raw = JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 1));
                var record = raw.RootElement;
                var output = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(record.GetProperty("output_utf8_base64").GetString()!));
                outputs[command] = output;
                provenance.Add(new(index, command, reader.GetString(3), reader.GetString(4), reader.GetInt64(5)));
            }
        }
        if (provenance.Count != commands.Count) return new("NO_COMPLETE_SUCCEEDED_RAW", 0);
        var capturedMs = provenance.Max(item => item.CapturedMs);
        var facts = BuildFacts(profileId, outputs, capturedMs, "H3C_TASK_RAW:" + runId, scope,
            new H3cFactRawProvenance(1, runId, "MULTI", string.Empty, string.Empty, capturedMs, provenance));
        if (facts.Count == 0) return new("NO_SUPPORTED_FACT_PROJECTION", 0);
        WriteH3cFacts(db, transaction, facts);
        transaction.Commit();
        return new("PUBLISHED", facts.Count);
    }

    private static string H3cFactRawVerificationPredicate(string alias)
    {
        string Field(string name) => $"CASE WHEN json_valid({alias}.payload_json) THEN json_extract({alias}.payload_json,'$.rawProvenance.{name}') END";
        return $@"(EXISTS(SELECT 1 FROM task_run_raw_outputs raw_o
            JOIN task_runs raw_t ON raw_t.id=raw_o.task_run_id
            JOIN task_definitions raw_d ON raw_d.id=raw_t.definition_id
            JOIN raw_segments raw_r ON raw_r.relative_path=raw_o.relative_path
            WHERE raw_o.task_run_id={Field("runId")} AND {Field("version")}=1
                AND {alias}.source='H3C_TASK_RAW:' || raw_t.id AND raw_d.target={alias}.profile_id
                AND raw_t.state='SUCCEEDED' AND raw_o.capture_status='COMPLETE'
                AND raw_o.command={Field("command")} AND raw_o.captured_ms={alias}.captured_ms AND raw_o.captured_ms={Field("capturedMs")}
                AND raw_o.sha256={Field("outputSha256")} AND raw_o.raw_segment_sha256={Field("segmentSha256")}
                AND raw_r.state='FINALIZED' AND raw_r.record_count=1 AND raw_r.task_run_id=raw_t.id
                AND raw_r.source_id=raw_t.source_id AND raw_r.sha256=raw_o.raw_segment_sha256)
            OR EXISTS(SELECT 1 FROM task_run_command_outputs multi_o
                JOIN task_runs multi_t ON multi_t.id=multi_o.task_run_id
                JOIN raw_segments multi_r ON multi_r.relative_path=multi_o.relative_path
                WHERE multi_o.task_run_id={Field("runId")} AND {Field("version")}=1
                    AND {alias}.source='H3C_TASK_RAW:' || multi_t.id
                    AND {Field("command")}='MULTI' AND multi_t.state='SUCCEEDED'
                    AND multi_o.capture_status='COMPLETE' AND multi_r.state='FINALIZED'
                    AND multi_r.record_count=1 AND multi_r.task_run_id=multi_t.id
                    AND multi_r.source_id=multi_t.source_id
                GROUP BY multi_o.task_run_id
                HAVING COUNT(*)=json_array_length({Field("commands")})))";
    }
}
