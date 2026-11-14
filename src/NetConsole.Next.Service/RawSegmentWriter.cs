using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.ExceptionServices;

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

    public RawSegmentWriter CreateFitAp(Catalog catalog, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId) || resourceId.Any(char.IsControl))
            throw new ArgumentException("FIT-AP resource id is invalid.", nameof(resourceId));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resourceId))).ToLowerInvariant()[..24];
        return new(catalog, "fit-ap:" + resourceId, Path.Combine("fit-ap", key));
    }
}

// Append-only P0 raw writer. Roll at 4MiB, seal before indexing; retain files on catalog errors.
internal sealed class RawSegmentWriter
{
    private enum SegmentState
    {
        Open,
        PreservedIncomplete,
        FilePublished,
        CatalogRegistered
    }

    private readonly Catalog _catalog;
    private readonly string _openPath;
    private readonly string _relativePath;
    private readonly string _sourceId;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private string? _taskRunId;
    private readonly MemoryStream _stagedBytes = new(256 * 1024);
    private readonly ArrayBufferWriter<byte> _jsonBytes = new(4096);
    private long _first = long.MaxValue;
    private long _last, _records, _bytes;
    private string? _checksum;
    private long? _publishedBytes;
    private SegmentState _state = SegmentState.Open;
    private bool _streamDisposed;

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
        else if (sourceId.StartsWith("fit-ap:", StringComparison.Ordinal) &&
                 category.Replace('\\', '/').StartsWith("fit-ap/", StringComparison.Ordinal))
        {
            category = Path.Combine("fit-ap", category.Replace('\\', '/')["fit-ap/".Length..]);
        }
        else throw new ArgumentException("Raw source id and category do not identify a supported source.", nameof(sourceId));
        _sourceId = sourceId;
        var now = DateTimeOffset.UtcNow;
        var folderRelative = Path.Combine(category, now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
        var folder = Path.Combine(catalog.RawRoot, folderRelative);
        Directory.CreateDirectory(folder);
        var name = now.ToString("HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        _openPath = Path.Combine(folder, name + ".open");
        _relativePath = Path.Combine("raw", folderRelative, name + ".ndjson").Replace('\\', '/');
        _stream = streamFactory is null
            ? new FileStream(_openPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32768, FileOptions.Asynchronous)
            : streamFactory(_openPath);
    }

    public RawSegmentWriter(Catalog catalog, Func<string, Stream> streamFactory)
        : this(catalog, "syslog-local", "syslog", streamFactory) { }

    public async Task AppendAsync(SyslogPacket packet)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureOpenForAppend();
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
            _first = Math.Min(_first, packet.ReceivedMs);
            _last = Math.Max(_last, packet.ReceivedMs);
            _records++;
        }
        finally { _operationGate.Release(); }
    }

    public byte[] AppendH3cCommand(string profileId, string taskRunId, string host, string command,
        string output, long capturedMs, string captureStatus)
    {
        _operationGate.Wait();
        try
        {
            if (_sourceId != "h3c-" + profileId || !Guid.TryParseExact(profileId, "N", out _) ||
                !Guid.TryParseExact(taskRunId, "N", out _))
                throw new ArgumentException("H3C profile/task identity does not match the Raw source.");
            if (!H3cCommandPolicy.IsAllowed(command)) throw new ArgumentException("H3C command is not allowlisted.", nameof(command));
            if (captureStatus is not ("COMPLETE" or "PARTIAL")) throw new ArgumentException("H3C capture status is invalid.", nameof(captureStatus));
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsControl))
                throw new ArgumentException("H3C host is invalid.", nameof(host));
            EnsureOpenForAppend();
            if (_records != 0) throw new InvalidOperationException("An H3C result segment accepts exactly one command result.");
            _taskRunId = taskRunId;
            var outputBytes = Encoding.UTF8.GetBytes(output);
            if (outputBytes.Length > H3cRawOutput.MaximumBytesFor(command))
                throw new InvalidDataException($"H3C output exceeds the {H3cRawOutput.MaximumBytesFor(command)}-byte storage limit.");
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
            _first = _last = capturedMs;
            _records = 1;
            return outputSha;
        }
        finally { _operationGate.Release(); }
    }

    public byte[] AppendFitApCommand(string resourceId, string captureId, string sourceAcDeviceId,
        string siteId, string apName, string host, string command, string output, long capturedMs,
        string captureStatus)
    {
        _operationGate.Wait();
        try
        {
            if (_sourceId != "fit-ap:" + resourceId || !Guid.TryParseExact(captureId, "N", out _) ||
                !H3cCommandPolicy.IsAllowed(command))
                throw new ArgumentException("FIT-AP Raw identity or command is invalid.");
            if (captureStatus is not ("COMPLETE" or "PARTIAL"))
                throw new ArgumentException("FIT-AP capture status is invalid.");
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsControl))
                throw new ArgumentException("FIT-AP host is invalid.", nameof(host));
            _catalog.EnsureFitApRawSource(_sourceId, resourceId, siteId);
            EnsureOpenForAppend();
            if (_records != 0) throw new InvalidOperationException("A FIT-AP result segment accepts exactly one command result.");
            var outputBytes = Encoding.UTF8.GetBytes(H3cDiagnosticCapture.Sanitize(output));
            if (outputBytes.Length > H3cRawOutput.MaximumBytesFor(command))
                throw new InvalidDataException("FIT-AP output exceeds the supported Raw limit.");
            var outputSha = SHA256.HashData(outputBytes);
            _jsonBytes.Clear();
            using (var json = new Utf8JsonWriter(_jsonBytes))
            {
                json.WriteStartObject();
                json.WriteString("record_type", "fit_ap_command");
                json.WriteNumber("captured_ms", capturedMs);
                json.WriteString("source_id", _sourceId);
                json.WriteString("resource_id", resourceId);
                json.WriteString("source_ac_device_id", sourceAcDeviceId);
                json.WriteString("site_id", siteId);
                json.WriteString("ap_name", apName);
                json.WriteString("task_run_id", captureId);
                json.WriteString("device_host", host);
                json.WriteString("command", command);
                json.WriteString("capture_status", captureStatus);
                json.WriteNumber("output_byte_count", outputBytes.Length);
                json.WriteString("output_sha256", Convert.ToHexString(outputSha).ToLowerInvariant());
                json.WriteBase64String("output_utf8_base64", outputBytes);
                json.WriteEndObject();
            }
            _stagedBytes.Write(_jsonBytes.WrittenSpan);
            _stagedBytes.WriteByte((byte)'\n');
            _bytes = _jsonBytes.WrittenCount + 1;
            _first = _last = capturedMs;
            _records = 1;
            return outputSha;
        }
        finally { _operationGate.Release(); }
    }

    public async Task FlushAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try { await FlushCoreAsync().ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }

    private async Task FlushCoreAsync()
    {
        if (_state is SegmentState.FilePublished or SegmentState.CatalogRegistered) return;
        if (_state == SegmentState.PreservedIncomplete)
            throw new InvalidOperationException("A preserved incomplete Raw segment cannot be flushed.");
        var started = Stopwatch.GetTimestamp();
        if (_stagedBytes.Length > 0)
        {
            _stagedBytes.Position = 0;
            await _stagedBytes.CopyToAsync(_stream).ConfigureAwait(false);
            _stagedBytes.SetLength(0);
            _stagedBytes.Position = 0;
        }
        await _stream.FlushAsync().ConfigureAwait(false);
        if (_stream is FileStream file) file.Flush(flushToDisk: true);
        else _stream.Flush();
        LastSyncDurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public async Task PreserveOpenAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state is SegmentState.PreservedIncomplete or SegmentState.FilePublished or SegmentState.CatalogRegistered)
                return;

            Exception? flushFailure = null;
            try { await FlushCoreAsync().ConfigureAwait(false); }
            catch (Exception ex) { flushFailure = ex; }
            finally
            {
                try { await DisposeStreamAsync().ConfigureAwait(false); }
                catch (Exception ex) { flushFailure ??= ex; }
                _state = SegmentState.PreservedIncomplete;
            }

            if (flushFailure is not null)
                ExceptionDispatchInfo.Capture(flushFailure).Throw();
        }
        finally { _operationGate.Release(); }
    }

    public async Task SealAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state == SegmentState.CatalogRegistered) return;
            if (_state == SegmentState.PreservedIncomplete)
                throw new InvalidOperationException("A preserved incomplete Raw segment cannot be sealed.");
            var sealStarted = Stopwatch.GetTimestamp();
            if (_state == SegmentState.Open)
            {
                await FlushCoreAsync().ConfigureAwait(false);
                await DisposeStreamAsync().ConfigureAwait(false);
                _state = SegmentState.PreservedIncomplete;
                using (var sha = SHA256.Create())
                using (var input = File.OpenRead(_openPath))
                    _checksum ??= Convert.ToHexString(sha.ComputeHash(input)).ToLowerInvariant();
                Sha256 = _checksum;
                File.Move(_openPath, PublishedPath);
                _state = SegmentState.FilePublished;
            }

            _publishedBytes ??= new FileInfo(PublishedPath).Length;
            // If the DB write fails, the final file is deliberately preserved for reconciliation.
            var catalogStarted = Stopwatch.GetTimestamp();
            _catalog.RegisterFinalizedSegment(_relativePath, _records,
                _first == long.MaxValue ? 0 : _first, _last, _checksum!, _publishedBytes.Value,
                _sourceId, _taskRunId);
            LastCatalogWriteDurationMs = Stopwatch.GetElapsedTime(catalogStarted).TotalMilliseconds;
            LastSealDurationMs = Stopwatch.GetElapsedTime(sealStarted).TotalMilliseconds;
            _state = SegmentState.CatalogRegistered;
        }
        finally { _operationGate.Release(); }
    }

    private void EnsureOpenForAppend()
    {
        if (_state != SegmentState.Open)
            throw new InvalidOperationException($"Raw segment is not writable in state {_state}.");
    }

    private async ValueTask DisposeStreamAsync()
    {
        if (_streamDisposed) return;
        _streamDisposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private string PublishedPath => _openPath[..^".open".Length] + ".ndjson";

}

internal static class H3cRawOutput
{
    public const int MaximumBytes = 256 * 1024;

    // Full-device diagnostics can be larger than ordinary command responses.
    // Keep admission command-specific throughout collection, sealing and recovery.
    public const int MaximumDiagnosticBytes = H3cDiagnosticCapture.MaximumOutputBytes;
    public static int MaximumBytesFor(string command) => command == H3cDiagnosticCapture.Command
        ? MaximumDiagnosticBytes : MaximumBytes;

    public static int MaximumRecordBytesFor(string command) => command == H3cDiagnosticCapture.Command
        ? 6 * 1024 * 1024 : 512 * 1024;
}
