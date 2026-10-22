using System.Security.Cryptography;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed class TerminalRecordingService
{
    private readonly Catalog _catalog;
    private readonly RuntimeSettings _settings;

    public TerminalRecordingService(Catalog catalog, RuntimeSettings settings)
    {
        _catalog = catalog;
        _settings = settings;
    }

    public TerminalRecordingIntegritySnapshot Verify(string sessionId)
    {
        var session = _catalog.GetTerminalSession(sessionId)
            ?? throw new KeyNotFoundException("Terminal session was not found.");
        var storedSegments = _catalog.TerminalRecordingSegments(sessionId);
        var segments = storedSegments.Count > 0
            ? storedSegments
            : session.RecordingRelativePath is null
                ? []
                : [new TerminalRecordingSegmentSnapshot(sessionId, 0, session.RecordingRelativePath,
                    session.StartedMs, session.EndedMs ?? session.LastActivityMs, session.RecordingBytes,
                    0, session.RecordingSha256, session.RecordingComplete ? "COMPLETE" : "INCOMPLETE")];
        var verifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var resultSegments = new List<TerminalRecordingSegmentSnapshot>();
        using var overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var status = "COMPLETE";
        var detail = "All recording segments were read and verified.";
        long totalBytes = 0;
        try
        {
            foreach (var segment in segments.OrderBy(item => item.Number))
            {
                var path = TerminalRecordingReader.ResolvePath(_settings.DataRoot, segment.RelativePath);
                if (!File.Exists(path))
                {
                    status = "MISSING";
                    detail = $"Recording segment {segment.Number} is missing.";
                    resultSegments.Add(segment with { State = "MISSING" });
                    continue;
                }
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long bytes = 0;
                long records = 0;
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                        64 * 1024, FileOptions.SequentialScan);
                    var buffer = new byte[64 * 1024];
                    int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hash.AppendData(buffer, 0, count);
                        overall.AppendData(buffer, 0, count);
                        bytes += count;
                    }
                    records = TerminalRecordingReader.ReadEntries(_settings.DataRoot, session,
                        [segment]).LongCount();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException)
                {
                    status = "CORRUPTED";
                    detail = $"Recording segment {segment.Number} could not be parsed: {ex.GetType().Name}.";
                    resultSegments.Add(segment with { State = "CORRUPTED" });
                    continue;
                }
                var observed = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                totalBytes += bytes;
                var matches = bytes == segment.Bytes &&
                    (segment.Sha256 is null || string.Equals(observed, segment.Sha256, StringComparison.OrdinalIgnoreCase));
                var segmentState = matches ? segment.State : "CORRUPTED";
                if (!matches)
                {
                    status = "CORRUPTED";
                    detail = $"Recording segment {segment.Number} checksum or byte count does not match its catalog record.";
                }
                else if (segment.State == "INCOMPLETE" && status == "COMPLETE")
                {
                    status = "INCOMPLETE";
                    detail = "The final recording segment was not cleanly completed.";
                }
                resultSegments.Add(segment with { RecordCount = records, State = segmentState });
            }
            var overallSha = Convert.ToHexString(overall.GetHashAndReset()).ToLowerInvariant();
            var overallMatches = session.RecordingSha256 is null ||
                string.Equals(overallSha, session.RecordingSha256, StringComparison.OrdinalIgnoreCase);
            if (!overallMatches && status != "MISSING")
            {
                status = "CORRUPTED";
                detail = "The session recording checksum does not match its catalog record.";
            }
            if (status is "COMPLETE" or "INCOMPLETE" &&
                string.Equals(session.RecordingStatus, "INTERRUPTED", StringComparison.OrdinalIgnoreCase))
            {
                status = "INTERRUPTED";
                detail = "The service stopped before the terminal session was closed.";
            }
            else if (status == "COMPLETE" && !session.RecordingComplete)
            {
                status = "INCOMPLETE";
                detail = "The recording was not marked complete by the session lifecycle.";
            }
            var hashMatches = overallMatches && resultSegments.All(item => item.State is "COMPLETE" or "INCOMPLETE");
            var integrity = new TerminalRecordingIntegritySnapshot(sessionId, status, hashMatches,
                verifiedAt, totalBytes, session.RecordingSha256, detail, resultSegments);
            _catalog.SetTerminalRecordingIntegrity(sessionId, integrity);
            return integrity;
        }
        catch (InvalidDataException ex)
        {
            var integrity = new TerminalRecordingIntegritySnapshot(sessionId, "CORRUPTED", false,
                verifiedAt, totalBytes, session.RecordingSha256, ex.Message, resultSegments);
            _catalog.SetTerminalRecordingIntegrity(sessionId, integrity);
            return integrity;
        }
    }

    public TerminalReplayPage ReadReplay(string sessionId, long offset, int limit, long? fromMs = null, long? toMs = null)
    {
        var session = _catalog.GetTerminalSession(sessionId)
            ?? throw new KeyNotFoundException("Terminal session was not found.");
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 2000);
        var segments = _catalog.TerminalRecordingSegments(sessionId);
        IReadOnlyList<TerminalRecordingEntry> entries;
        bool hasMore;
        if (fromMs is null && toMs is null)
        {
            (entries, hasMore) = TerminalRecordingReader.ReadPage(_settings.DataRoot, session, segments, offset, limit);
        }
        else
        {
            var filtered = TerminalRecordingReader.ReadEntries(_settings.DataRoot, session, segments)
                .Where(item => (!fromMs.HasValue || item.AtMs >= fromMs.Value) && (!toMs.HasValue || item.AtMs <= toMs.Value))
                .Skip(checked((int)Math.Min(offset, int.MaxValue))).Take(limit + 1).ToList();
            hasMore = filtered.Count > limit;
            if (hasMore) filtered.RemoveAt(limit);
            entries = filtered;
        }
        return new(sessionId, offset, limit, hasMore, entries);
    }
}
