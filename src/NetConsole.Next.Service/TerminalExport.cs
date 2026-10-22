using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace NetConsole.Next.Service;

internal sealed class TerminalExportService
{
    private readonly Catalog _catalog;
    private readonly RuntimeSettings _settings;

    public TerminalExportService(Catalog catalog, RuntimeSettings settings)
    {
        _catalog = catalog;
        _settings = settings;
    }

    public async Task WriteAsync(HttpResponse response, TerminalSessionSnapshot session, string format,
        TerminalRecordingIntegritySnapshot integrity, CancellationToken cancellationToken)
    {
        var normalized = format.Trim().ToUpperInvariant() switch
        {
            "TXT" => "TXT",
            "JSONL" => "JSONL",
            "HTML" => "HTML",
            "MANIFEST" => "MANIFEST",
            _ => throw new ArgumentException("format must be TXT, JSONL, HTML or MANIFEST.", nameof(format))
        };
        response.ContentType = normalized switch
        {
            "TXT" => "text/plain; charset=utf-8",
            "JSONL" => "application/x-ndjson; charset=utf-8",
            "HTML" => "text/html; charset=utf-8",
            _ => "application/json; charset=utf-8"
        };
        response.Headers.ContentDisposition = $"attachment; filename=terminal-{session.Id}.{normalized.ToLowerInvariant()}";
        var exportId = Guid.NewGuid().ToString("N");
        _catalog.StartTerminalExport(exportId, session.Id, normalized, integrity.Status, session.OperatorIdentity);
        long bytes = 0;
        try
        {
            await using var counting = new CountingStream(response.Body);
            if (normalized == "MANIFEST")
            {
                await JsonSerializer.SerializeAsync(counting, new { session, integrity }, cancellationToken: cancellationToken).ConfigureAwait(false);
                await counting.WriteAsync(Encoding.UTF8.GetBytes("\n"), cancellationToken).ConfigureAwait(false);
            }
            else if (normalized == "HTML")
            {
                await WriteHtmlAsync(counting, session, integrity, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteTextAsync(counting, session, integrity, normalized == "JSONL", cancellationToken).ConfigureAwait(false);
            }
            bytes = counting.BytesWritten;
            _catalog.CompleteTerminalExport(exportId, "COMPLETED", bytes, null);
        }
        catch (Exception ex)
        {
            _catalog.CompleteTerminalExport(exportId, "FAILED", bytes, ex.GetType().Name);
            throw;
        }
    }

    private async Task WriteTextAsync(Stream output, TerminalSessionSnapshot session,
        TerminalRecordingIntegritySnapshot integrity, bool jsonl, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
        if (!jsonl)
        {
            await writer.WriteLineAsync($"Session: {session.Id}").ConfigureAwait(false);
            await writer.WriteLineAsync($"Integrity: {integrity.Status} ({integrity.Detail})").ConfigureAwait(false);
            await writer.WriteLineAsync($"Operator: {session.OperatorIdentity}").ConfigureAwait(false);
        }
        foreach (var entry in TerminalRecordingReader.ReadEntries(_settings.DataRoot, session, integrity.Segments))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (jsonl)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    session_id = session.Id, position = entry.Position, segment = entry.SegmentNumber,
                    at_ms = entry.AtMs, direction = entry.Direction, data_b64 = Convert.ToBase64String(entry.Data)
                })).ConfigureAwait(false);
            }
            else
            {
                var text = Encoding.UTF8.GetString(entry.Data);
                await writer.WriteLineAsync($"[{DateTimeOffset.FromUnixTimeMilliseconds(entry.AtMs):O}] {entry.Direction.ToUpperInvariant()}: {text}").ConfigureAwait(false);
            }
        }
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteHtmlAsync(Stream output, TerminalSessionSnapshot session,
        TerminalRecordingIntegritySnapshot integrity, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
        await writer.WriteAsync("<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>Terminal session</title><h1>Terminal session ").ConfigureAwait(false);
        await writer.WriteAsync(HtmlEncode(session.Id)).ConfigureAwait(false);
        await writer.WriteAsync("</h1><p>Integrity: <strong>").ConfigureAwait(false);
        await writer.WriteAsync(HtmlEncode(integrity.Status + " - " + integrity.Detail)).ConfigureAwait(false);
        await writer.WriteAsync("</strong></p><pre>").ConfigureAwait(false);
        foreach (var entry in TerminalRecordingReader.ReadEntries(_settings.DataRoot, session, integrity.Segments))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync(HtmlEncode($"[{DateTimeOffset.FromUnixTimeMilliseconds(entry.AtMs):O}] {entry.Direction.ToUpperInvariant()}: {Encoding.UTF8.GetString(entry.Data)}")).ConfigureAwait(false);
            await writer.WriteAsync("\n").ConfigureAwait(false);
        }
        await writer.WriteAsync("</pre>").ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static string HtmlEncode(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); BytesWritten += count; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); BytesWritten += buffer.Length; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
