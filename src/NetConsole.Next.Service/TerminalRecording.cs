using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service;

// Test-only fault seams. Production callers leave this null; the callbacks let
// the service tests reproduce publication interruption without locking or
// mutating an unrelated data root.
internal sealed class TerminalRecordingFaults
{
    public Func<string, Stream>? CreateStream { get; init; }
    public Action<string, string>? BeforeSegmentPublish { get; init; }
    public Action<string>? BeforeIndexPublish { get; init; }
}

internal sealed class TerminalRecordingWriter : IAsyncDisposable
{
    private const long MaximumSessionBytes = 64L * 1024 * 1024;
    private const long MaximumSegmentBytes = 8L * 1024 * 1024;
    private readonly string _sessionId;
    private readonly string _recordingDirectory;
    private readonly string _relativeIndexPath;
    private readonly TerminalRecordingFaults? _faults;
    private readonly IncrementalHash _overallHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<TerminalRecordingSegmentResult> _segments = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Stream? _stream;
    private IncrementalHash? _segmentHash;
    private string? _openPath;
    private string? _segmentRelativePath;
    private long _segmentStartedMs;
    private long _segmentEndedMs;
    private long _segmentBytes;
    private long _segmentRecords;
    private long _bytes;
    private long _inputCount;
    private long _outputBytes;
    private int _segmentNumber;
    private int _failed;
    private int _completionRequested;
    private bool _completionCanBeComplete;
    private TerminalRecordingResult? _result;

    public TerminalRecordingWriter(string dataRoot, string sessionId, TerminalRecordingFaults? faults = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_')))
            throw new ArgumentException("The terminal session id is not a safe file name.", nameof(sessionId));
        var fullRoot = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(fullRoot);
        EnsureNotReparse(fullRoot);
        var recordingsRoot = Path.Combine(fullRoot, "terminal-recordings");
        if (!Directory.Exists(recordingsRoot)) Directory.CreateDirectory(recordingsRoot);
        EnsureNotReparse(recordingsRoot);
        _sessionId = sessionId;
        _faults = faults;
        _recordingDirectory = Path.Combine(recordingsRoot, sessionId);
        Directory.CreateDirectory(_recordingDirectory);
        EnsureNotReparse(_recordingDirectory);
        _relativeIndexPath = $"terminal-recordings/{sessionId}/index.json";
        OpenNextSegment();
    }

    public async ValueTask AppendAsync(long atMs, string direction, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        if (direction is not ("input" or "output")) throw new ArgumentException("Invalid recording direction.", nameof(direction));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            at_ms = atMs,
            direction,
            data_b64 = Convert.ToBase64String(bytes.Span)
        });
        var line = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, line, 0, payload.Length);
        line[^1] = (byte)'\n';

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _failed) != 0) return;
            if (Volatile.Read(ref _completionRequested) != 0)
                throw new InvalidOperationException("The terminal recording has already started completion.");
            if (_bytes + line.Length > MaximumSessionBytes)
            {
                Volatile.Write(ref _failed, 1);
                throw new IOException("Terminal recording reached its bounded session size limit.");
            }
            if (_segmentBytes > 0 && _segmentBytes + line.Length > MaximumSegmentBytes)
                await SealCurrentAsync(complete: true, cancellationToken).ConfigureAwait(false);
            await WriteCurrentAsync(line, cancellationToken).ConfigureAwait(false);
            _overallHash.AppendData(line);
            _segmentHash!.AppendData(line);
            _bytes += line.Length;
            _segmentBytes += line.Length;
            _segmentRecords++;
            if (_segmentStartedMs == 0) _segmentStartedMs = atMs;
            _segmentEndedMs = atMs;
            if (direction == "input") _inputCount++;
            else _outputBytes += bytes.Length;
        }
        finally { _gate.Release(); }
    }

    public void MarkIncomplete() => Volatile.Write(ref _failed, 1);

    public string RelativePath => _relativeIndexPath;

    public async ValueTask<TerminalRecordingResult> CompleteAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _completionRequested, 1) == 0)
                _completionCanBeComplete = Volatile.Read(ref _failed) == 0;
            if (_result is not null)
            {
                // The recording is already sealed.  An index publication failure must
                // remain retryable on the same writer and must not be mistaken for a
                // successful completion merely because the payload is durable.
                await WriteIndexAsync(_result, cancellationToken).ConfigureAwait(false);
                return _result;
            }
            if (_stream is not null || (_openPath is not null && File.Exists(_openPath)))
                await SealCurrentAsync(_completionCanBeComplete && Volatile.Read(ref _failed) == 0, cancellationToken).ConfigureAwait(false);
            _result = BuildResult();
            await WriteIndexAsync(_result, cancellationToken).ConfigureAwait(false);
            return _result;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stream is not null)
            {
                try { await _stream.FlushAsync().ConfigureAwait(false); } catch { }
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }
            _segmentHash?.Dispose();
            _segmentHash = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _overallHash.Dispose();
        }
    }

    private async ValueTask WriteCurrentAsync(byte[] line, CancellationToken cancellationToken)
    {
        if (_stream is null) throw new ObjectDisposedException(nameof(TerminalRecordingWriter));
        try { await _stream.WriteAsync(line, cancellationToken).ConfigureAwait(false); }
        catch { Volatile.Write(ref _failed, 1); throw; }
    }

    private void OpenNextSegment()
    {
        _segmentNumber++;
        var name = $"segment-{_segmentNumber:000000}.jsonl";
        _segmentRelativePath = $"terminal-recordings/{_sessionId}/{name}";
        _openPath = Path.Combine(_recordingDirectory, name + ".open");
        _stream = _faults?.CreateStream?.Invoke(_openPath) ?? new FileStream(_openPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        _segmentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _segmentStartedMs = 0;
        _segmentEndedMs = 0;
        _segmentBytes = 0;
        _segmentRecords = 0;
    }

    private async ValueTask SealCurrentAsync(bool complete, CancellationToken cancellationToken)
    {
        if (_openPath is null || _segmentRelativePath is null || _segmentHash is null) return;
        var stream = _stream;
        var openPath = _openPath;
        var finalPath = openPath[..^".open".Length];
        var streamClosed = stream is null;
        try
        {
            if (stream is not null)
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream is FileStream fileStream) fileStream.Flush(flushToDisk: true);
                await stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
                streamClosed = true;
            }
            else if (!File.Exists(openPath) && !File.Exists(finalPath))
            {
                throw new FileNotFoundException("Terminal recording segment disappeared before publication.", openPath);
            }

            _faults?.BeforeSegmentPublish?.Invoke(openPath, finalPath);
            if (File.Exists(openPath))
            {
                // Never replace an existing segment.  A same-name collision is
                // retained as evidence and must be resolved by an operator or a
                // recovery policy, not silently deleted by the recorder.
                if (File.Exists(finalPath))
                    throw new IOException($"Terminal recording segment target already exists: {finalPath}");
                File.Move(openPath, finalPath);
            }
            else if (!File.Exists(finalPath))
            {
                throw new FileNotFoundException("Terminal recording segment disappeared before publication.", openPath);
            }

            if (!_segments.Any(item => item.Number == _segmentNumber &&
                    string.Equals(item.RelativePath, _segmentRelativePath, StringComparison.Ordinal)))
            {
                _segments.Add(new TerminalRecordingSegmentResult(_segmentNumber, _segmentRelativePath,
                    _segmentStartedMs == 0 ? _segmentEndedMs : _segmentStartedMs, _segmentEndedMs,
                    _segmentBytes, _segmentRecords,
                    Convert.ToHexString(_segmentHash.GetHashAndReset()).ToLowerInvariant(), complete));
            }
            _segmentHash.Dispose();
            _segmentHash = null;
            if (!complete) Volatile.Write(ref _failed, 1);
            if (Volatile.Read(ref _completionRequested) == 0) OpenNextSegment();
            else
            {
                _openPath = null;
                _segmentRelativePath = null;
            }
        }
        catch
        {
            if (!streamClosed) Volatile.Write(ref _failed, 1);
            // Keep a still-open stream available for a retry when flushing failed.
            // Once the stream was successfully closed, the .open file remains on
            // disk and the next CompleteAsync call can publish it without loss.
            if (stream is not null && ReferenceEquals(_stream, stream))
            {
                try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
                _stream = null;
            }
            throw;
        }
    }

    private TerminalRecordingResult BuildResult()
    {
        if (_result is not null) return _result;
        var complete = Volatile.Read(ref _failed) == 0;
        var hash = Convert.ToHexString(_overallHash.GetHashAndReset()).ToLowerInvariant();
        return _result = new(_relativeIndexPath, _bytes, hash, complete, _segments.ToArray(), _inputCount, _outputBytes);
    }

    private async ValueTask WriteIndexAsync(TerminalRecordingResult result, CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(_recordingDirectory, "index.json");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 7,
            session_id = _sessionId,
            bytes = result.Bytes,
            sha256 = result.Sha256,
            complete = result.Complete,
            segments = result.Segments
        });
        var temporaryPath = indexPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (File.Exists(indexPath))
            {
                try
                {
                    var existing = await File.ReadAllBytesAsync(indexPath, cancellationToken).ConfigureAwait(false);
                    if (existing.AsSpan().SequenceEqual(payload)) return;
                }
                catch (IOException)
                {
                    // Fall through to the atomic replacement path.  A transient
                    // read failure must not make a durable index unrecoverable.
                }
            }
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _faults?.BeforeIndexPublish?.Invoke(indexPath);
            PublishIndexAtomically(temporaryPath, indexPath);
        }
        finally
        {
            // A failed publication leaves the previous index and all segment files
            // untouched.  Only the private, incomplete temporary index is eligible
            // for cleanup.
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private static void PublishIndexAtomically(string temporaryPath, string indexPath)
    {
        if (!File.Exists(indexPath))
        {
            File.Move(temporaryPath, indexPath);
            return;
        }

        // Preserve every previous index as evidence.  File.Replace publishes the
        // complete flushed temporary file atomically on supported local filesystems.
        var backupPath = indexPath + ".previous-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" +
            Guid.NewGuid().ToString("N");
        File.Replace(temporaryPath, indexPath, backupPath, ignoreMetadataErrors: true);
    }

    private static void EnsureNotReparse(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Terminal recording path cannot be a reparse point.");
    }
}
internal static class RestrictedReadonlyInputPolicy
{
    public static bool TryNormalize(string? input, out string command, out string reason)
    {
        var raw = input ?? string.Empty;
        command = raw.Trim();
        reason = string.Empty;
        if (raw.Any(char.IsControl))
        {
            reason = "Control characters are not allowed.";
            return false;
        }
        if (command.Length == 0 || command.Length > 256)
        {
            reason = "Command must contain 1-256 characters.";
            return false;
        }
        if (command.Contains(';') || command.Contains('|') || command.Contains('&') || command.Contains('`') ||
            command.Contains('$') || command.Contains('>') || command.Contains('<') || command.Contains('\\'))
        {
            reason = "Control characters and command composition are not allowed.";
            return false;
        }
        if (!H3cCommandPolicy.IsAllowed(command))
        {
            reason = "The RESTRICTED_READONLY command is not allowlisted.";
            return false;
        }
        return true;
    }
}
internal static class TerminalRecordingReader
{
    public static (IReadOnlyList<TerminalRecordingEntry> Entries, bool HasMore) ReadPage(
        string dataRoot, TerminalSessionSnapshot session, IReadOnlyList<TerminalRecordingSegmentSnapshot> segments,
        long offset, int limit)
    {
        if (segments.Count == 0)
        {
            var legacy = ReadEntries(dataRoot, session, segments).Skip(checked((int)Math.Min(offset, int.MaxValue)))
                .Take(limit + 1).ToList();
            return TrimPage(legacy, limit);
        }

        var ordered = segments.OrderBy(item => item.Number).ToArray();
        var remaining = offset;
        var skipped = 0L;
        var firstSegment = 0;
        while (firstSegment < ordered.Length)
        {
            var recordCount = Math.Max(0, ordered[firstSegment].RecordCount);
            if (remaining < recordCount) break;
            remaining -= recordCount;
            skipped += recordCount;
            firstSegment++;
        }

        if (firstSegment == ordered.Length) return (Array.Empty<TerminalRecordingEntry>(), false);

        var page = new List<TerminalRecordingEntry>(Math.Min(limit + 1, 2048));
        var position = skipped;
        for (var index = firstSegment; index < ordered.Length; index++)
        {
            foreach (var entry in ReadEntries(dataRoot, session, [ordered[index]]))
            {
                var globalPosition = position++;
                if (globalPosition < offset) continue;
                page.Add(entry with { Position = globalPosition });
                if (page.Count > limit) return TrimPage(page, limit);
            }
        }
        return TrimPage(page, limit);
    }

    private static (IReadOnlyList<TerminalRecordingEntry> Entries, bool HasMore) TrimPage(
        List<TerminalRecordingEntry> entries, int limit)
    {
        var hasMore = entries.Count > limit;
        if (hasMore) entries.RemoveAt(limit);
        return (entries, hasMore);
    }

    public static IEnumerable<TerminalRecordingEntry> ReadEntries(string dataRoot,
        TerminalSessionSnapshot session, IReadOnlyList<TerminalRecordingSegmentSnapshot> segments)
    {
        var source = segments.Count > 0
            ? segments.OrderBy(item => item.Number).Select(item => (item.Number, item.RelativePath))
            : session.RecordingRelativePath is null
                ? Enumerable.Empty<(int Number, string RelativePath)>()
                : [(0, session.RecordingRelativePath)];
        long position = 0;
        foreach (var (number, relativePath) in source)
        {
            var path = ResolvePath(dataRoot, relativePath);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                if (!TryParseEntry(line, out var atMs, out var direction, out var data, out var error))
                {
                    if (relativePath.EndsWith(".open", StringComparison.OrdinalIgnoreCase) && reader.EndOfStream)
                    {
                        // A process can stop after writing a partial final JSONL record. Preserve and replay
                        // the verified prefix, while the segment remains explicitly INCOMPLETE.
                        yield break;
                    }
                    throw error!;
                }
                yield return new(position++, number, atMs, direction, data);
            }
        }
    }

    private static bool TryParseEntry(string line, out long atMs, out string direction, out byte[] data,
        out Exception? error)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            atMs = root.GetProperty("at_ms").GetInt64();
            direction = root.GetProperty("direction").GetString() ?? throw new InvalidDataException("Recording direction is missing.");
            if (direction is not ("input" or "output")) throw new InvalidDataException("Recording direction is invalid.");
            data = Convert.FromBase64String(root.GetProperty("data_b64").GetString() ?? string.Empty);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            atMs = 0;
            direction = string.Empty;
            data = [];
            error = ex;
            return false;
        }
    }

    public static TerminalRecordingFileInspection Inspect(string dataRoot, TerminalSessionSnapshot session,
        TerminalRecordingSegmentSnapshot segment)
    {
        var path = ResolvePath(dataRoot, segment.RelativePath);
        long records = 0;
        long started = 0;
        long ended = 0;
        foreach (var entry in ReadEntries(dataRoot, session, [segment]))
        {
            if (records == 0) started = entry.AtMs;
            ended = entry.AtMs;
            records++;
        }
        var fingerprint = Fingerprint(path);
        return new(records, started, ended, fingerprint.Bytes, fingerprint.Sha256);
    }

    public static (long Bytes, string Sha256) Fingerprint(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, count);
            bytes += count;
        }
        return (bytes, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public static string ResolvePath(string dataRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Recording path must be relative.");
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(dataRoot);
        EnsureNotReparse(fullRoot);
        var recordingsRoot = Path.GetFullPath(Path.Combine(fullRoot, "terminal-recordings"));
        EnsureNotReparse(recordingsRoot);
        var path = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        if (!path.StartsWith(recordingsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recording path escapes the controlled recording root.");
        var current = recordingsRoot;
        foreach (var part in path[recordingsRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Recording path contains a reparse point.");
        }
        return path;
    }

    private static void EnsureNotReparse(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Recording path cannot be a reparse point.");
    }
}
