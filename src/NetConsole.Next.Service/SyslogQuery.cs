using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record SyslogQuery(long? FromMs = null, long? ToMs = null, string? SenderIp = null,
    int? MaximumSeverity = null, string? Text = null, int Limit = 200);
internal sealed record SyslogEntry(long ReceivedMs, string SenderIp, int SenderPort, int? Facility,
    int? Severity, string Message, string RawReference, int Line);
internal sealed record SyslogQueryResult(IReadOnlyList<SyslogEntry> Items, int ScannedSegments, int ScannedRecords,
    int UnavailableSegments, bool Partial, long FromMs, long ToMs, string SourceState);

internal sealed partial class Catalog
{
    // Query registered, sealed syslog only. Never traverse other Raw sources or active writer files.
    public SyslogQueryResult QuerySyslog(SyslogQuery query, P2B5SiteScopeDecision scope,
        CancellationToken cancellationToken = default)
    {
        if (!scope.IsAuthorized || (!scope.IsGlobal && !scope.AllowedSiteReferences.Contains("local")))
            throw new TaskScopeRevokedException("SYSLOG_SITE_SCOPE_DENIED");
        var to = query.ToMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var from = query.FromMs ?? to - 86_400_000;
        if (from < 0 || to < from || to - from > 31L * 86_400_000 || query.Limit is < 1 or > 500 ||
            query.MaximumSeverity is < 0 or > 7 || query.Text?.Length > 256 || query.SenderIp?.Length > 64)
            throw new ArgumentException("SYSLOG_QUERY_INVALID");
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT r.relative_path,r.sha256,r.byte_count,r.state
            FROM raw_segments r JOIN collection_sources s ON s.id=r.source_id
            WHERE s.id='syslog-local' AND s.site_id='local'
              AND r.last_received_ms >= $from AND r.first_received_ms <= $to
            ORDER BY r.last_received_ms DESC,r.relative_path DESC LIMIT 9
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var segments = new List<(string Path, string Hash, long Bytes, string State)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) segments.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
        var partial = segments.Count > 8;
        var unavailable = 0;
        var scanned = 0;
        var records = 0;
        var entries = new List<SyslogEntry>();
        foreach (var segment in segments.Take(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.State != "FINALIZED" || segment.Bytes is < 1 or > 4 * 1024 * 1024 + 128 * 1024 ||
                !TryResolveRawPath(segment.Path, out var path)) { unavailable++; continue; }
            try
            {
                if (!File.Exists(path) || ContainsReparsePoint(path)) { unavailable++; continue; }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != segment.Bytes) { unavailable++; continue; }
                var bytes = new byte[(int)stream.Length];
                stream.ReadExactly(bytes);
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(segment.Hash, StringComparison.OrdinalIgnoreCase))
                { unavailable++; continue; }
                scanned++;
                using var lines = new StringReader(Encoding.UTF8.GetString(bytes));
                var lineNumber = 0;
                while (lines.ReadLine() is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lineNumber++;
                    records++;
                    if (records > 100_000) { partial = true; break; }
                    try
                    {
                        using var json = JsonDocument.Parse(line);
                        var root = json.RootElement;
                        var received = root.GetProperty("received_ms").GetInt64();
                        var sender = root.GetProperty("sender_ip").GetString() ?? "";
                        if (received < from || received > to || (!string.IsNullOrWhiteSpace(query.SenderIp) && sender != query.SenderIp.Trim())) continue;
                        var message = Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("payload_base64").GetString() ?? ""));
                        int? severity = null, facility = null;
                        var end = message.IndexOf('>');
                        if (message.StartsWith('<') && end is > 1 and <= 4 &&
                            int.TryParse(message.AsSpan(1, end - 1), out var pri) && pri is >= 0 and <= 191)
                        { severity = pri % 8; facility = pri / 8; }
                        if (query.MaximumSeverity.HasValue && (!severity.HasValue || severity > query.MaximumSeverity)) continue;
                        if (!string.IsNullOrWhiteSpace(query.Text) && !message.Contains(query.Text.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                        entries.Add(new(received, sender, root.GetProperty("sender_port").GetInt32(), facility, severity,
                            message, segment.Path, lineNumber));
                        // Keep the newest bounded page even when records are not ordered in a segment.
                        if (entries.Count > query.Limit * 2)
                        { entries = entries.OrderByDescending(x => x.ReceivedMs).ThenBy(x => x.RawReference, StringComparer.Ordinal).ThenByDescending(x => x.Line).Take(query.Limit).ToList(); partial = true; }
                    }
                    catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
                    { unavailable++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unavailable++; }
            if (records > 100_000) break;
        }
        if (entries.Count > query.Limit) partial = true;
        return new(entries.OrderByDescending(x => x.ReceivedMs).ThenBy(x => x.RawReference, StringComparer.Ordinal)
            .ThenByDescending(x => x.Line).Take(query.Limit).ToArray(), scanned, records, unavailable,
            partial || unavailable > 0, from, to, "SEALED_SEGMENTS_ONLY");
    }
}
