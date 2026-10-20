using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Credentials;

namespace NetConsole.Next.Service;

internal sealed record RawRecoverySummary(
    int ScannedFiles,
    int RecoveredOpenFiles,
    int IndexedOrphanFiles,
    int RepairedTailFiles,
    long DiscardedTailBytes,
    int RecoveryEvidenceFiles,
    long RecoveryEvidenceBytes,
    int MissingFiles,
    int FailedFiles,
    long BytesRead,
    long BytesHashed,
    int TrustedFilesSkipped,
    long ElapsedMs,
    IReadOnlyList<RecoveryIssue> Issues);

internal sealed record RecoveryIssue(string RelativePath, string Category, bool Critical, string Detail);

internal sealed record RawCatalogCounts(int Finalized, int Incomplete, int Missing);
internal sealed record TaskRunSnapshot(string Id, string? SourceId, string TaskType, string State,
    long? QueuedMs, long? StartedMs, long? DeadlineMs, long? EndedMs, string? FailureReason, string? DefinitionId,
    string? RawOutputPath, string? RawOutputSha256, long? RawOutputBytes, string? ParsedResultJson,
    string? CaptureStatus, string? ParseStatus);
internal sealed record TaskDefinitionSnapshot(string Id, string TaskType, string Target, string Command,
    long TimeoutMs, bool Enabled, string RestartPolicy);
internal sealed record RawIntegrityAuditSnapshot(string Id, string RelativePath, long DetectedMs, string Reason,
    string? ExpectedSha256, string? ObservedSha256, long? ExpectedBytes, long? ObservedBytes, string? EvidencePath,
    string Detail, string? Disposition, string? DisposedBy, string? DispositionDetail, long? DisposedMs);
internal sealed record CatalogHealthSnapshot(RawCatalogCounts RawSegments, int IntegrityAuditCount,
    int UnresolvedIntegrityIssues,
    IReadOnlyList<RawIntegrityAuditSnapshot> RecentIntegrityAudits);
internal sealed record RawSegmentInspection(long RecordCount, long FirstReceivedMs, long LastReceivedMs,
    long ByteCount, string Sha256, bool CorruptTail, long DiscardedTailBytes, string? EvidencePath,
    long BytesRead, long BytesHashed, string SourceId = "syslog-local", string? DeviceHost = null,
    string? TaskRunId = null, string CaptureStatus = "COMPLETE");
internal sealed record RawMaintenanceAuditResult(string RelativePath, string Outcome, string Detail);

internal sealed class Catalog(RuntimeSettings settings)
{
    private const int MaximumLineBytes = 512 * 1024;
    private string DatabasePath => Path.Combine(settings.DataRoot, "catalog.db");
    public string RawRoot => Path.Combine(settings.DataRoot, "raw");
    public RawRecoverySummary RecoverySummary { get; private set; } = EmptyRecovery();
    public string IntegrityStatus { get; private set; } = "unchecked";
    public bool Available { get; private set; }
    public bool RawWriteAvailable { get; private set; }
    public string? StorageFailure { get; private set; }
    public bool RecoveryWasInterrupted { get; private set; }
    public int InterruptedTaskRuns { get; private set; }
    private string RecoveryMarkerPath => Path.Combine(settings.DataRoot, ".raw-recovery-in-progress");

    public void Initialize()
    {
        Directory.CreateDirectory(settings.DataRoot);
        if ((File.GetAttributes(settings.DataRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Data root cannot be a symbolic link or reparse point.");
        var marker = Path.Combine(settings.DataRoot, ".netconsole-next-root");
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(settings.DataRoot).Any())
                throw new InvalidOperationException("Refusing a nonempty unmarked data directory.");
            File.WriteAllText(marker, "NETCONSOLE_NEXT_V1\n");
        }
        else if (File.ReadAllText(marker).Trim() != "NETCONSOLE_NEXT_V1")
        {
            throw new InvalidOperationException("Data directory marker is unknown.");
        }
        _ = LocalApiTokenStore.GetOrCreate(settings.DataRoot);

        foreach (var directoryName in new[] { "raw", "published", "inbox", "archive", "backup" })
        {
            var directory = Path.Combine(settings.DataRoot, directoryName);
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"{directoryName} data directory cannot be a symbolic link or reparse point.");
        }
        if (File.Exists(RecoveryMarkerPath) && (File.GetAttributes(RecoveryMarkerPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Raw recovery marker cannot be a symbolic link or reparse point.");
        RecoveryWasInterrupted = File.Exists(RecoveryMarkerPath);
        using var db = Open();
        using (var journal = db.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL";
            var mode = journal.ExecuteScalar()?.ToString();
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite could not enable WAL mode for the isolated catalog.");
        }
        using (var integrity = db.CreateCommand())
        {
            integrity.CommandText = "PRAGMA quick_check(1)";
            var result = integrity.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite catalog integrity check failed; preserving the data root without starting collection.");
        }
        ApplyEmbeddedSchema(db, "schema_v1.sql");
        ApplyV2Migration(db);
        ApplyV3Migration(db);
        ApplyV4Migration(db);
        ApplyV5Migration(db);

        using (var foreignKeys = db.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using var violations = foreignKeys.ExecuteReader();
            if (violations.Read())
                throw new InvalidDataException("SQLite catalog foreign-key check failed; preserving the data root without starting collection.");
        }
        IntegrityStatus = "ok";

        // A RUNNING row after process restart did not receive a cooperative stop.
        using var interrupted = db.CreateCommand();
        interrupted.CommandText = @"UPDATE task_runs SET state='INTERRUPTED', ended_ms=$now,
            failure_reason='Process terminated before completion; automatic replay is disabled.'
            WHERE state IN ('QUEUED','CONNECTING','RUNNING')";
        interrupted.Parameters.AddWithValue("$now", UtcNowMs());
        InterruptedTaskRuns = interrupted.ExecuteNonQuery();
        Available = true;
        StorageFailure = null;
    }

    private static void ApplyEmbeddedSchema(SqliteConnection db, string name)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded {name} was not found.");
        using var reader = new StreamReader(resource);
        using var command = db.CreateCommand();
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
    }

    private static void ApplyV2Migration(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        AddColumnIfMissing(db, transaction, "raw_segments", "checkpoint_size", "INTEGER");
        AddColumnIfMissing(db, transaction, "raw_segments", "checkpoint_write_ticks", "INTEGER");
        AddColumnIfMissing(db, transaction, "raw_segments", "last_verified_ms", "INTEGER");
        AddColumnIfMissing(db, transaction, "raw_segments", "last_full_audit_ms", "INTEGER");
        AddColumnIfMissing(db, transaction, "task_runs", "definition_id", "TEXT");
        AddColumnIfMissing(db, transaction, "task_runs", "queued_ms", "INTEGER");
        AddColumnIfMissing(db, transaction, "task_runs", "deadline_ms", "INTEGER");
        AddColumnIfMissing(db, transaction, "task_runs", "failure_reason", "TEXT");
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("schema_v2.sql")
            ?? throw new InvalidOperationException("Embedded schema_v2.sql was not found.");
        using var reader = new StreamReader(resource);
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }
        using (var version = db.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "INSERT OR IGNORE INTO schema_versions(version,applied_ms) VALUES(2,$now)";
            version.Parameters.AddWithValue("$now", UtcNowMs());
            version.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void ApplyV3Migration(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("schema_v3.sql")
            ?? throw new InvalidOperationException("Embedded schema_v3.sql was not found.");
        using var reader = new StreamReader(resource);
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }
        using (var version = db.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "INSERT OR IGNORE INTO schema_versions(version,applied_ms) VALUES(3,$now)";
            version.Parameters.AddWithValue("$now", UtcNowMs());
            version.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void ApplyV4Migration(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("schema_v4.sql")
            ?? throw new InvalidOperationException("Embedded schema_v4.sql was not found.");
        using var reader = new StreamReader(resource);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ApplyV5Migration(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        AddColumnIfMissing(db, transaction, "raw_segments", "task_run_id", "TEXT REFERENCES task_runs(id)");
        AddColumnIfMissing(db, transaction, "task_run_raw_outputs", "capture_status", "TEXT NOT NULL DEFAULT 'COMPLETE'");
        AddColumnIfMissing(db, transaction, "task_run_raw_outputs", "parse_status", "TEXT NOT NULL DEFAULT 'UNCLASSIFIED'");
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("schema_v5.sql")
            ?? throw new InvalidOperationException("Embedded schema_v5.sql was not found.");
        using var reader = new StreamReader(resource);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void AddColumnIfMissing(SqliteConnection db, SqliteTransaction transaction,
        string table, string column, string type)
    {
        using var info = db.CreateCommand();
        info.Transaction = transaction;
        info.CommandText = $"PRAGMA table_info({table})";
        using (var reader = info.ExecuteReader())
        {
            while (reader.Read())
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        using var alter = db.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return db;
    }

    public RawRecoverySummary RecoverRawSegments()
    {
        var timer = Stopwatch.StartNew();
        var indexed = ReadIndexedSegments();
        int missing = 0, recoveredOpen = 0, indexedOrphans = 0, repaired = 0, failed = 0, scanned = 0, evidenceFiles = 0, trusted = 0;
        long discarded = 0, evidenceBytes = 0, bytesRead = 0, bytesHashed = 0;
        var issues = new List<RecoveryIssue>();
        if (RecoveryWasInterrupted)
            issues.Add(new(".raw-recovery-in-progress", "RECOVERY_INTERRUPTED", false,
                "The previous process stopped before completing Raw reconciliation; this startup is replaying it."));

        void AddIssue(string relative, string category, bool critical, Exception ex)
        {
            issues.Add(new(relative, category, critical, $"{ex.GetType().Name}: {ex.Message}"));
            if (critical) StorageFailure ??= $"{category}: {ex.GetType().Name}";
        }

        foreach (var row in indexed.Values)
        {
            try
            {
                if (!TryResolveRawPath(row.RelativePath, out var path) || !File.Exists(path))
                {
                    if (row.State != "MISSING") MarkMissing(row.RelativePath);
                    missing++;
                    RecordIntegrityAudit(row.RelativePath, row, null, "MISSING_RAW", "Catalog entry exists but its Raw file is missing.");
                    issues.Add(new(row.RelativePath, "MISSING_RAW", false, "Catalog entry exists but its Raw file is missing."));
                }
                else if (ContainsReparsePoint(path))
                {
                    failed++;
                    RecordIntegrityAudit(row.RelativePath, row, null, "REPARSE_POINT",
                        "Raw file or a parent directory is a symbolic link/reparse point; the file was not opened.");
                    issues.Add(new(row.RelativePath, "REPARSE_POINT", false, "Raw file or a parent directory is a symbolic link/reparse point; file was not opened."));
                }
            }
            catch (SqliteException ex)
            {
                failed++;
                AddIssue(row.RelativePath, "CATALOG_WRITE", true, ex);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failed++;
                RecordIntegrityAudit(row.RelativePath, row, null, "HISTORICAL_RAW", $"{ex.GetType().Name}: {ex.Message}");
                AddIssue(row.RelativePath, "HISTORICAL_RAW", false, ex);
            }
        }

        string[] openFiles;
        string[] finalizedFiles;
        try
        {
            openFiles = Directory.EnumerateFiles(RawRoot, "*.open", SearchOption.AllDirectories).ToArray();
            finalizedFiles = Directory.EnumerateFiles(RawRoot, "*.ndjson", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddIssue("raw", "RAW_ROOT_UNAVAILABLE", true, ex);
            RecoverySummary = new(0, 0, 0, 0, 0, 0, 0, missing, 1, 0, 0, 0,
                timer.ElapsedMilliseconds, issues);
            return RecoverySummary;
        }

        foreach (var originalPath in openFiles)
        {
            scanned++;
            var relative = RelativeRawPath(originalPath);
            try
            {
                if (ContainsReparsePoint(originalPath))
                    throw new IOException("Raw file or a parent directory is a symbolic link/reparse point.");
                var inspection = InspectAndRepair(originalPath);
                ResolveVerifiedIntegrityAudits(relative, inspection.Sha256);
                bytesRead += inspection.BytesRead;
                bytesHashed += inspection.BytesHashed;
                discarded += inspection.DiscardedTailBytes;
                var publishedPath = Path.ChangeExtension(originalPath, ".ndjson");
                if (File.Exists(publishedPath))
                    publishedPath = Path.Combine(Path.GetDirectoryName(originalPath)!, Path.GetFileNameWithoutExtension(originalPath) + ".recovered-" + Guid.NewGuid().ToString("N") + ".ndjson");
                var recoveredRelative = RelativeRawPath(publishedPath);
                if (inspection.CorruptTail)
                {
                    repaired++;
                    if (inspection.DiscardedTailBytes > 0)
                    {
                        evidenceFiles++;
                        evidenceBytes += inspection.DiscardedTailBytes;
                    }
                    RecordIntegrityAudit(recoveredRelative, null, inspection, "OPEN_TAIL_REPAIRED");
                }
                else
                {
                    RecordIntegrityAudit(recoveredRelative, null, inspection, "OPEN_SEGMENT_RECOVERED",
                        "An unsealed Raw segment was reconciled after process interruption; the original bytes were retained.");
                }

                File.Move(originalPath, publishedPath);
                relative = recoveredRelative;
                UpsertRecoveredSegment(relative, inspection, "INCOMPLETE");
                recoveredOpen++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
            {
                failed++;
                var critical = ex is SqliteException;
                if (!critical) RecordIntegrityAudit(relative, null, null, "OPEN_RECOVERY_FAILED", $"{ex.GetType().Name}: {ex.Message}");
                AddIssue(relative, critical ? "CATALOG_WRITE" : "HISTORICAL_RAW", critical, ex);
                if (critical) break;
            }
        }

        foreach (var path in finalizedFiles)
        {
            scanned++;
            var relative = RelativeRawPath(path);
            var wasIndexed = indexed.TryGetValue(relative, out var row);
            try
            {
                if (ContainsReparsePoint(path))
                    throw new IOException("Raw file or a parent directory is a symbolic link/reparse point.");
                var info = new FileInfo(path);
                if (wasIndexed && row!.State == "FINALIZED" && row.Sha256 is not null &&
                    row.CheckpointSize == info.Length && row.CheckpointWriteTicks == info.LastWriteTimeUtc.Ticks)
                {
                    if (!row.SourceId.StartsWith("h3c-", StringComparison.Ordinal) || row.HasTaskOutputManifest)
                    {
                        trusted++;
                        continue;
                    }
                }

                var inspection = InspectAndRepair(path);
                bytesRead += inspection.BytesRead;
                bytesHashed += inspection.BytesHashed;
                discarded += inspection.DiscardedTailBytes;
                if (inspection.CorruptTail)
                {
                    repaired++;
                    if (inspection.DiscardedTailBytes > 0)
                    {
                        evidenceFiles++;
                        evidenceBytes += inspection.DiscardedTailBytes;
                    }
                }

                var sameAsCatalog = wasIndexed && row!.RecordCount == inspection.RecordCount &&
                    row.ByteCount == inspection.ByteCount &&
                    string.Equals(row.Sha256, inspection.Sha256, StringComparison.OrdinalIgnoreCase);
                if (sameAsCatalog && !inspection.CorruptTail)
                    ResolveVerifiedIntegrityAudits(relative, inspection.Sha256);
                if (wasIndexed && row!.Sha256 is not null && !sameAsCatalog)
                {
                    RecordIntegrityAudit(relative, row, inspection, "CHECKSUM_OR_LENGTH_MISMATCH");
                    issues.Add(new(relative, "CHECKSUM_MISMATCH", false,
                        "Raw bytes no longer match the last catalog checksum; current bytes were retained and marked incomplete."));
                }
                if (inspection.CorruptTail)
                    RecordIntegrityAudit(relative, row, inspection, "CORRUPT_TAIL_REPAIRED");

                var state = inspection.CorruptTail || (wasIndexed && row!.State == "INCOMPLETE") ||
                    (wasIndexed && row!.Sha256 is not null && !sameAsCatalog) ? "INCOMPLETE" : "FINALIZED";
                UpsertRecoveredSegment(relative, inspection, state);
                if (!wasIndexed) indexedOrphans++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
            {
                failed++;
                var critical = ex is SqliteException;
                if (!critical) RecordIntegrityAudit(relative, wasIndexed ? row : null, null, "FINALIZED_RECOVERY_FAILED", $"{ex.GetType().Name}: {ex.Message}");
                AddIssue(relative, critical ? "CATALOG_WRITE" : "HISTORICAL_RAW", critical, ex);
                if (critical) break;
            }
        }

        try
        {
            RecoverH3cOutputManifests();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
        {
            failed++;
            var critical = ex is SqliteException;
            AddIssue("h3c-task-results", critical ? "CATALOG_WRITE" : "H3C_RESULT_RECONCILIATION", critical, ex);
        }

        timer.Stop();
        RecoverySummary = new(scanned, recoveredOpen, indexedOrphans, repaired, discarded, evidenceFiles,
            evidenceBytes, missing, failed, bytesRead, bytesHashed, trusted, timer.ElapsedMilliseconds, issues);
        return RecoverySummary;
    }

    private void RecoverH3cOutputManifests()
    {
        var candidates = new List<(string RunId, string RelativePath, string SegmentSha)>();
        using (var db = Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = @"SELECT r.task_run_id,r.relative_path,r.sha256
                FROM raw_segments r JOIN task_runs t ON t.id=r.task_run_id
                WHERE r.source_id LIKE 'h3c-%' AND r.state IN ('FINALIZED','INCOMPLETE') AND r.record_count=1
                  AND r.task_run_id IS NOT NULL
                  AND NOT EXISTS(SELECT 1 FROM task_run_raw_outputs o WHERE o.task_run_id=r.task_run_id)";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        foreach (var candidate in candidates)
        {
            if (!TryResolveRawPath(candidate.RelativePath, out var path) || !File.Exists(path) || ContainsReparsePoint(path))
                throw new IOException("H3C result Raw segment is missing or unsafe during manifest recovery.");
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is < 2 or > MaximumLineBytes || bytes[^1] != (byte)'\n' ||
                bytes.AsSpan(0, bytes.Length - 1).Contains((byte)'\n'))
                throw new InvalidDataException("H3C result Raw segment is not one complete NDJSON record.");
            var segmentSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(segmentSha, candidate.SegmentSha, StringComparison.OrdinalIgnoreCase) ||
                !TryReadRecord(bytes.AsSpan(0, bytes.Length - 1).ToArray(), out _, out var sourceId,
                    out var deviceHost, out var recordRunId, out var captureStatus) || recordRunId != candidate.RunId)
                throw new InvalidDataException("H3C result Raw record does not match its catalog identity or checksum.");

            using var document = JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 1));
            var record = document.RootElement;
            var commandText = record.GetProperty("command").GetString() ?? string.Empty;
            var profileId = record.GetProperty("device_profile_id").GetString() ?? string.Empty;
            var outputBytes = Convert.FromBase64String(record.GetProperty("output_utf8_base64").GetString() ?? string.Empty);
            var output = new System.Text.UTF8Encoding(false, true).GetString(outputBytes);
            var parse = captureStatus == "PARTIAL"
                ? new H3cParseResult("PARTIAL", "{}")
                : H3cComwareParser.Parse(commandText, output);
            var parsedAt = record.GetProperty("captured_ms").GetInt64();
            using var db = Open();
            using var transaction = db.BeginTransaction();
            using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT OR IGNORE INTO task_run_raw_outputs
                (task_run_id,relative_path,command,byte_count,sha256,raw_segment_sha256,parsed_json,captured_ms,capture_status,parse_status)
                SELECT t.id,r.relative_path,$command,$bytes,$outputSha,$segmentSha,$parsed,$captured,$capture,$parse
                FROM task_runs t JOIN task_definitions d ON d.id=t.definition_id
                JOIN h3c_device_profiles p ON p.source_id=t.source_id
                JOIN raw_segments r ON r.relative_path=$path
                WHERE t.id=$run AND t.task_type='H3C_SSH_READONLY' AND d.task_type=t.task_type
                  AND json_extract(d.request_json,'$.command')=$command AND d.target=p.id AND p.id=$profile
                  AND p.host=$host AND t.source_id=$source AND r.state IN ('FINALIZED','INCOMPLETE') AND r.record_count=1
                  AND r.task_run_id=$run AND r.sha256=$segmentSha AND r.byte_count=$segmentBytes";
            insert.Parameters.AddWithValue("$command", commandText);
            insert.Parameters.AddWithValue("$bytes", outputBytes.Length);
            insert.Parameters.AddWithValue("$outputSha", record.GetProperty("output_sha256").GetString()!);
            insert.Parameters.AddWithValue("$segmentSha", segmentSha);
            insert.Parameters.AddWithValue("$parsed", parse.Json);
            insert.Parameters.AddWithValue("$captured", parsedAt);
            insert.Parameters.AddWithValue("$capture", captureStatus);
            insert.Parameters.AddWithValue("$parse", parse.Status);
            insert.Parameters.AddWithValue("$path", candidate.RelativePath);
            insert.Parameters.AddWithValue("$run", candidate.RunId);
            insert.Parameters.AddWithValue("$profile", profileId);
            insert.Parameters.AddWithValue("$host", deviceHost ?? string.Empty);
            insert.Parameters.AddWithValue("$source", sourceId);
            insert.Parameters.AddWithValue("$segmentBytes", bytes.Length);
            if (insert.ExecuteNonQuery() != 1)
                throw new InvalidDataException("H3C Raw result did not match its persisted task definition and source.");
            transaction.Commit();
        }
    }

    public string StartTaskRun(string? sourceId, string taskType)
    {
        var id = Guid.NewGuid().ToString("N");
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO task_runs(id,source_id,task_type,state,started_ms)
            VALUES($id,$source,$type,'RUNNING',$now)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", taskType);
        command.Parameters.AddWithValue("$now", UtcNowMs());
        command.ExecuteNonQuery();
        return id;
    }

    public void ProbeRawWritable()
    {
        var probe = Path.Combine(RawRoot, ".write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1, FileOptions.WriteThrough))
            {
                stream.WriteByte(0x4e);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(probe);
            RawWriteAvailable = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            RawWriteAvailable = false;
            StorageFailure = $"RAW_WRITE_UNAVAILABLE: {ex.GetType().Name}";
            throw;
        }
    }

    public void BeginRawRecovery()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"started_ms={UtcNowMs()}\nprocess_id={Environment.ProcessId}\n");
        using var stream = new FileStream(RecoveryMarkerPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            256, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public void CompleteRawRecovery()
    {
        if (File.Exists(RecoveryMarkerPath)) File.Delete(RecoveryMarkerPath);
        RecoveryWasInterrupted = false;
    }

    public void CreateH3cDeviceProfile(H3cDeviceProfileSnapshot profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Guid.TryParseExact(profile.Id, "N", out _))
            throw new ArgumentException("H3C profile id must be a 32-character GUID.", nameof(profile));
        if (!string.Equals(profile.Id, profile.Id.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException("H3C profile id must use lowercase hexadecimal characters.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Host) || profile.Host.Length > 253 ||
            profile.Host.Any(char.IsControl) || profile.Host.Any(char.IsWhiteSpace) ||
            Uri.CheckHostName(profile.Host) == UriHostNameType.Unknown)
            throw new ArgumentException("H3C host must be a valid IP address or DNS hostname.", nameof(profile));
        if (profile.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(profile), "H3C SSH port must be 1-65535.");
        if (string.IsNullOrWhiteSpace(profile.Username) || profile.Username.Length > 64 ||
            profile.Username.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException("H3C username contains unsupported characters.", nameof(profile));
        if (!H3cCredentialReference.IsValid(profile.CredentialReference))
            throw new ArgumentException("Credential reference must match [A-Z][A-Z0-9_]{0,63}.", nameof(profile));
        var fingerprint = H3cHostKeyFingerprint.Normalize(profile.HostKeySha256);
        var sourceId = "h3c-" + profile.Id;
        var now = UtcNowMs();
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var existing = db.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = @"SELECT host,port,username,credential_reference,host_key_sha256
                FROM h3c_device_profiles WHERE id=$id";
            existing.Parameters.AddWithValue("$id", profile.Id);
            using var reader = existing.ExecuteReader();
            if (reader.Read() && (reader.GetString(0) != profile.Host || reader.GetInt32(1) != profile.Port ||
                reader.GetString(2) != profile.Username || reader.GetString(3) != profile.CredentialReference ||
                reader.GetString(4) != fingerprint))
                throw new InvalidOperationException("H3C connection identity is immutable; create a new profile id for host, credential-reference or host-key changes.");
        }
        using (var source = db.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText = @"INSERT INTO collection_sources(id,site_id,kind,label,enabled)
                VALUES($source,'local','H3C_SSH',$label,$enabled)
                ON CONFLICT(id) DO UPDATE SET label=excluded.label,enabled=excluded.enabled";
            source.Parameters.AddWithValue("$source", sourceId);
            source.Parameters.AddWithValue("$label", $"H3C {profile.Host}:{profile.Port}");
            source.Parameters.AddWithValue("$enabled", profile.Enabled ? 1 : 0);
            source.ExecuteNonQuery();
        }
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO h3c_device_profiles
                (id,source_id,host,port,username,credential_reference,host_key_sha256,enabled,created_ms,updated_ms)
                VALUES($id,$source,$host,$port,$username,$credential,$fingerprint,$enabled,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET source_id=excluded.source_id,host=excluded.host,port=excluded.port,
                    username=excluded.username,credential_reference=excluded.credential_reference,
                    host_key_sha256=excluded.host_key_sha256,enabled=excluded.enabled,updated_ms=excluded.updated_ms";
            insert.Parameters.AddWithValue("$id", profile.Id);
            insert.Parameters.AddWithValue("$source", sourceId);
            insert.Parameters.AddWithValue("$host", profile.Host);
            insert.Parameters.AddWithValue("$port", profile.Port);
            insert.Parameters.AddWithValue("$username", profile.Username);
            insert.Parameters.AddWithValue("$credential", profile.CredentialReference);
            insert.Parameters.AddWithValue("$fingerprint", fingerprint);
            insert.Parameters.AddWithValue("$enabled", profile.Enabled ? 1 : 0);
            insert.Parameters.AddWithValue("$created", profile.CreatedMs > 0 ? profile.CreatedMs : now);
            insert.Parameters.AddWithValue("$updated", now);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public H3cDeviceProfileSnapshot? GetH3cDeviceProfile(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT id,host,port,username,credential_reference,host_key_sha256,enabled,created_ms,updated_ms
            FROM h3c_device_profiles WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt32(6) != 0, reader.GetInt64(7), reader.GetInt64(8))
            : null;
    }

    public IReadOnlyList<H3cDeviceProfileSnapshot> RecentH3cDeviceProfiles(int limit = 25)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT id,host,port,username,credential_reference,host_key_sha256,enabled,created_ms,updated_ms
            FROM h3c_device_profiles ORDER BY updated_ms DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var reader = command.ExecuteReader();
        var profiles = new List<H3cDeviceProfileSnapshot>();
        while (reader.Read())
            profiles.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt32(6) != 0, reader.GetInt64(7), reader.GetInt64(8)));
        return profiles;
    }

    public void CreateTaskDefinition(string id, string taskType, string target, string command,
        TimeSpan timeout, bool enabled = true, string restartPolicy = "NEVER")
    {
        if (taskType is not ("SIMULATED_DEVICE_ECHO" or "H3C_SSH_READONLY"))
            throw new ArgumentException("Unsupported task type.", nameof(taskType));
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)) throw new ArgumentException("Task definition id is invalid.", nameof(id));
        if (string.IsNullOrWhiteSpace(target) || target.Length > 253 || target.Any(char.IsControl)) throw new ArgumentException("Task target is invalid.", nameof(target));
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4096 || command.Contains('\r') || command.Contains('\n')) throw new ArgumentException("Task command must be one line.", nameof(command));
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (restartPolicy != "NEVER") throw new ArgumentException("G1 restart policy only supports NEVER; interrupted runs require an explicit retry.", nameof(restartPolicy));
        if (taskType == "H3C_SSH_READONLY")
        {
            if (!H3cCommandPolicy.IsAllowed(command))
                throw new ArgumentException("Only the H3C read-only version and interface-brief commands are allowed.", nameof(command));
            var profile = GetH3cDeviceProfile(target);
            if (profile is null || !profile.Enabled) throw new ArgumentException("Enabled H3C device profile was not found.", nameof(target));
        }
        var now = UtcNowMs();
        using var db = Open();
        using var insert = db.CreateCommand();
        insert.CommandText = @"INSERT INTO task_definitions(id,task_type,target,request_json,timeout_ms,enabled,restart_policy,created_ms,updated_ms)
            VALUES($id,$type,$target,$request,$timeout,$enabled,$policy,$now,$now)
            ON CONFLICT(id) DO UPDATE SET task_type=excluded.task_type,target=excluded.target,request_json=excluded.request_json,
              timeout_ms=excluded.timeout_ms,enabled=excluded.enabled,restart_policy=excluded.restart_policy,updated_ms=excluded.updated_ms";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$type", taskType);
        insert.Parameters.AddWithValue("$target", target);
        insert.Parameters.AddWithValue("$request", JsonSerializer.Serialize(new { command }));
        insert.Parameters.AddWithValue("$timeout", (long)timeout.TotalMilliseconds);
        insert.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        insert.Parameters.AddWithValue("$policy", restartPolicy);
        insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
    }

    public TaskDefinitionSnapshot? GetTaskDefinition(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,task_type,target,request_json,timeout_ms,enabled,restart_policy FROM task_definitions WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var request = JsonDocument.Parse(reader.GetString(3));
        using (request)
        {
            var commandText = request.RootElement.GetProperty("command").GetString() ?? "";
            return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), commandText,
                reader.GetInt64(4), reader.GetInt32(5) != 0, reader.GetString(6));
        }
    }

    public string CreateTaskRun(string definitionId)
    {
        var definition = GetTaskDefinition(definitionId) ?? throw new KeyNotFoundException($"Task definition '{definitionId}' does not exist.");
        return CreateTaskRun(definition);
    }

    public string CreateTaskRun(TaskDefinitionSnapshot definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.Enabled) throw new InvalidOperationException("Disabled task definitions cannot be scheduled.");
        var id = Guid.NewGuid().ToString("N");
        var now = UtcNowMs();
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var current = db.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = @"SELECT task_type,target,request_json,timeout_ms,enabled,restart_policy
                FROM task_definitions WHERE id=$id";
            current.Parameters.AddWithValue("$id", definition.Id);
            using var reader = current.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException($"Task definition '{definition.Id}' does not exist.");
            using var request = JsonDocument.Parse(reader.GetString(2));
            var currentCommand = request.RootElement.GetProperty("command").GetString() ?? string.Empty;
            if (reader.GetString(0) != definition.TaskType || reader.GetString(1) != definition.Target ||
                currentCommand != definition.Command || reader.GetInt64(3) != definition.TimeoutMs ||
                (reader.GetInt32(4) != 0) != definition.Enabled || reader.GetString(5) != definition.RestartPolicy)
                throw new InvalidOperationException("Task definition changed during queue admission; reload it and retry.");
        }
        string? sourceId = null;
        if (definition.TaskType == "H3C_SSH_READONLY")
        {
            using var profile = db.CreateCommand();
            profile.Transaction = transaction;
            profile.CommandText = "SELECT source_id FROM h3c_device_profiles WHERE id=$id AND enabled=1";
            profile.Parameters.AddWithValue("$id", definition.Target);
            sourceId = Convert.ToString(profile.ExecuteScalar());
            if (sourceId is null) throw new InvalidOperationException("Enabled H3C profile disappeared before task-run admission.");
        }
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO task_runs(id,source_id,task_type,state,queued_ms,deadline_ms,definition_id)
            VALUES($id,$source,$type,'QUEUED',$now,NULL,$definition)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", definition.TaskType);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$definition", definition.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
        return id;
    }

    public void TransitionTaskRun(string id, string nextState, string? failureReason = null)
    {
        if (nextState is not ("CONNECTING" or "RUNNING" or "SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED"))
            throw new ArgumentException("Unsupported task run state.", nameof(nextState));
        var now = UtcNowMs();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"UPDATE task_runs SET state=$next,
            started_ms=CASE WHEN $next='CONNECTING' THEN COALESCE(started_ms,$now) ELSE started_ms END,
            deadline_ms=CASE WHEN $next='CONNECTING' AND definition_id IS NOT NULL
                THEN $now+(SELECT timeout_ms FROM task_definitions WHERE id=definition_id) ELSE deadline_ms END,
            ended_ms=CASE WHEN $next IN ('SUCCEEDED','FAILED','TIMED_OUT','CANCELLED') THEN $now ELSE ended_ms END,
            failure_reason=$reason
            WHERE id=$id AND ((state='QUEUED' AND $next IN ('CONNECTING','FAILED','CANCELLED')) OR
              (state='CONNECTING' AND $next IN ('RUNNING','FAILED','TIMED_OUT','CANCELLED')) OR
              (state='RUNNING' AND $next IN ('SUCCEEDED','FAILED','TIMED_OUT','CANCELLED')))";
        command.Parameters.AddWithValue("$next", nextState);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$reason", (object?)failureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Task run '{id}' did not accept the requested '{nextState}' transition.");
    }

    public void CompleteTaskRunWithRawOutput(string runId, H3cRawOutputManifest manifest,
        string terminalState, string? failureReason)
    {
        if (terminalState is not ("SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED"))
            throw new ArgumentException("H3C task completion requires a terminal state.", nameof(terminalState));
        if (manifest.CaptureStatus is not ("COMPLETE" or "PARTIAL") ||
            manifest.ParseStatus is not ("PARSED" or "EMPTY" or "UNRECOGNIZED" or "PARTIAL"))
            throw new ArgumentException("H3C capture and parse statuses are invalid.", nameof(manifest));
        if (manifest.CaptureStatus == "PARTIAL" && terminalState == "SUCCEEDED")
            throw new InvalidOperationException("Partial H3C capture cannot transition to SUCCEEDED.");
        if (!H3cCommandPolicy.IsAllowed(manifest.Command)) throw new ArgumentException("Raw output command is not allowlisted.", nameof(manifest));
        if (manifest.ByteCount is < 0 or > H3cRawOutput.MaximumBytes) throw new ArgumentOutOfRangeException(nameof(manifest));
        if (!IsSha256(manifest.OutputSha256) || !IsSha256(manifest.SegmentSha256))
            throw new ArgumentException("H3C output manifest checksum is invalid.", nameof(manifest));
        using (JsonDocument.Parse(manifest.ParsedJson)) { }
        if (!TryResolveRawPath(manifest.RelativePath, out var fullPath) || !File.Exists(fullPath) || ContainsReparsePoint(fullPath))
            throw new IOException("Sealed H3C Raw output path is missing or unsafe.");
        var rawBytes = File.ReadAllBytes(fullPath);
        if (rawBytes.Length is < 2 or > MaximumLineBytes || rawBytes[^1] != (byte)'\n' ||
            rawBytes.AsSpan(0, rawBytes.Length - 1).Contains((byte)'\n') ||
            !TryReadRecord(rawBytes.AsSpan(0, rawBytes.Length - 1).ToArray(), out _, out var recordSourceId,
                out var recordDeviceHost, out var rawRunId, out var rawCaptureStatus))
            throw new InvalidDataException("Sealed H3C Raw file must contain exactly one complete verified command record.");
        var actualSegmentSha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
        if (!string.Equals(actualSegmentSha, manifest.SegmentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sealed H3C Raw bytes do not match the supplied segment checksum.");
        string rawProfileId;
        string rawDeviceHost;
        using (var rawDocument = JsonDocument.Parse(rawBytes.AsMemory(0, rawBytes.Length - 1)))
        {
            var record = rawDocument.RootElement;
            rawProfileId = record.GetProperty("device_profile_id").GetString() ?? string.Empty;
            rawDeviceHost = recordDeviceHost ?? string.Empty;
            if (rawRunId != runId || rawCaptureStatus != manifest.CaptureStatus ||
                record.GetProperty("command").GetString() != manifest.Command ||
                record.GetProperty("output_byte_count").GetInt32() != manifest.ByteCount ||
                !string.Equals(record.GetProperty("output_sha256").GetString(), manifest.OutputSha256, StringComparison.OrdinalIgnoreCase) ||
                record.GetProperty("captured_ms").GetInt64() != manifest.CapturedMs ||
                !string.Equals(recordSourceId, "h3c-" + record.GetProperty("device_profile_id").GetString(), StringComparison.Ordinal))
                throw new InvalidDataException("Sealed H3C Raw record does not match the task output manifest.");
        }

        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var insert = db.CreateCommand())
        {
        insert.Transaction = transaction;
        insert.CommandText = @"INSERT OR IGNORE INTO task_run_raw_outputs
            (task_run_id,relative_path,command,byte_count,sha256,raw_segment_sha256,parsed_json,captured_ms,capture_status,parse_status)
            SELECT t.id,r.relative_path,$command,$bytes,$outputSha,$segmentSha,$parsed,$captured,$capture,$parse
            FROM task_runs t JOIN task_definitions d ON d.id=t.definition_id
            JOIN h3c_device_profiles p ON p.source_id=t.source_id
            JOIN raw_segments r ON r.relative_path=$path
            WHERE t.id=$run AND t.task_type='H3C_SSH_READONLY' AND d.task_type=t.task_type
              AND json_extract(d.request_json,'$.command')=$command
              AND d.target=p.id AND p.id=$profile AND p.host=$host
              AND t.state='RUNNING' AND r.state='FINALIZED' AND r.record_count=1 AND r.source_id=t.source_id
              AND r.task_run_id=$run AND r.sha256=$segmentSha AND r.byte_count=$segmentBytes";
        AddManifestParameters(insert, runId, manifest, rawProfileId, rawDeviceHost);
        insert.Parameters.AddWithValue("$segmentBytes", rawBytes.Length);
        insert.ExecuteNonQuery();
        }
        using (var verify = db.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = @"SELECT relative_path,command,byte_count,sha256,raw_segment_sha256,parsed_json,captured_ms,capture_status,parse_status
                FROM task_run_raw_outputs WHERE task_run_id=$run";
            verify.Parameters.AddWithValue("$run", runId);
            using var reader = verify.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != manifest.RelativePath || reader.GetString(1) != manifest.Command ||
                reader.GetInt64(2) != manifest.ByteCount || reader.GetString(3) != manifest.OutputSha256 ||
                reader.GetString(4) != manifest.SegmentSha256 || reader.GetString(5) != manifest.ParsedJson ||
                reader.GetInt64(6) != manifest.CapturedMs || reader.GetString(7) != manifest.CaptureStatus ||
                reader.GetString(8) != manifest.ParseStatus)
            throw new InvalidOperationException("H3C Raw output did not match the task run, command or sealed segment manifest.");
        }
        using (var update = db.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = @"UPDATE task_runs SET state=$state,ended_ms=$now,failure_reason=$failure
                WHERE id=$run AND state='RUNNING'";
            update.Parameters.AddWithValue("$state", terminalState);
            update.Parameters.AddWithValue("$now", UtcNowMs());
            update.Parameters.AddWithValue("$failure", (object?)failureReason ?? DBNull.Value);
            update.Parameters.AddWithValue("$run", runId);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("H3C task state did not accept the atomic terminal transition.");
        }
        transaction.Commit();
    }

    private static void AddManifestParameters(SqliteCommand command, string runId, H3cRawOutputManifest manifest,
        string profileId, string host)
    {
        command.Parameters.AddWithValue("$command", manifest.Command);
        command.Parameters.AddWithValue("$bytes", manifest.ByteCount);
        command.Parameters.AddWithValue("$outputSha", manifest.OutputSha256);
        command.Parameters.AddWithValue("$segmentSha", manifest.SegmentSha256);
        command.Parameters.AddWithValue("$parsed", manifest.ParsedJson);
        command.Parameters.AddWithValue("$captured", manifest.CapturedMs);
        command.Parameters.AddWithValue("$capture", manifest.CaptureStatus);
        command.Parameters.AddWithValue("$parse", manifest.ParseStatus);
        command.Parameters.AddWithValue("$path", manifest.RelativePath);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$host", host);
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    public void RegisterFinalizedSegment(string relative, long count, long first, long last, string sha, long bytes,
        string sourceId = "syslog-local", string? taskRunId = null)
    {
        UpsertRecoveredSegment(relative, new(count, first, last, bytes, sha, false, 0, null, 0, 0, sourceId,
            TaskRunId: taskRunId), "FINALIZED");
    }

    public void MarkUnavailable(Exception exception)
    {
        Available = false;
        RawWriteAvailable = false;
        StorageFailure = $"CATALOG_UNAVAILABLE: {exception.GetType().Name}";
    }

    public void EndTaskRun(string id, string state)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE task_runs SET state=$state, ended_ms=$now WHERE id=$id AND state='RUNNING'";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$now", UtcNowMs());
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TaskRunSnapshot> RecentTaskRuns(int limit = 25)
    {
        using var db = Open();
        using var command = db.CreateCommand();
            command.CommandText = @"SELECT t.id,t.source_id,t.task_type,t.state,t.queued_ms,t.started_ms,t.deadline_ms,t.ended_ms,t.failure_reason,t.definition_id,
                o.relative_path,o.sha256,o.byte_count,o.parsed_json,o.capture_status,o.parse_status
            FROM task_runs t LEFT JOIN task_run_raw_outputs o ON o.task_run_id=t.id
            ORDER BY COALESCE(t.queued_ms,t.started_ms) DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var reader = command.ExecuteReader();
        var result = new List<TaskRunSnapshot>();
        while (reader.Read())
        {
            result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15)));
        }
        return result;
    }

    public RawCatalogCounts GetRawCounts()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT state,COUNT(*) FROM raw_segments GROUP BY state";
        using var reader = command.ExecuteReader();
        int finalized = 0, incomplete = 0, missing = 0;
        while (reader.Read())
        {
            switch (reader.GetString(0))
            {
                case "FINALIZED": finalized = reader.GetInt32(1); break;
                case "INCOMPLETE": incomplete = reader.GetInt32(1); break;
                case "MISSING": missing = reader.GetInt32(1); break;
            }
        }
        return new(finalized, incomplete, missing);
    }

    public int GetIntegrityAuditCount()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_integrity_audits";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int GetUnresolvedIntegrityIssueCount()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT
            (SELECT COUNT(*) FROM raw_integrity_audits a
             LEFT JOIN raw_issue_dispositions d ON d.audit_id=a.id WHERE d.audit_id IS NULL) +
            (SELECT COUNT(*) FROM raw_segments s WHERE s.state IN ('INCOMPLETE','MISSING')
             AND NOT EXISTS(SELECT 1 FROM raw_integrity_audits a WHERE a.relative_path=s.relative_path))";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public bool HasUnresolvedRecoveryMarker => RecoveryWasInterrupted;

    public void QuarantineIntegrityAudit(string auditId, string actor, string detail)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128)
            throw new ArgumentException("Actor is required and must be at most 128 characters.", nameof(actor));
        if (string.IsNullOrWhiteSpace(detail) || detail.Length > 2048)
            throw new ArgumentException("Disposition detail is required and must be at most 2048 characters.", nameof(detail));
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var existing = db.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT 1 FROM raw_issue_dispositions WHERE audit_id=$audit";
            existing.Parameters.AddWithValue("$audit", auditId);
            if (existing.ExecuteScalar() is not null)
                throw new InvalidOperationException($"Raw integrity audit '{auditId}' already has a disposition.");
        }
        string? observedSha, expectedSha, evidence;
        string relativePath;
        using (var read = db.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT relative_path,observed_sha256,expected_sha256,evidence_path FROM raw_integrity_audits WHERE id=$id";
            read.Parameters.AddWithValue("$id", auditId);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException($"Raw integrity audit '{auditId}' does not exist.");
            relativePath = reader.GetString(0);
            observedSha = reader.IsDBNull(1) ? null : reader.GetString(1);
            expectedSha = reader.IsDBNull(2) ? null : reader.GetString(2);
            evidence = reader.IsDBNull(3) ? null : reader.GetString(3);
        }
        if (!TryResolveRawPath(relativePath, out var rawPath) || !File.Exists(rawPath) || ContainsReparsePoint(rawPath))
            throw new InvalidOperationException("Raw bytes are unavailable or unsafe; a missing file cannot be quarantined as retained evidence.");
        var expectedCurrentHash = observedSha ?? expectedSha;
        if (expectedCurrentHash is null || !FileHashMatches(rawPath, expectedCurrentHash))
            throw new InvalidOperationException("Raw bytes changed since this audit or have no verified checksum; rerun recovery before disposition.");
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO raw_issue_dispositions
                (id,audit_id,disposition,actor,detail,evidence_path,observed_sha256,disposed_ms)
                VALUES($id,$audit,'QUARANTINED',$actor,$detail,$evidence,$sha,$now)";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$audit", auditId);
            insert.Parameters.AddWithValue("$actor", actor.Trim());
            insert.Parameters.AddWithValue("$detail", detail.Trim());
            insert.Parameters.AddWithValue("$evidence", (object?)evidence ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sha", (object?)observedSha ?? (expectedSha is null ? DBNull.Value : expectedSha));
            insert.Parameters.AddWithValue("$now", UtcNowMs());
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public CatalogHealthSnapshot GetHealthSnapshot()
    {
        using var db = Open();
        int finalized = 0, incomplete = 0, missing = 0;
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT state,COUNT(*) FROM raw_segments GROUP BY state";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                switch (reader.GetString(0))
                {
                    case "FINALIZED": finalized = reader.GetInt32(1); break;
                    case "INCOMPLETE": incomplete = reader.GetInt32(1); break;
                    case "MISSING": missing = reader.GetInt32(1); break;
                }
            }
        }
        int auditCount;
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM raw_integrity_audits";
            auditCount = Convert.ToInt32(command.ExecuteScalar());
        }
        var unresolvedCount = GetUnresolvedIntegrityIssueCount();
        var audits = new List<RawIntegrityAuditSnapshot>();
        using (var command = db.CreateCommand())
        {
            command.CommandText = @"SELECT a.id,a.relative_path,a.detected_ms,a.reason,a.expected_sha256,a.observed_sha256,
                a.expected_bytes,a.observed_bytes,a.evidence_path,a.detail,d.disposition,d.actor,d.detail,d.disposed_ms
                FROM raw_integrity_audits a LEFT JOIN raw_issue_dispositions d ON d.audit_id=a.id
                ORDER BY a.detected_ms DESC LIMIT 10";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                audits.Add(ReadIntegrityAudit(reader));
        }
        return new(new(finalized, incomplete, missing), auditCount, unresolvedCount, audits);
    }

    public RawMaintenanceAuditResult? AuditOneFinalizedSegment()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT relative_path,record_count,byte_count,sha256 FROM raw_segments
            WHERE state='FINALIZED' ORDER BY COALESCE(last_full_audit_ms,0),relative_path LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var row = new IndexedSegment(reader.GetString(0), "syslog-local", "FINALIZED",
            reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), null, null, null, null, false);
        reader.Close();
        try
        {
            if (!TryResolveRawPath(row.RelativePath, out var path) || !File.Exists(path) || ContainsReparsePoint(path))
                throw new IOException("Raw file is missing or traverses a reparse point.");
            var inspection = InspectAndRepair(path);
            var matches = row.RecordCount == inspection.RecordCount && row.ByteCount == inspection.ByteCount &&
                string.Equals(row.Sha256, inspection.Sha256, StringComparison.OrdinalIgnoreCase) && !inspection.CorruptTail;
            if (!matches)
            {
                RecordIntegrityAudit(row.RelativePath, row, inspection, inspection.CorruptTail ? "MAINTENANCE_CORRUPTION" : "MAINTENANCE_CHECKSUM_MISMATCH");
                UpsertRecoveredSegment(row.RelativePath, inspection, "INCOMPLETE");
                return new(row.RelativePath, "INTEGRITY_ISSUE", "Checksum/record integrity mismatch; file marked INCOMPLETE.");
            }

            ResolveVerifiedIntegrityAudits(row.RelativePath, inspection.Sha256);
            using var updateDb = Open();
            using var update = updateDb.CreateCommand();
            var info = new FileInfo(path);
            update.CommandText = @"UPDATE raw_segments SET last_full_audit_ms=$now,last_verified_ms=$now,
                checkpoint_size=$size,checkpoint_write_ticks=$ticks WHERE relative_path=$path AND state='FINALIZED'";
            update.Parameters.AddWithValue("$now", UtcNowMs());
            update.Parameters.AddWithValue("$size", info.Length);
            update.Parameters.AddWithValue("$ticks", info.LastWriteTimeUtc.Ticks);
            update.Parameters.AddWithValue("$path", row.RelativePath);
            update.ExecuteNonQuery();
            return new(row.RelativePath, "VERIFIED", $"Full integrity audit passed ({inspection.BytesRead} bytes read).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
        {
            RecordIntegrityAudit(row.RelativePath, row, null, "MAINTENANCE_FAILED", ex.Message);
            using var updateDb = Open();
            using var update = updateDb.CreateCommand();
            update.CommandText = "UPDATE raw_segments SET last_full_audit_ms=$now WHERE relative_path=$path AND state='FINALIZED'";
            update.Parameters.AddWithValue("$now", UtcNowMs());
            update.Parameters.AddWithValue("$path", row.RelativePath);
            update.ExecuteNonQuery();
            return new(row.RelativePath, "FAILED", $"Full integrity audit failed ({ex.GetType().Name}).");
        }
    }

    public IReadOnlyList<RawIntegrityAuditSnapshot> RecentIntegrityAudits(int limit = 10)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT a.id,a.relative_path,a.detected_ms,a.reason,a.expected_sha256,a.observed_sha256,
            a.expected_bytes,a.observed_bytes,a.evidence_path,a.detail,d.disposition,d.actor,d.detail,d.disposed_ms
            FROM raw_integrity_audits a LEFT JOIN raw_issue_dispositions d ON d.audit_id=a.id
            ORDER BY a.detected_ms DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));
        using var reader = command.ExecuteReader();
        var audits = new List<RawIntegrityAuditSnapshot>();
        while (reader.Read())
            audits.Add(ReadIntegrityAudit(reader));
        return audits;
    }

    private static RawIntegrityAuditSnapshot ReadIntegrityAudit(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetInt64(13));

    public int OpenSegmentCount() => Directory.Exists(RawRoot)
        ? Directory.EnumerateFiles(RawRoot, "*.open", SearchOption.AllDirectories).Take(10001).Count()
        : 0;

    private Dictionary<string, IndexedSegment> ReadIndexedSegments()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT relative_path,source_id,state,record_count,byte_count,sha256,checkpoint_size,checkpoint_write_ticks,last_full_audit_ms,task_run_id,EXISTS(SELECT 1 FROM task_run_raw_outputs o WHERE o.relative_path=raw_segments.relative_path) FROM raw_segments";
        using var reader = command.ExecuteReader();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new Dictionary<string, IndexedSegment>(pathComparer);
        while (reader.Read())
        {
            var relative = reader.GetString(0).Replace('\\', '/');
            result[relative] = new(relative, reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt32(10) != 0);
        }
        return result;
    }

    private bool TryResolveRawPath(string relative, out string path)
    {
        path = string.Empty;
        if (Path.IsPathRooted(relative)) return false;
        var candidate = Path.GetFullPath(Path.Combine(settings.DataRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rawRoot = Path.GetFullPath(RawRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rawRoot, comparison)) return false;
        path = candidate;
        return true;
    }

    private bool ContainsReparsePoint(string path)
    {
        var rawRoot = Path.GetFullPath(RawRoot);
        var current = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            if (string.Equals(current, rawRoot, comparison)) return false;
            current = Directory.GetParent(current)?.FullName
                ?? throw new IOException("Raw file escaped the raw data root.");
        }
    }

    private string RelativeRawPath(string path) => Path.GetRelativePath(settings.DataRoot, path).Replace('\\', '/');

    private void MarkMissing(string relative)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE raw_segments SET state='MISSING' WHERE relative_path=$path AND state<>'MISSING'";
        command.Parameters.AddWithValue("$path", relative);
        command.ExecuteNonQuery();
    }

    private void UpsertRecoveredSegment(string relative, RawSegmentInspection info, string state)
    {
        ValidateRawSourcePath(relative, info.SourceId, info.DeviceHost);
        var filePath = Path.Combine(settings.DataRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        var file = new FileInfo(filePath);
        var checkpointSize = file.Exists ? file.Length : info.ByteCount;
        var checkpointTicks = file.Exists ? file.LastWriteTimeUtc.Ticks : 0;
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO raw_segments
            (id,source_id,relative_path,state,first_received_ms,last_received_ms,record_count,byte_count,sha256,sealed_ms,
             checkpoint_size,checkpoint_write_ticks,last_verified_ms,task_run_id)
            VALUES($id,$source,$path,$state,$first,$last,$count,$bytes,$sha,$now,$size,$ticks,$now,$run)
            ON CONFLICT(relative_path) DO UPDATE SET
              state=excluded.state, first_received_ms=excluded.first_received_ms,
              last_received_ms=excluded.last_received_ms, record_count=excluded.record_count,
              byte_count=excluded.byte_count, sha256=excluded.sha256,
              checkpoint_size=excluded.checkpoint_size,checkpoint_write_ticks=excluded.checkpoint_write_ticks,
              last_verified_ms=excluded.last_verified_ms,
              task_run_id=COALESCE(raw_segments.task_run_id,excluded.task_run_id),
              sealed_ms=COALESCE(raw_segments.sealed_ms,excluded.sealed_ms)
            WHERE raw_segments.source_id=excluded.source_id";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$source", info.SourceId);
        command.Parameters.AddWithValue("$path", relative);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$first", info.RecordCount == 0 ? DBNull.Value : info.FirstReceivedMs);
        command.Parameters.AddWithValue("$last", info.RecordCount == 0 ? DBNull.Value : info.LastReceivedMs);
        command.Parameters.AddWithValue("$count", info.RecordCount);
        command.Parameters.AddWithValue("$bytes", info.ByteCount);
        command.Parameters.AddWithValue("$sha", info.Sha256);
        command.Parameters.AddWithValue("$now", UtcNowMs());
        command.Parameters.AddWithValue("$size", checkpointSize);
        command.Parameters.AddWithValue("$ticks", checkpointTicks);
        command.Parameters.AddWithValue("$run", (object?)info.TaskRunId ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("Raw segment source identity changed for an indexed path; preserving the file for investigation.");
    }

    private void ValidateRawSourcePath(string relative, string sourceId, string? deviceHost = null)
    {
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "raw")
            throw new InvalidDataException("Raw segment path is outside a supported source folder.");
        if (sourceId == "syslog-local" && parts[1] == "syslog") return;
        if (!sourceId.StartsWith("h3c-", StringComparison.Ordinal) || parts[1] != "h3c" || parts.Length < 4 ||
            !Guid.TryParseExact(sourceId[4..], "N", out _) || parts[2] != sourceId[4..])
            throw new InvalidDataException("Raw content source identity does not match its isolated source folder.");

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT host FROM h3c_device_profiles WHERE id=$id AND source_id=$source";
        command.Parameters.AddWithValue("$id", sourceId[4..]);
        command.Parameters.AddWithValue("$source", sourceId);
        var profileHost = Convert.ToString(command.ExecuteScalar());
        if (profileHost is null)
            throw new InvalidDataException("H3C Raw output references an unknown device profile/source.");
        if (deviceHost is not null && !string.Equals(profileHost, deviceHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("H3C Raw device host does not match its immutable profile.");
    }

    private string ResolveEmptyRawSource(string path)
    {
        var relative = RelativeRawPath(path);
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0] == "raw" && parts[1] == "syslog") return "syslog-local";
        if (parts.Length >= 4 && parts[0] == "raw" && parts[1] == "h3c" &&
            Guid.TryParseExact(parts[2], "N", out _) && parts[2] == parts[2].ToLowerInvariant())
        {
            var source = "h3c-" + parts[2];
            ValidateRawSourcePath(relative, source);
            return source;
        }
        throw new InvalidDataException("Raw file contains no valid record and has no verifiable source identity.");
    }

    private void RecordIntegrityAudit(string relative, IndexedSegment? expected, RawSegmentInspection? observed,
        string reason, string? detail = null)
    {
        using var db = Open();
        using (var exists = db.CreateCommand())
        {
            exists.CommandText = @"SELECT 1 FROM raw_integrity_audits a
                LEFT JOIN raw_issue_dispositions d ON d.audit_id=a.id
                WHERE a.relative_path=$path AND a.reason=$reason AND a.expected_sha256 IS $expectedSha
                  AND a.observed_sha256 IS $observedSha AND d.audit_id IS NULL LIMIT 1";
            exists.Parameters.AddWithValue("$path", relative);
            exists.Parameters.AddWithValue("$reason", reason);
            exists.Parameters.AddWithValue("$expectedSha", (object?)expected?.Sha256 ?? DBNull.Value);
            exists.Parameters.AddWithValue("$observedSha", (object?)observed?.Sha256 ?? DBNull.Value);
            if (exists.ExecuteScalar() is not null) return;
        }
        using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO raw_integrity_audits
            (id,relative_path,detected_ms,reason,expected_sha256,observed_sha256,expected_bytes,observed_bytes,evidence_path,detail)
            VALUES($id,$path,$now,$reason,$expectedSha,$observedSha,$expectedBytes,$observedBytes,$evidence,$detail)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$path", relative);
        command.Parameters.AddWithValue("$now", UtcNowMs());
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$expectedSha", (object?)expected?.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$observedSha", (object?)observed?.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$expectedBytes", (object?)expected?.ByteCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$observedBytes", (object?)observed?.ByteCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidence", (object?)observed?.EvidencePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", detail ?? string.Empty);
        command.ExecuteNonQuery();
    }

    private void ResolveVerifiedIntegrityAudits(string relative, string observedSha)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO raw_issue_dispositions
            (id,audit_id,disposition,actor,detail,evidence_path,observed_sha256,disposed_ms)
            SELECT lower(hex(randomblob(16))),a.id,'RESOLVED_VERIFIED','system-integrity-auditor',
                'A later complete verification succeeded; the Raw bytes remain retained.',a.evidence_path,$sha,$now
            FROM raw_integrity_audits a LEFT JOIN raw_issue_dispositions d ON d.audit_id=a.id
            WHERE a.relative_path=$path AND d.audit_id IS NULL
              AND a.reason IN ('MISSING_RAW','REPARSE_POINT','HISTORICAL_RAW','OPEN_RECOVERY_FAILED','FINALIZED_RECOVERY_FAILED','MAINTENANCE_FAILED')";
        command.Parameters.AddWithValue("$path", relative);
        command.Parameters.AddWithValue("$sha", observedSha);
        command.Parameters.AddWithValue("$now", UtcNowMs());
        command.ExecuteNonQuery();
    }

    private RawSegmentInspection InspectAndRepair(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.SequentialScan);
        var originalLength = stream.Length;
        long validBytes = 0, recordCount = 0, first = long.MaxValue, last = 0, consumed = 0;
        string? segmentSourceId = null;
        string? segmentDeviceHost = null;
        string? segmentTaskRunId = null;
        string segmentCaptureStatus = "COMPLETE";
        bool corrupt = false;
        using var line = new MemoryStream();
        var buffer = new byte[65536];

        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                var current = buffer[i];
                consumed++;
                if (current == (byte)'\n')
                {
                    var lineBytes = line.ToArray();
                    line.SetLength(0);
                    line.Position = 0;
                    if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
                        Array.Resize(ref lineBytes, lineBytes.Length - 1);
                    if (!TryReadRecord(lineBytes, out var timestamp, out var recordSourceId, out var recordDeviceHost,
                            out var recordTaskRunId, out var recordCaptureStatus) ||
                        (segmentSourceId is not null && !string.Equals(segmentSourceId, recordSourceId, StringComparison.Ordinal)) ||
                        (segmentDeviceHost is not null && !string.Equals(segmentDeviceHost, recordDeviceHost, StringComparison.OrdinalIgnoreCase)) ||
                        (segmentTaskRunId is not null && !string.Equals(segmentTaskRunId, recordTaskRunId, StringComparison.Ordinal)))
                    {
                        corrupt = true;
                        goto Finished;
                    }
                    segmentSourceId ??= recordSourceId;
                    segmentDeviceHost ??= recordDeviceHost;
                    segmentTaskRunId ??= recordTaskRunId;
                    segmentCaptureStatus = recordCaptureStatus;
                    validBytes = consumed;
                    recordCount++;
                    first = Math.Min(first, timestamp);
                    last = Math.Max(last, timestamp);
                }
                else
                {
                    if (line.Length >= MaximumLineBytes)
                    {
                        corrupt = true;
                        goto Finished;
                    }
                    line.WriteByte(current);
                }
            }
        }

        if (line.Length != 0) corrupt = true; // Only newline-terminated records are durable records.

    Finished:
        string? evidencePath = null;
        if (corrupt)
        {
            if (originalLength > validBytes)
                evidencePath = PreserveTailEvidence(path, stream, validBytes, originalLength);
            stream.SetLength(validBytes);
            stream.Flush(flushToDisk: true);
        }
        var byteCount = corrupt ? validBytes : originalLength;
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var sourceId = segmentSourceId ?? ResolveEmptyRawSource(path);
        return new(recordCount, recordCount == 0 ? 0 : first, last, byteCount, hash, corrupt,
            Math.Max(0, originalLength - byteCount), evidencePath, originalLength, byteCount, sourceId,
            segmentDeviceHost, segmentTaskRunId, segmentCaptureStatus);
    }

    private string PreserveTailEvidence(string sourcePath, FileStream source, long offset, long originalLength)
    {
        var relativeSourcePath = RelativeRawPath(sourcePath);
        var evidenceRoot = Path.Combine(settings.DataRoot, "recovery-evidence");
        Directory.CreateDirectory(evidenceRoot);
        if ((File.GetAttributes(evidenceRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recovery evidence directory cannot be a symbolic link or reparse point.");

        var tailLength = originalLength - offset;
        source.Position = offset;
        var tailHash = HashRange(source, tailLength);
        var identityMaterial = System.Text.Encoding.UTF8.GetBytes($"{relativeSourcePath}\n{offset}\n{tailHash}");
        var identity = Convert.ToHexString(SHA256.HashData(identityMaterial)).ToLowerInvariant();
        var evidencePath = Path.Combine(evidenceRoot, identity + ".tail.bin");
        var metadataPath = Path.Combine(evidenceRoot, identity + ".json");

        if (File.Exists(evidencePath) && (File.GetAttributes(evidencePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recovery evidence cannot be a symbolic link or reparse point.");
        if (File.Exists(metadataPath) && (File.GetAttributes(metadataPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recovery evidence metadata cannot be a symbolic link or reparse point.");

        if (!File.Exists(evidencePath))
        {
            var temporaryPath = evidencePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                source.Position = offset;
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           65536, FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    CopyRange(source, output, tailLength);
                    output.Flush(flushToDisk: true);
                }
                if (new FileInfo(temporaryPath).Length != tailLength || !FileHashMatches(temporaryPath, tailHash))
                    throw new IOException("Recovery evidence verification failed; the damaged Raw source was left untouched.");
                try
                {
                    File.Move(temporaryPath, evidencePath);
                }
                catch (IOException) when (File.Exists(evidencePath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        if (!FileHashMatches(evidencePath, tailHash) || new FileInfo(evidencePath).Length != tailLength)
            throw new IOException("Existing recovery evidence does not match the damaged Raw tail; the source was left untouched.");

        if (!File.Exists(metadataPath))
        {
            var metadata = JsonSerializer.Serialize(new
            {
                sourceRelativePath = relativeSourcePath,
                originalLength,
                tailOffset = offset,
                tailBytes = tailLength,
                tailSha256 = tailHash,
                preservedAtMs = UtcNowMs()
            }, new JsonSerializerOptions { WriteIndented = true });
            WriteDurableNewFile(metadataPath, metadata);
        }

        return evidencePath;
    }

    private static string HashRange(Stream stream, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        var remaining = length;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("Raw tail changed while recovery evidence was being hashed.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void CopyRange(Stream source, Stream destination, long length)
    {
        var buffer = new byte[65536];
        var remaining = length;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("Raw tail changed while recovery evidence was being copied.");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static bool FileHashMatches(string path, string expectedHash)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteDurableNewFile(string path, string contents)
    {
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another recovery pass already created the immutable metadata.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool TryReadRecord(byte[] line, out long timestamp, out string sourceId, out string? deviceHost,
        out string? taskRunId, out string captureStatus)
    {
        timestamp = 0;
        sourceId = string.Empty;
        deviceHost = null;
        taskRunId = null;
        captureStatus = "COMPLETE";
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("record_type", out var type))
            {
                if (type.ValueKind != JsonValueKind.String || type.GetString() != "h3c_command" ||
                    !root.TryGetProperty("captured_ms", out var captured) || !captured.TryGetInt64(out timestamp) || timestamp <= 0 ||
                    !root.TryGetProperty("source_id", out var source) || source.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("device_profile_id", out var profile) || profile.ValueKind != JsonValueKind.String ||
                    !Guid.TryParseExact(profile.GetString(), "N", out _) ||
                    !root.TryGetProperty("task_run_id", out var run) || run.ValueKind != JsonValueKind.String ||
                    !Guid.TryParseExact(run.GetString(), "N", out _) ||
                    !root.TryGetProperty("device_host", out var host) || host.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(host.GetString()) || host.GetString()!.Length > 253 ||
                    host.GetString()!.Any(char.IsControl) || host.GetString()!.Any(char.IsWhiteSpace) ||
                    Uri.CheckHostName(host.GetString()) == UriHostNameType.Unknown ||
                    !root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String ||
                    !H3cCommandPolicy.IsAllowed(command.GetString() ?? string.Empty) ||
                    !root.TryGetProperty("output_byte_count", out var byteCount) || !byteCount.TryGetInt32(out var count) ||
                    count is < 0 or > H3cRawOutput.MaximumBytes ||
                    !root.TryGetProperty("output_sha256", out var outputHash) || outputHash.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("output_utf8_base64", out var output) || output.ValueKind != JsonValueKind.String)
                    return false;
                if (root.TryGetProperty("capture_status", out var capture))
                {
                    if (capture.ValueKind != JsonValueKind.String || capture.GetString() is not ("COMPLETE" or "PARTIAL")) return false;
                    captureStatus = capture.GetString()!;
                }
                sourceId = source.GetString() ?? string.Empty;
                deviceHost = host.GetString();
                taskRunId = run.GetString();
                var profileId = profile.GetString()!;
                if (!string.Equals(sourceId, "h3c-" + profileId, StringComparison.Ordinal) ||
                    !string.Equals(profileId, profileId.ToLowerInvariant(), StringComparison.Ordinal)) return false;
                var outputBytes = Convert.FromBase64String(output.GetString() ?? string.Empty);
                if (outputBytes.Length != count) return false;
                var expectedHash = outputHash.GetString() ?? string.Empty;
                if (expectedHash.Length != 64 || expectedHash.Any(c => !Uri.IsHexDigit(c))) return false;
                var actualHash = SHA256.HashData(outputBytes);
                if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(expectedHash))) return false;
                _ = new System.Text.UTF8Encoding(false, true).GetString(outputBytes);
                return true;
            }

            if (!root.TryGetProperty("received_ms", out var received) || !received.TryGetInt64(out timestamp) || timestamp <= 0 ||
                !root.TryGetProperty("sender_ip", out var sender) || sender.ValueKind != JsonValueKind.String ||
                !System.Net.IPAddress.TryParse(sender.GetString(), out _) ||
                !root.TryGetProperty("sender_port", out var port) || !port.TryGetInt32(out var senderPort) || senderPort is < 0 or > 65535 ||
                !root.TryGetProperty("payload_base64", out var payload) || payload.ValueKind != JsonValueKind.String)
                return false;
            var payloadBytes = Convert.FromBase64String(payload.GetString() ?? string.Empty);
            if (payloadBytes.Length > 65507) return false;
            sourceId = "syslog-local";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static RawRecoverySummary EmptyRecovery() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<RecoveryIssue>());

    private sealed record IndexedSegment(string RelativePath, string SourceId, string State, long? RecordCount,
        long ByteCount, string? Sha256, long? CheckpointSize, long? CheckpointWriteTicks, long? LastFullAuditMs,
        string? TaskRunId, bool HasTaskOutputManifest);
}
