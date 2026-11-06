using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record TaskDiagnosticCommand(int Index, string Command, string Availability, bool CanDownload,
    string? CaptureStatus = null, string? ParseStatus = null, long? CapturedMs = null, long? ByteCount = null,
    string? OutputSha256 = null, string? SegmentSha256 = null, long? DownloadByteCount = null,
    string? DownloadSha256 = null, bool Redacted = false);
internal sealed record TaskDiagnosticManifest(int Version, string RunId, string TaskState, string Status,
    bool CanDownload, IReadOnlyList<TaskDiagnosticCommand> Commands);
internal sealed record TaskDiagnosticAccess(TaskScopeSnapshot Scope, string SourceId, string DefinitionId, string PlanSha256);
internal sealed record TaskDiagnosticDownload(byte[] Bytes, string Name, string ContentType, TaskDiagnosticAccess Access);
internal sealed class TaskDiagnosticException(string code, string detail, int statusCode = 409) : InvalidOperationException(detail)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public IResult Result() => Results.Json(new { error = Code, detail = Message }, statusCode: StatusCode);
}

// An on-demand projection of sealed command evidence. It neither collects device
// data nor creates files, jobs, schemas, credentials or a second Raw authority.
internal sealed class TaskDiagnosticEvidenceService(Catalog catalog, P2B5SiteScopeStore scopes,
    InteractiveScopeAuthorizer principals)
{
    public TaskDiagnosticManifest? Manifest(WebUserIdentity original, string runId, CancellationToken cancellationToken)
    {
        var scope = CurrentScope(original);
        if (scope is null) return null;
        var evidence = catalog.ReadTaskDiagnosticEvidence(runId, scope, cancellationToken);
        if (evidence is null || !CanRead(original, runId, evidence.Access)) return null;
        return evidence.Manifest;
    }

    public TaskDiagnosticDownload? Download(WebUserIdentity original, string runId, int? commandIndex,
        CancellationToken cancellationToken)
    {
        var scope = CurrentScope(original);
        if (scope is null) return null;
        var evidence = catalog.ReadTaskDiagnosticEvidence(runId, scope, cancellationToken);
        if (evidence is null) return null;
        if (!CanRead(original, runId, evidence.Access)) return null;
        if (commandIndex is < 0 || commandIndex >= evidence.Manifest.Commands.Count)
            throw new TaskDiagnosticException("INVALID_COMMAND_INDEX", "命令序号无效。", 400);
        var selected = commandIndex.HasValue ? [evidence.Manifest.Commands[commandIndex.Value]] : evidence.Manifest.Commands;
        if (evidence.Manifest.TaskState is not ("SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED"))
            throw new TaskDiagnosticException("DIAGNOSTIC_TASK_ACTIVE", "任务尚未结束，请稍后下载。");
        foreach (var command in selected)
        {
            if (command.Availability == "RAW_INVALID")
            {
                var code = evidence.Errors.GetValueOrDefault(command.Index) ?? "DIAGNOSTIC_RAW_INVALID";
                throw new TaskDiagnosticException(code, "诊断证据未通过完整性校验，无法下载。", code == "DIAGNOSTIC_SIZE_LIMIT" ? 413 : 409);
            }
            if (commandIndex.HasValue && command.Availability is "CONFIG_EXCLUDED" or "UNSUPPORTED_COMMAND")
                throw new TaskDiagnosticException("DIAGNOSTIC_COMMAND_EXCLUDED", "该命令不属于可下载的诊断内容。");
            if (commandIndex.HasValue && !command.CanDownload)
                throw new TaskDiagnosticException("DIAGNOSTIC_NO_RAW", "该命令尚无封存的诊断证据。");
        }
        if (!selected.Any(item => item.CanDownload))
            throw new TaskDiagnosticException("DIAGNOSTIC_NO_RAW", "该任务尚无可下载的诊断证据。");

        byte[] bytes;
        if (commandIndex.HasValue) bytes = evidence.Outputs[commandIndex.Value];
        else
        {
            // All members have been verified before the first HTTP byte is sent.
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Fastest);
                using (var stream = manifest.Open())
                    JsonSerializer.Serialize(stream, evidence.Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                foreach (var command in selected.Where(item => item.CanDownload))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry($"command-{command.Index + 1:000}.txt", CompressionLevel.Fastest);
                    using var stream = entry.Open();
                    stream.Write(evidence.Outputs[command.Index]);
                }
            }
            if (buffer.Length > Catalog.MaximumDiagnosticDownloadBytes)
                throw new TaskDiagnosticException("DIAGNOSTIC_SIZE_LIMIT", "诊断包超过大小上限。", 413);
            bytes = buffer.ToArray();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRead(original, runId, evidence.Access)) return null;
        return new(bytes, commandIndex.HasValue ? $"diagnostics-{runId}-command-{commandIndex.Value + 1:000}.txt" : $"diagnostics-{runId}.zip",
            commandIndex.HasValue ? "text/plain; charset=utf-8" : "application/zip", evidence.Access);
    }

    public bool CanRead(WebUserIdentity original, string runId, TaskDiagnosticAccess expected)
    {
        try
        {
            var scope = CurrentScope(original);
            return scope is not null && catalog.ReadTaskDiagnosticAccess(runId, scope)?.Access == expected;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return false; }
    }

    private P2B5SiteScopeDecision? CurrentScope(WebUserIdentity original)
    {
        var current = principals.Current(original);
        if (current is null || current.Role is not ("SUPERADMIN" or "ADMIN")) return null;
        var scope = scopes.Resolve(current);
        return scope.IsAuthorized ? scope : null;
    }
}

internal sealed class TaskDiagnosticDownloadResult(TaskDiagnosticDownload download, Func<bool> isAuthorized) : IResult
{
    internal const int ChunkBytes = 64 * 1024;
    private TaskDiagnosticDownload? _download = download;

    public async Task ExecuteAsync(HttpContext context)
    {
        try
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            if (!isAuthorized())
            {
                await new TaskDiagnosticException("DIAGNOSTIC_SCOPE_REVOKED", "当前权限或设备归属已变化，请刷新后重试。").Result().ExecuteAsync(context);
                return;
            }
            var item = _download!;
            context.Response.ContentType = item.ContentType;
            context.Response.ContentLength = item.Bytes.LongLength;
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{item.Name}\"";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            for (var offset = 0; offset < item.Bytes.Length; offset += ChunkBytes)
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                if (!isAuthorized()) { context.Abort(); return; }
                await context.Response.Body.WriteAsync(item.Bytes.AsMemory(offset, Math.Min(ChunkBytes, item.Bytes.Length - offset)), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        finally { _download = null; }
    }
}

internal sealed record TaskDiagnosticEvidence(TaskDiagnosticManifest Manifest, TaskDiagnosticAccess Access,
    IReadOnlyDictionary<int, byte[]> Outputs, IReadOnlyDictionary<int, string> Errors);
internal sealed record TaskDiagnosticTarget(TaskRunSnapshot Run, TaskDefinitionSnapshot Definition, TaskDiagnosticAccess Access);

internal sealed partial class Catalog
{
    // 32 commands * 256 KiB plus a bounded manifest and ZIP headers.
    internal const int MaximumDiagnosticDownloadBytes = 8 * 1024 * 1024 + 128 * 1024;
    private static readonly Regex DiagnosticSecretLine = new(
        @"^.*\b(?:password|passwd|passphrase|pass-phrase|pre-shared-key|community|private-key|secret|credential|authorization|token|psk)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex DiagnosticPrivateKey = new(@"-----BEGIN [^\r\n]*PRIVATE KEY-----[\s\S]*?(?:-----END [^\r\n]*PRIVATE KEY-----|\z)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal TaskDiagnosticTarget? ReadTaskDiagnosticAccess(string runId, P2B5SiteScopeDecision scope)
    {
        try { return ReadTaskDiagnosticAccessCore(runId, scope); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { return null; }
    }

    private TaskDiagnosticTarget? ReadTaskDiagnosticAccessCore(string runId, P2B5SiteScopeDecision scope)
    {
        if (!Guid.TryParseExact(runId, "N", out _) || !scope.IsAuthorized) return null;
        var run = GetTaskRun(runId, scope);
        if (run?.TaskType != "H3C_SSH_READONLY" || run.DefinitionId is null) return null;
        var definition = GetTaskDefinition(run.DefinitionId);
        if (definition is null || definition.TaskType != run.TaskType || definition.ScopeSnapshot is not { Version: 1, Endpoint: not null } frozen)
            return null;
        var plan = definition.CommandPlan;
        if (plan.Count is < 1 or > 32 || plan.Distinct(StringComparer.Ordinal).Count() != plan.Count) return null;
        var target = GetTaskScopeTarget(definition.Target, scope);
        var profile = GetH3cDeviceProfile(definition.Target);
        if (target is null || profile is null || frozen.ProfileId != definition.Target || frozen.DeviceId != target.Id ||
            frozen.SiteId != target.SiteId || !DeviceSiteScope.Allows(scope, frozen.SiteId) || run.SourceId != profile.SourceId ||
            frozen.Endpoint.Host != profile.Host || frozen.Endpoint.Port != profile.Port ||
            target.ManagementAddress != profile.Host || target.Port != profile.Port) return null;
        var planSha = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan))).ToLowerInvariant();
        return new(run, definition, new(frozen, profile.SourceId, definition.Id, planSha));
    }

    internal TaskDiagnosticEvidence? ReadTaskDiagnosticEvidence(string runId, P2B5SiteScopeDecision scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ReadTaskDiagnosticAccess(runId, scope);
        if (target is null) return null;
        var plan = target.Definition.CommandPlan;
        var rows = ReadDiagnosticRows(runId);
        var invalidLedger = rows.Any(row => row.Index < 0 || row.Index >= plan.Count || row.Command != plan[row.Index]) ||
            rows.Select(row => row.Index).Distinct().Count() != rows.Count;
        var outputs = new Dictionary<int, byte[]>();
        var errors = new Dictionary<int, string>();
        var commands = new List<TaskDiagnosticCommand>(plan.Count);
        var terminal = target.Run.State is "SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED";
        for (var index = 0; index < plan.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = plan[index];
            var availability = H3cCommandPolicy.IsConfigurationCommand(command) ? "CONFIG_EXCLUDED" :
                !IsDiagnosticCommand(command) ? "UNSUPPORTED_COMMAND" : !terminal ? "TASK_ACTIVE" :
                invalidLedger ? "RAW_INVALID" : rows.All(item => item.Index != index) ? "NO_RAW" : "READY";
            var safeCommand = H3cCommandPolicy.IsAllowed(command) ? command : "（不支持的命令）";
            if (availability != "READY")
            {
                commands.Add(new(index, safeCommand, availability, false));
                continue;
            }
            var row = rows.Single(item => item.Index == index);
            try
            {
                var rawOutput = ReadDiagnosticOutput(row, runId, target.Access, cancellationToken);
                var text = new UTF8Encoding(false, true).GetString(rawOutput);
                var sanitized = DiagnosticSecretLine.Replace(DiagnosticPrivateKey.Replace(text, "<REDACTED>"), "<REDACTED>");
                var bytes = Encoding.UTF8.GetBytes(sanitized);
                if (bytes.Length > H3cRawOutput.MaximumBytesFor(command))
                    throw new TaskDiagnosticException("DIAGNOSTIC_SIZE_LIMIT", "脱敏后的诊断内容超过大小上限。", 413);
                outputs[index] = bytes;
                commands.Add(new(index, command, "READY", true, row.Capture, row.Parse, row.CapturedMs, row.Bytes,
                    row.OutputSha.ToLowerInvariant(), row.SegmentSha.ToLowerInvariant(), bytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), sanitized != text));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or
                FormatException or DecoderFallbackException or RegexMatchTimeoutException or ArgumentException or KeyNotFoundException)
            {
                commands.Add(new(index, command, "RAW_INVALID", false));
                errors[index] = ex is TaskDiagnosticException diagnostic ? diagnostic.Code : "DIAGNOSTIC_RAW_INVALID";
            }
        }
        var ready = commands.Any(item => item.CanDownload);
        var status = !ready ? "UNAVAILABLE" : target.Run.State == "SUCCEEDED" && commands.All(item => item.CanDownload && item.CaptureStatus == "COMPLETE") ? "COMPLETE" : "PARTIAL";
        return new(new(1, runId, target.Run.State, status, ready && commands.All(item => item.Availability != "RAW_INVALID"), commands), target.Access, outputs, errors);
    }

    private static bool IsDiagnosticCommand(string command) => command is
        H3cCommandPolicy.DisplayVersion or H3cCommandPolicy.DisplayInterfaceBrief or H3cCommandPolicy.DisplayInterface or
        H3cCommandPolicy.DisplayDevice or H3cCommandPolicy.DisplayVlanAll or H3cCommandPolicy.DisplayWlanApAll or
        H3cCommandPolicy.DisplayWlanApAllAddress or H3cCommandPolicy.DisplayWlanApAllRadio or H3cCommandPolicy.DisplayWlanMeshLink or
        H3cCommandPolicy.DisplayWlanClient or H3cCommandPolicy.DisplayLldpNeighborInformationList or
        H3cCommandPolicy.DisplayLldpNeighborInformationVerbose or H3cCommandPolicy.DisplayTransceiverDiagnosisInterface or
        H3cDiagnosticCapture.Command;

    private sealed record DiagnosticRawRow(int Index, string Command, string Path, long Bytes, string OutputSha,
        string SegmentSha, long CapturedMs, string Capture, string Parse, string? SegmentState, long? SegmentRecords,
        long? SegmentBytes, string? CatalogSha, string? Source, string? Run);

    private List<DiagnosticRawRow> ReadDiagnosticRows(string runId)
    {
        using var db = Open();
        using var query = db.CreateCommand();
        query.CommandText = @"SELECT o.command_index,o.command,o.relative_path,o.byte_count,o.sha256,o.raw_segment_sha256,
                o.captured_ms,o.capture_status,o.parse_status,r.state,r.record_count,r.byte_count,r.sha256,r.source_id,r.task_run_id
            FROM (SELECT command_index,command,relative_path,byte_count,sha256,raw_segment_sha256,captured_ms,capture_status,parse_status
                FROM task_run_command_outputs WHERE task_run_id=$run
                UNION ALL SELECT 0,command,relative_path,byte_count,sha256,raw_segment_sha256,captured_ms,capture_status,parse_status
                FROM task_run_raw_outputs WHERE task_run_id=$run) o
            LEFT JOIN raw_segments r ON r.relative_path=o.relative_path ORDER BY o.command_index LIMIT 34";
        query.Parameters.AddWithValue("$run", runId);
        using var reader = query.ExecuteReader();
        var rows = new List<DiagnosticRawRow>();
        while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14)));
        return rows;
    }

    private byte[] ReadDiagnosticOutput(DiagnosticRawRow row, string runId, TaskDiagnosticAccess access, CancellationToken cancellationToken)
    {
        if (row.Bytes < 0 || row.Bytes > H3cRawOutput.MaximumBytesFor(row.Command) ||
            row.SegmentBytes < 2 || row.SegmentBytes > H3cRawOutput.MaximumRecordBytesFor(row.Command))
            throw new TaskDiagnosticException("DIAGNOSTIC_SIZE_LIMIT", "诊断证据超过大小上限。", 413);
        if (row.SegmentState != "FINALIZED" || row.SegmentRecords != 1 || row.Source != access.SourceId || row.Run != runId ||
            !IsSha256(row.OutputSha) || !IsSha256(row.SegmentSha) || !string.Equals(row.SegmentSha, row.CatalogSha, StringComparison.OrdinalIgnoreCase) ||
            row.Capture is not ("COMPLETE" or "PARTIAL") || row.Parse is not ("PARSED" or "EMPTY" or "UNRECOGNIZED" or "PARTIAL" or "UNCLASSIFIED") ||
            !TryResolveRawPath(row.Path, out var fullPath) || ContainsReparsePoint(fullPath))
            throw new InvalidDataException("Invalid sealed diagnostic evidence.");
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length < 2 || stream.Length > H3cRawOutput.MaximumRecordBytesFor(row.Command))
            throw new TaskDiagnosticException("DIAGNOSTIC_SIZE_LIMIT", "诊断证据超过大小上限。", 413);
        if (stream.Length != row.SegmentBytes) throw new InvalidDataException("Diagnostic record length differs from its sealed manifest.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes[^1] != (byte)'\n' || bytes.AsSpan(0, bytes.Length - 1).Contains((byte)'\n') ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), row.SegmentSha, StringComparison.OrdinalIgnoreCase) ||
            !TryReadRecord(bytes[..^1], out var captured, out var source, out var host, out var recordRun, out var capture) ||
            captured != row.CapturedMs || source != access.SourceId || host != access.Scope.Endpoint!.Host || recordRun != runId || capture != row.Capture)
            throw new InvalidDataException("Diagnostic record does not match its sealed identity.");
        using var raw = JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 1));
        var record = raw.RootElement;
        if (record.GetProperty("device_profile_id").GetString() != access.Scope.ProfileId ||
            record.GetProperty("command").GetString() != row.Command || record.GetProperty("output_byte_count").GetInt64() != row.Bytes ||
            record.GetProperty("capture_status").GetString() != row.Capture ||
            !string.Equals(record.GetProperty("output_sha256").GetString(), row.OutputSha, StringComparison.OrdinalIgnoreCase) ||
            (record.TryGetProperty("command_index", out var index) && (!index.TryGetInt32(out var rawIndex) || rawIndex != row.Index)))
            throw new InvalidDataException("Diagnostic command does not match its immutable plan.");
        return record.GetProperty("output_utf8_base64").GetBytesFromBase64();
    }
}
