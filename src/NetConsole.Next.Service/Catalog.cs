using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

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
    long? QueuedMs, long? StartedMs, long? DeadlineMs, long? EndedMs, string? FailureReason, string? DefinitionId);
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
    long BytesRead, long BytesHashed);

internal sealed class Catalog(RuntimeSettings settings)
{
    private const int MaximumLineBytes = 128 * 1024;
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
                    trusted++;
                    continue;
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

        timer.Stop();
        RecoverySummary = new(scanned, recoveredOpen, indexedOrphans, repaired, discarded, evidenceFiles,
            evidenceBytes, missing, failed, bytesRead, bytesHashed, trusted, timer.ElapsedMilliseconds, issues);
        return RecoverySummary;
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

    public void CreateTaskDefinition(string id, string taskType, string target, string command,
        TimeSpan timeout, bool enabled = true, string restartPolicy = "NEVER")
    {
        if (taskType != "SIMULATED_DEVICE_ECHO")
            throw new ArgumentException("Only simulated device tasks are enabled in this G1 build.", nameof(taskType));
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)) throw new ArgumentException("Task definition id is invalid.", nameof(id));
        if (string.IsNullOrWhiteSpace(target) || target.Length > 253 || target.Any(char.IsControl)) throw new ArgumentException("Task target is invalid.", nameof(target));
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4096 || command.Contains('\r') || command.Contains('\n')) throw new ArgumentException("Task command must be one line.", nameof(command));
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (restartPolicy != "NEVER") throw new ArgumentException("G1 restart policy only supports NEVER; interrupted runs require an explicit retry.", nameof(restartPolicy));
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
        if (!definition.Enabled) throw new InvalidOperationException("Disabled task definitions cannot be scheduled.");
        var id = Guid.NewGuid().ToString("N");
        var now = UtcNowMs();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO task_runs(id,source_id,task_type,state,queued_ms,deadline_ms,definition_id)
            VALUES($id,NULL,$type,'QUEUED',$now,NULL,$definition)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$type", definition.TaskType);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$definition", definitionId);
        command.ExecuteNonQuery();
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
            WHERE id=$id AND ((state='QUEUED' AND $next IN ('CONNECTING','CANCELLED')) OR
              (state='CONNECTING' AND $next IN ('RUNNING','FAILED','TIMED_OUT','CANCELLED')) OR
              (state='RUNNING' AND $next IN ('SUCCEEDED','FAILED','TIMED_OUT','CANCELLED')))";
        command.Parameters.AddWithValue("$next", nextState);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$reason", (object?)failureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void RegisterFinalizedSegment(string relative, long count, long first, long last, string sha, long bytes)
    {
        UpsertRecoveredSegment(relative, new(count, first, last, bytes, sha, false, 0, null, 0, 0), "FINALIZED");
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
        command.CommandText = @"SELECT id,source_id,task_type,state,queued_ms,started_ms,deadline_ms,ended_ms,failure_reason,definition_id
            FROM task_runs ORDER BY COALESCE(queued_ms,started_ms) DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var reader = command.ExecuteReader();
        var result = new List<TaskRunSnapshot>();
        while (reader.Read())
        {
            result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
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

    public string? AuditOneFinalizedSegment()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT relative_path,record_count,byte_count,sha256 FROM raw_segments
            WHERE state='FINALIZED' ORDER BY COALESCE(last_full_audit_ms,0),relative_path LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var row = new IndexedSegment(reader.GetString(0), "FINALIZED",
            reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), null, null, null);
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
                return $"{row.RelativePath}: checksum/record integrity mismatch; file marked INCOMPLETE";
            }

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
            return $"{row.RelativePath}: full integrity audit passed ({inspection.BytesRead} bytes read)";
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
            return $"{row.RelativePath}: full integrity audit failed ({ex.GetType().Name})";
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
        command.CommandText = "SELECT relative_path,state,record_count,byte_count,sha256,checkpoint_size,checkpoint_write_ticks,last_full_audit_ms FROM raw_segments";
        using var reader = command.ExecuteReader();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new Dictionary<string, IndexedSegment>(pathComparer);
        while (reader.Read())
        {
            var relative = reader.GetString(0).Replace('\\', '/');
            result[relative] = new(relative, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7));
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
        var filePath = Path.Combine(settings.DataRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        var file = new FileInfo(filePath);
        var checkpointSize = file.Exists ? file.Length : info.ByteCount;
        var checkpointTicks = file.Exists ? file.LastWriteTimeUtc.Ticks : 0;
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO raw_segments
            (id,source_id,relative_path,state,first_received_ms,last_received_ms,record_count,byte_count,sha256,sealed_ms,
             checkpoint_size,checkpoint_write_ticks,last_verified_ms)
            VALUES($id,'syslog-local',$path,$state,$first,$last,$count,$bytes,$sha,$now,$size,$ticks,$now)
            ON CONFLICT(relative_path) DO UPDATE SET
              state=excluded.state, first_received_ms=excluded.first_received_ms,
              last_received_ms=excluded.last_received_ms, record_count=excluded.record_count,
              byte_count=excluded.byte_count, sha256=excluded.sha256,
              checkpoint_size=excluded.checkpoint_size,checkpoint_write_ticks=excluded.checkpoint_write_ticks,
              last_verified_ms=excluded.last_verified_ms,
              sealed_ms=COALESCE(raw_segments.sealed_ms,excluded.sealed_ms)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
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
        command.ExecuteNonQuery();
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
              AND a.reason IN ('MISSING_RAW','REPARSE_POINT','HISTORICAL_RAW','OPEN_RECOVERY_FAILED','FINALIZED_RECOVERY_FAILED')";
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
                    if (!TryReadRecord(lineBytes, out var timestamp))
                    {
                        corrupt = true;
                        goto Finished;
                    }
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
        return new(recordCount, recordCount == 0 ? 0 : first, last, byteCount, hash, corrupt,
            Math.Max(0, originalLength - byteCount), evidencePath, originalLength, byteCount);
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

    private static bool TryReadRecord(byte[] line, out long timestamp)
    {
        timestamp = 0;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("received_ms", out var received) || !received.TryGetInt64(out timestamp) ||
                !root.TryGetProperty("sender_ip", out var sender) || sender.ValueKind != JsonValueKind.String ||
                !System.Net.IPAddress.TryParse(sender.GetString(), out _) ||
                !root.TryGetProperty("sender_port", out var port) || !port.TryGetInt32(out var senderPort) || senderPort is < 0 or > 65535 ||
                !root.TryGetProperty("payload_base64", out var payload) || payload.ValueKind != JsonValueKind.String)
                return false;
            _ = Convert.FromBase64String(payload.GetString() ?? string.Empty);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static RawRecoverySummary EmptyRecovery() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<RecoveryIssue>());

    private sealed record IndexedSegment(string RelativePath, string State, long? RecordCount, long ByteCount,
        string? Sha256, long? CheckpointSize, long? CheckpointWriteTicks, long? LastFullAuditMs);
}
