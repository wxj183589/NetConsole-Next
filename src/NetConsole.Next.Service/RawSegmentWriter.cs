using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record SyslogPacket(long ReceivedMs, string SenderIp, int SenderPort, byte[] Payload);

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

    private readonly Action<string, long, long, long, string, long> _register;
    private readonly string _openPath;
    private readonly string _relativePath;
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _first = long.MaxValue;
    private long _last, _records, _bytes;
    private string? _checksum;
    private long? _publishedBytes;
    private SegmentState _state = SegmentState.Open;
    private bool _writerDisposed;

    public long Bytes => _bytes;

    public RawSegmentWriter(Catalog catalog)
        : this(catalog.RawRoot, catalog.RegisterFinalizedSegment)
    {
    }

    internal RawSegmentWriter(string rawRoot, Action<string, long, long, long, string, long> register)
    {
        _register = register;
        var now = DateTimeOffset.UtcNow;
        var folderRelative = Path.Combine("syslog", now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
        var folder = Path.Combine(rawRoot, folderRelative);
        Directory.CreateDirectory(folder);
        var name = now.ToString("HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        _openPath = Path.Combine(folder, name + ".open");
        _relativePath = Path.Combine("raw", folderRelative, name + ".ndjson").Replace('\\', '/');
        _stream = new FileStream(_openPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32768, FileOptions.Asynchronous);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), 32768);
    }

    public async Task AppendAsync(SyslogPacket packet)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureOpenForAppend();
            var line = JsonSerializer.Serialize(new
            {
                received_ms = packet.ReceivedMs,
                sender_ip = packet.SenderIp,
                sender_port = packet.SenderPort,
                payload_base64 = Convert.ToBase64String(packet.Payload)
            });
            await _writer.WriteLineAsync(line).ConfigureAwait(false);
            _bytes += Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(_writer.NewLine);
            _first = Math.Min(_first, packet.ReceivedMs);
            _last = Math.Max(_last, packet.ReceivedMs);
            _records++;
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
        await _writer.FlushAsync().ConfigureAwait(false);
        _stream.Flush(flushToDisk: true);
    }

    public async Task PreserveOpenAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state is SegmentState.PreservedIncomplete or SegmentState.FilePublished or SegmentState.CatalogRegistered)
                return;

            Exception? failure = null;
            try { await FlushCoreAsync().ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { await DisposeWriterAsync().ConfigureAwait(false); }
                catch (Exception ex) { failure ??= ex; }
                _state = SegmentState.PreservedIncomplete;
            }

            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
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

            if (_state == SegmentState.Open)
            {
                await FlushCoreAsync().ConfigureAwait(false);
                _state = SegmentState.PreservedIncomplete;
                await DisposeWriterAsync().ConfigureAwait(false);
                using (var sha = SHA256.Create())
                using (var input = File.OpenRead(_openPath))
                    _checksum ??= Convert.ToHexString(sha.ComputeHash(input)).ToLowerInvariant();
                File.Move(_openPath, PublishedPath);
                _state = SegmentState.FilePublished;
            }

            _publishedBytes ??= new FileInfo(PublishedPath).Length;
            // If the DB write fails, the final file is deliberately preserved for reconciliation.
            _register(_relativePath, _records,
                _first == long.MaxValue ? 0 : _first, _last, _checksum!, _publishedBytes.Value);
            _state = SegmentState.CatalogRegistered;
        }
        finally { _operationGate.Release(); }
    }

    private void EnsureOpenForAppend()
    {
        if (_state != SegmentState.Open)
            throw new InvalidOperationException($"Raw segment is not writable in state {_state}.");
    }

    private async ValueTask DisposeWriterAsync()
    {
        if (_writerDisposed) return;
        _writerDisposed = true;
        await _writer.DisposeAsync().ConfigureAwait(false);
    }

    private string PublishedPath => _openPath[..^".open".Length] + ".ndjson";
}
