using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record SyslogPacket(long ReceivedMs, string SenderIp, int SenderPort, byte[] Payload);

internal interface IRawSegmentWriterFactory
{
    RawSegmentWriter Create(Catalog catalog);
}

internal sealed class RawSegmentWriterFactory : IRawSegmentWriterFactory
{
    public RawSegmentWriter Create(Catalog catalog) => new(catalog);

    public RawSegmentWriter CreateH3c(Catalog catalog, string profileId) =>
        new(catalog, "h3c-" + profileId, Path.Combine("h3c", profileId));
}

// Append-only P0 raw writer. Roll at 4MiB, seal before indexing; retain files on catalog errors.
internal sealed class RawSegmentWriter
{
    private readonly Catalog _catalog;
    private readonly string _openPath;
    private readonly string _relativePath;
    private readonly string _sourceId;
    private readonly Stream _stream;
    private string? _taskRunId;
    private readonly MemoryStream _stagedBytes = new(256 * 1024);
    private readonly ArrayBufferWriter<byte> _jsonBytes = new(4096);
    private long _first = long.MaxValue;
    private long _last, _records, _bytes;
    private bool _sealed, _closed, _dirty;

    public long Bytes => _bytes;
    public string RelativePath => _relativePath;
    public string? Sha256 { get; private set; }
    public double LastSyncDurationMs { get; private set; }
    public double LastSealDurationMs { get; private set; }
    public double LastCatalogWriteDurationMs { get; private set; }

    public RawSegmentWriter(Catalog catalog, string sourceId = "syslog-local", string category = "syslog",
        Func<string, Stream>? streamFactory = null)
    {
        _catalog = catalog;
        if (sourceId == "syslog-local")
        {
            if (category != "syslog") throw new ArgumentException("Syslog Raw must use the syslog folder.", nameof(category));
        }
        else if (sourceId.StartsWith("h3c-", StringComparison.Ordinal) &&
                 Guid.TryParseExact(sourceId[4..], "N", out var profileId) &&
                 category.Replace('\\', '/') == $"h3c/{profileId:N}")
        {
            category = Path.Combine("h3c", profileId.ToString("N"));
        }
        else throw new ArgumentException("Raw source id and category do not identify a supported source.", nameof(sourceId));
        _sourceId = sourceId;
        var now = DateTimeOffset.UtcNow;
        var folderRelative = Path.Combine(category, now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
        var folder = Path.Combine(catalog.RawRoot, folderRelative);
        Directory.CreateDirectory(folder);
        var name = now.ToString("HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        _openPath = Path.Combine(folder, name + ".open");
        _relativePath = Path.Combine("raw", folderRelative, name + ".ndjson");
        _stream = streamFactory is null
            ? new FileStream(_openPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32768, FileOptions.Asynchronous)
            : streamFactory(_openPath);
    }

    public RawSegmentWriter(Catalog catalog, Func<string, Stream> streamFactory)
        : this(catalog, "syslog-local", "syslog", streamFactory) { }

    public Task AppendAsync(SyslogPacket packet)
    {
        if (_sealed) throw new InvalidOperationException("A sealed Raw segment cannot be changed.");
        _jsonBytes.Clear();
        using (var json = new Utf8JsonWriter(_jsonBytes))
        {
            json.WriteStartObject();
            json.WriteNumber("received_ms", packet.ReceivedMs);
            json.WriteString("sender_ip", packet.SenderIp);
            json.WriteNumber("sender_port", packet.SenderPort);
            json.WriteBase64String("payload_base64", packet.Payload);
            json.WriteEndObject();
        }
        _stagedBytes.Write(_jsonBytes.WrittenSpan);
        _stagedBytes.WriteByte((byte)'\n');
        _bytes += _jsonBytes.WrittenCount + 1;
        _dirty = true;
        _first = Math.Min(_first, packet.ReceivedMs);
        _last = Math.Max(_last, packet.ReceivedMs);
        _records++;
        return Task.CompletedTask;
    }

    public byte[] AppendH3cCommand(string profileId, string taskRunId, string host, string command,
        string output, long capturedMs, string captureStatus)
    {
        if (_sourceId != "h3c-" + profileId || !Guid.TryParseExact(profileId, "N", out _) ||
            !Guid.TryParseExact(taskRunId, "N", out _))
            throw new ArgumentException("H3C profile/task identity does not match the Raw source.");
        if (!H3cCommandPolicy.IsAllowed(command)) throw new ArgumentException("H3C command is not allowlisted.", nameof(command));
        if (captureStatus is not ("COMPLETE" or "PARTIAL")) throw new ArgumentException("H3C capture status is invalid.", nameof(captureStatus));
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsControl))
            throw new ArgumentException("H3C host is invalid.", nameof(host));
        if (_sealed || _records != 0) throw new InvalidOperationException("An H3C result segment accepts exactly one command result.");
        _taskRunId = taskRunId;
        var outputBytes = Encoding.UTF8.GetBytes(output);
        if (outputBytes.Length > H3cRawOutput.MaximumBytes)
            throw new InvalidDataException($"H3C output exceeds the {H3cRawOutput.MaximumBytes}-byte storage limit.");
        var outputSha = SHA256.HashData(outputBytes);
        var outputShaHex = Convert.ToHexString(outputSha).ToLowerInvariant();
        _jsonBytes.Clear();
        using (var json = new Utf8JsonWriter(_jsonBytes))
        {
            json.WriteStartObject();
            json.WriteString("record_type", "h3c_command");
            json.WriteNumber("captured_ms", capturedMs);
            json.WriteString("source_id", _sourceId);
            json.WriteString("device_profile_id", profileId);
            json.WriteString("task_run_id", taskRunId);
            json.WriteString("device_host", host);
            json.WriteString("command", command);
            json.WriteString("capture_status", captureStatus);
            json.WriteNumber("output_byte_count", outputBytes.Length);
            json.WriteString("output_sha256", outputShaHex);
            json.WriteBase64String("output_utf8_base64", outputBytes);
            json.WriteEndObject();
        }
        _stagedBytes.Write(_jsonBytes.WrittenSpan);
        _stagedBytes.WriteByte((byte)'\n');
        _bytes = _jsonBytes.WrittenCount + 1;
        _dirty = true;
        _first = _last = capturedMs;
        _records = 1;
        return outputSha;
    }

    public async Task FlushAsync()
    {
        if (_closed) throw new InvalidOperationException("A closed Raw segment cannot be flushed.");
        var started = Stopwatch.GetTimestamp();
        if (_stagedBytes.Length > 0)
        {
            _stagedBytes.Position = 0;
            await _stagedBytes.CopyToAsync(_stream);
            _stagedBytes.SetLength(0);
            _stagedBytes.Position = 0;
        }
        await _stream.FlushAsync();
        if (_stream is FileStream file) file.Flush(flushToDisk: true);
        else _stream.Flush();
        LastSyncDurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _dirty = false;
    }

    public async Task PreserveOpenAsync()
    {
        if (_closed) return;
        try { await FlushAsync(); }
        catch (IOException) { }
        try { await _stream.DisposeAsync(); }
        catch (IOException) { }
        finally { _closed = true; }
    }

    public async Task SealAsync()
    {
        if (_sealed) return;
        var sealStarted = Stopwatch.GetTimestamp();
        if (_dirty) await FlushAsync();
        await _stream.DisposeAsync();
        _closed = true;
        string checksum;
        using (var sha = SHA256.Create())
        using (var input = File.OpenRead(_openPath))
            checksum = Convert.ToHexString(sha.ComputeHash(input)).ToLowerInvariant();
        Sha256 = checksum;
        var published = _openPath[..^".open".Length] + ".ndjson";
        File.Move(_openPath, published);
        // If the DB write fails, the final file is deliberately preserved for reconciliation.
        var catalogStarted = Stopwatch.GetTimestamp();
        _catalog.RegisterFinalizedSegment(_relativePath, _records,
            _first == long.MaxValue ? 0 : _first, _last, checksum, new FileInfo(published).Length,
            _sourceId, _taskRunId);
        LastCatalogWriteDurationMs = Stopwatch.GetElapsedTime(catalogStarted).TotalMilliseconds;
        LastSealDurationMs = Stopwatch.GetElapsedTime(sealStarted).TotalMilliseconds;
        _sealed = true;
    }

}

internal static class H3cRawOutput
{
    public const int MaximumBytes = 256 * 1024;
}
