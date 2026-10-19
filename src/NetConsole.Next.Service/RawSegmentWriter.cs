using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record SyslogPacket(long ReceivedMs, string SenderIp, int SenderPort, byte[] Payload);

// Append-only P0 raw writer. Roll at 4MiB, seal before indexing; retain files on catalog errors.
internal sealed class RawSegmentWriter
{
    private readonly Catalog _catalog;
    private readonly string _openPath;
    private readonly string _relativePath;
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private long _first = long.MaxValue;
    private long _last, _records, _bytes;
    private bool _sealed, _closed;

    public long Bytes => _bytes;

    public RawSegmentWriter(Catalog catalog)
    {
        _catalog = catalog;
        var now = DateTimeOffset.UtcNow;
        var folderRelative = Path.Combine("syslog", now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
        var folder = Path.Combine(catalog.RawRoot, folderRelative);
        Directory.CreateDirectory(folder);
        var name = now.ToString("HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        _openPath = Path.Combine(folder, name + ".open");
        _relativePath = Path.Combine("raw", folderRelative, name + ".ndjson");
        _stream = new FileStream(_openPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32768, FileOptions.Asynchronous);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), 32768);
    }

    public async Task AppendAsync(SyslogPacket packet)
    {
        if (_sealed) throw new InvalidOperationException("A sealed Raw segment cannot be changed.");
        var line = JsonSerializer.Serialize(new
        {
            received_ms = packet.ReceivedMs,
            sender_ip = packet.SenderIp,
            sender_port = packet.SenderPort,
            payload_base64 = Convert.ToBase64String(packet.Payload)
        });
        await _writer.WriteLineAsync(line);
        _bytes += Encoding.UTF8.GetByteCount(line) + 1;
        _first = Math.Min(_first, packet.ReceivedMs);
        _last = Math.Max(_last, packet.ReceivedMs);
        _records++;
    }

    public async Task FlushAsync()
    {
        if (_closed) throw new InvalidOperationException("A closed Raw segment cannot be flushed.");
        await _writer.FlushAsync();
        _stream.Flush(flushToDisk: true);
    }

    public async Task PreserveOpenAsync()
    {
        if (_closed) return;
        try { await _writer.FlushAsync(); }
        catch (IOException) { }
        await _writer.DisposeAsync();
        _closed = true;
    }

    public async Task SealAsync()
    {
        if (_sealed) return;
        await FlushAsync();
        await _writer.DisposeAsync();
        _closed = true;
        string checksum;
        using (var sha = SHA256.Create())
        using (var input = File.OpenRead(_openPath))
            checksum = Convert.ToHexString(sha.ComputeHash(input)).ToLowerInvariant();
        var published = _openPath[..^".open".Length] + ".ndjson";
        File.Move(_openPath, published);
        // If the DB write fails, the final file is deliberately preserved for reconciliation.
        _catalog.RegisterFinalizedSegment(_relativePath, _records,
            _first == long.MaxValue ? 0 : _first, _last, checksum, new FileInfo(published).Length);
        _sealed = true;
    }
}
