using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
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
    int FailedFiles);

internal sealed record RawCatalogCounts(int Finalized, int Incomplete, int Missing);
internal sealed record TaskRunSnapshot(string Id, string? SourceId, string TaskType, string State, long? StartedMs, long? EndedMs);
internal sealed record RawSegmentInspection(long RecordCount, long FirstReceivedMs, long LastReceivedMs, long ByteCount, string Sha256, bool CorruptTail, long DiscardedTailBytes, string? EvidencePath);

internal sealed class Catalog(RuntimeSettings settings)
{
    private const int MaximumLineBytes = 128 * 1024;
    private string DatabasePath => Path.Combine(settings.DataRoot, "catalog.db");
    public string RawRoot => Path.Combine(settings.DataRoot, "raw");
    public RawRecoverySummary RecoverySummary { get; private set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    public string IntegrityStatus { get; private set; } = "unchecked";

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
        using var db = Open();
        using (var integrity = db.CreateCommand())
        {
            integrity.CommandText = "PRAGMA quick_check(1)";
            var result = integrity.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite catalog integrity check failed; preserving the data root without starting collection.");
        }
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("schema_v1.sql")
            ?? throw new InvalidOperationException("Embedded schema_v1.sql was not found.");
        using var reader = new StreamReader(resource);
        using var command = db.CreateCommand();
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();

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
        interrupted.CommandText = "UPDATE task_runs SET state='INTERRUPTED', ended_ms=$now WHERE state='RUNNING'";
        interrupted.Parameters.AddWithValue("$now", UtcNowMs());
        interrupted.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return db;
    }

    public RawRecoverySummary RecoverRawSegments()
    {
        var indexed = ReadIndexedSegments();
        int missing = 0, recoveredOpen = 0, indexedOrphans = 0, repaired = 0, failed = 0, scanned = 0, evidenceFiles = 0;
        long discarded = 0, evidenceBytes = 0;

        foreach (var row in indexed.Values)
        {
            try
            {
                if (!TryResolveRawPath(row.RelativePath, out var path) || !File.Exists(path))
                {
                    MarkMissing(row.RelativePath);
                    missing++;
                }
                else if (ContainsReparsePoint(path))
                {
                    failed++;
                }
            }
            catch (IOException)
            {
                MarkMissing(row.RelativePath);
                missing++;
            }
        }

        string[] openFiles;
        string[] finalizedFiles;
        try
        {
            openFiles = Directory.EnumerateFiles(RawRoot, "*.open", SearchOption.AllDirectories).ToArray();
            finalizedFiles = Directory.EnumerateFiles(RawRoot, "*.ndjson", SearchOption.AllDirectories).ToArray();
        }
        catch (IOException)
        {
            RecoverySummary = new(0, 0, 0, 0, 0, 0, 0, missing, 1);
            return RecoverySummary;
        }

        foreach (var originalPath in openFiles)
        {
            scanned++;
            try
            {
                if (ContainsReparsePoint(originalPath))
                {
                    failed++;
                    continue;
                }
                var inspection = InspectAndRepair(originalPath);
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

                var publishedPath = Path.ChangeExtension(originalPath, ".ndjson");
                if (File.Exists(publishedPath))
                    publishedPath = Path.Combine(Path.GetDirectoryName(originalPath)!, Path.GetFileNameWithoutExtension(originalPath) + ".recovered-" + Guid.NewGuid().ToString("N") + ".ndjson");
                File.Move(originalPath, publishedPath);
                var relative = RelativeRawPath(publishedPath);
                UpsertRecoveredSegment(relative, inspection, "INCOMPLETE");
                recoveredOpen++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
            {
                failed++;
            }
        }

        foreach (var path in finalizedFiles)
        {
            scanned++;
            try
            {
                if (ContainsReparsePoint(path))
                {
                    failed++;
                    continue;
                }
                var relative = RelativeRawPath(path);
                var inspection = InspectAndRepair(path);
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

                var wasIndexed = indexed.TryGetValue(relative, out var row);
                var sameAsCatalog = wasIndexed && row!.State == "FINALIZED" &&
                    row.RecordCount == inspection.RecordCount && row.ByteCount == inspection.ByteCount &&
                    string.Equals(row.Sha256, inspection.Sha256, StringComparison.OrdinalIgnoreCase);
                var state = sameAsCatalog && !inspection.CorruptTail ? "FINALIZED" :
                    (!wasIndexed && !inspection.CorruptTail ? "FINALIZED" : "INCOMPLETE");
                UpsertRecoveredSegment(relative, inspection, state);
                if (!wasIndexed) indexedOrphans++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
            {
                failed++;
            }
        }

        RecoverySummary = new(scanned, recoveredOpen, indexedOrphans, repaired, discarded, evidenceFiles, evidenceBytes, missing, failed);
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

    public void RegisterFinalizedSegment(string relative, long count, long first, long last, string sha, long bytes)
    {
        UpsertRecoveredSegment(relative, new(count, first, last, bytes, sha, false, 0, null), "FINALIZED");
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
        command.CommandText = @"SELECT id,source_id,task_type,state,started_ms,ended_ms
            FROM task_runs ORDER BY started_ms DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var reader = command.ExecuteReader();
        var result = new List<TaskRunSnapshot>();
        while (reader.Read())
        {
            result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5)));
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

    public int OpenSegmentCount() => Directory.Exists(RawRoot)
        ? Directory.EnumerateFiles(RawRoot, "*.open", SearchOption.AllDirectories).Take(10001).Count()
        : 0;

    private Dictionary<string, IndexedSegment> ReadIndexedSegments()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT relative_path,state,record_count,byte_count,sha256 FROM raw_segments";
        using var reader = command.ExecuteReader();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new Dictionary<string, IndexedSegment>(pathComparer);
        while (reader.Read())
        {
            var relative = reader.GetString(0).Replace('\\', '/');
            result[relative] = new(relative, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4));
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
        command.CommandText = "UPDATE raw_segments SET state='MISSING' WHERE relative_path=$path";
        command.Parameters.AddWithValue("$path", relative);
        command.ExecuteNonQuery();
    }

    private void UpsertRecoveredSegment(string relative, RawSegmentInspection info, string state)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO raw_segments
            (id,source_id,relative_path,state,first_received_ms,last_received_ms,record_count,byte_count,sha256,sealed_ms)
            VALUES($id,'syslog-local',$path,$state,$first,$last,$count,$bytes,$sha,$now)
            ON CONFLICT(relative_path) DO UPDATE SET
              state=excluded.state, first_received_ms=excluded.first_received_ms,
              last_received_ms=excluded.last_received_ms, record_count=excluded.record_count,
              byte_count=excluded.byte_count, sha256=excluded.sha256,
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
            Math.Max(0, originalLength - byteCount), evidencePath);
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

    private sealed record IndexedSegment(string RelativePath, string State, long? RecordCount, long ByteCount, string? Sha256);
}
