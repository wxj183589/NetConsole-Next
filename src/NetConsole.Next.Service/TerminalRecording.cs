using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed class TerminalRecordingWriter : IAsyncDisposable
{
    private const long MaximumBytes = 64L * 1024 * 1024;
    private readonly FileStream _stream;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly string _relativePath;
    private long _bytes;
    private bool _failed;

    public TerminalRecordingWriter(string dataRoot, string sessionId)
    {
        var directory = Path.Combine(dataRoot, "terminal-recordings");
        Directory.CreateDirectory(directory);
        _relativePath = Path.Combine("terminal-recordings", sessionId + ".jsonl").Replace('\\', '/');
        var path = Path.Combine(dataRoot, _relativePath.Replace('/', Path.DirectorySeparatorChar));
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
    }

    public async ValueTask AppendAsync(long atMs, string direction, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        if (_failed) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            at_ms = atMs,
            direction,
            data_b64 = Convert.ToBase64String(bytes.Span)
        });
        var line = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, line, 0, payload.Length);
        line[^1] = (byte)'\n';
        if (_bytes + line.Length > MaximumBytes)
        {
            _failed = true;
            throw new IOException("Terminal recording reached its bounded size limit.");
        }
        await _stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        _hash.AppendData(line);
        _bytes += line.Length;
    }

    public void MarkIncomplete() => _failed = true;

    public string RelativePath => _relativePath;

    public async ValueTask<TerminalRecordingResult> CompleteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _stream.Flush(flushToDisk: true);
            var hash = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
            return new(_relativePath, _bytes, hash, !_failed);
        }
        finally
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _hash.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream.SafeFileHandle.IsClosed) return;
        try { await _stream.FlushAsync().ConfigureAwait(false); }
        catch { }
        await _stream.DisposeAsync().ConfigureAwait(false);
        _hash.Dispose();
    }
}

internal static class RestrictedReadonlyInputPolicy
{
    public static bool TryNormalize(string? input, out string command, out string reason)
    {
        command = input?.Trim() ?? string.Empty;
        reason = string.Empty;
        if (command.Length == 0 || command.Length > 256)
        {
            reason = "Command must contain 1-256 characters.";
            return false;
        }
        if (command.Any(char.IsControl) || command.Contains(';') || command.Contains('|') ||
            command.Contains('&') || command.Contains('`') || command.Contains('$') ||
            command.Contains('>') || command.Contains('<') || command.Contains('\\'))
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
