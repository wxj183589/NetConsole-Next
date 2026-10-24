using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record SyslogPacket(long ReceivedMs, string SenderIp, int SenderPort, byte[] Payload);

// Append-only P0 raw writer. Roll at 4MiB, seal before indexing; retain files on catalog errors.
internal sealed class RawSegmentWriter
{
    private readonly Action<string, long, long, long, string, long> _register;
    private readonly string _openPath;
    private readonly string _publishedPath;
    private readonly string _relativePath;
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _first = long.MaxValue;
    private long _last, _records, _bytes;
    private long? _publishedBytes;
    private string? _checksum;
    private bool _writerClosed;
    private bool _filePublished;
    private bool _catalogRegistered;

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
        _publishedPath = Path.Combine(folder, name + ".ndjson");
        _relativePath = Path.Combine("raw", folderRelative, name + ".ndjson");
        _stream = new FileStream(_openPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32768, FileOptions.Asynchronous);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), 32768);
    }

    public async Task AppendAsync(SyslogPacket packet)
    {
        await _operationGate.WaitAsync();
        try
        {
            if (_writerClosed || _filePublished)
                throw new InvalidOperationException("A sealed Raw segment cannot be changed.");
            var line = JsonSerializer.Serialize(new
            {
                received_ms = packet.ReceivedMs,
                sender_ip = packet.SenderIp,
                sender_port = packet.SenderPort,
                payload_base64 = Convert.ToBase64String(packet.Payload)
            });
            await _writer.WriteLineAsync(line);
            _bytes += Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(_writer.NewLine);
            _first = Math.Min(_first, packet.ReceivedMs);
            _last = Math.Max(_last, packet.ReceivedMs);
            _records++;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task FlushAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            await FlushCoreAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SealAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            if (_catalogRegistered) return;

            if (!_filePublished)
            {
                await FlushCoreAsync();
                if (!_writerClosed)
                {
                    await _writer.DisposeAsync();
                    _writerClosed = true;
                }

                using (var sha = SHA256.Create())
                using (var input = File.OpenRead(_openPath))
                    _checksum ??= Convert.ToHexString(sha.ComputeHash(input)).ToLowerInvariant();

                File.Move(_openPath, _publishedPath);
                // Publishing is separate from catalog registration. A later SealAsync
                // must never return to the old .open path after this move succeeds.
                _filePublished = true;
            }

            _publishedBytes ??= new FileInfo(_publishedPath).Length;
            _register(_relativePath, _records,
                _first == long.MaxValue ? 0 : _first, _last, _checksum!, _publishedBytes.Value);
            _catalogRegistered = true;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task FlushCoreAsync()
    {
        if (_writerClosed) return;
        await _writer.FlushAsync();
        _stream.Flush(flushToDisk: true);
    }
}
