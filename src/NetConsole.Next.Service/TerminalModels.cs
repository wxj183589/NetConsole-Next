using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace NetConsole.Next.Service;

internal enum TerminalProtocol
{
    Ssh,
    Telnet,
    Serial
}

internal static class TerminalProtocolNames
{
    public static string ToStorage(TerminalProtocol protocol) => protocol switch
    {
        TerminalProtocol.Ssh => "SSH",
        TerminalProtocol.Telnet => "TELNET",
        TerminalProtocol.Serial => "SERIAL",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    public static bool TryParse(string value, out TerminalProtocol protocol)
    {
        protocol = value?.Trim().ToUpperInvariant() switch
        {
            "SSH" => TerminalProtocol.Ssh,
            "TELNET" => TerminalProtocol.Telnet,
            "SERIAL" => TerminalProtocol.Serial,
            _ => (TerminalProtocol)(-1)
        };
        return (int)protocol >= 0;
    }
}

internal sealed record TerminalSessionRequest(
    string TargetId,
    string Protocol,
    string Mode = "RESTRICTED_READONLY",
    string? SerialPort = null,
    int BaudRate = 9600,
    int DataBits = 8,
    string StopBits = "One",
    string Parity = "None",
    string Handshake = "None",
    bool DtrEnable = false,
    bool RtsEnable = false);

internal sealed record TerminalSerialConfiguration(
    string PortName,
    int BaudRate,
    int DataBits,
    string StopBits,
    string Parity,
    string Handshake,
    bool DtrEnable,
    bool RtsEnable)
{
    public string ToStorage() => JsonSerializer.Serialize(this);
}

internal sealed record TerminalSessionSnapshot(
    string Id,
    string TargetId,
    string TargetName,
    string Protocol,
    string Mode,
    string? Host,
    int? Port,
    string? SerialPort,
    string? SerialConfig,
    string State,
    long StartedMs,
    long? EndedMs,
    long LastActivityMs,
    string? ConnectionResult,
    string? DisconnectReason,
    string? NegotiatedAlgorithm,
    string? HostKeyEvent,
    string? RecordingRelativePath,
    long RecordingBytes,
    string? RecordingSha256,
    bool RecordingComplete,
    string OperatorIdentity = "UNATTRIBUTED",
    string? SiteName = null,
    int CommandCount = 0,
    long OutputBytes = 0,
    string RecordingStatus = "INCOMPLETE",
    long? RecordingVerifiedMs = null,
    string? RecordingIntegrityDetail = null);

internal sealed record TerminalEventSnapshot(
    long Id,
    string SessionId,
    long AtMs,
    string EventType,
    string DetailJson);

internal sealed record TerminalEventInput(string EventType, object? Detail = null)
{
    public string DetailJson => Detail is null ? "{}" : JsonSerializer.Serialize(Detail);
}

internal sealed record TerminalRecordingSegmentResult(
    int Number,
    string RelativePath,
    long StartedMs,
    long EndedMs,
    long Bytes,
    long RecordCount,
    string Sha256,
    bool Complete);

internal sealed record TerminalRecordingResult(
    string RelativePath,
    long Bytes,
    string Sha256,
    bool Complete,
    IReadOnlyList<TerminalRecordingSegmentResult>? Segments = null,
    long InputCount = 0,
    long OutputBytes = 0);

internal sealed record TerminalRecordingSegmentSnapshot(
    string SessionId,
    int Number,
    string RelativePath,
    long StartedMs,
    long EndedMs,
    long Bytes,
    long RecordCount,
    string? Sha256,
    string State);

internal sealed record TerminalRecordingFileInspection(
    long RecordCount,
    long StartedMs,
    long EndedMs,
    long Bytes,
    string Sha256);

internal sealed record TerminalRecordingIntegritySnapshot(
    string SessionId,
    string Status,
    bool HashMatches,
    long VerifiedAtMs,
    long Bytes,
    string? Sha256,
    string Detail,
    IReadOnlyList<TerminalRecordingSegmentSnapshot> Segments);

internal sealed record TerminalRecordingEntry(
    long Position,
    int SegmentNumber,
    long AtMs,
    string Direction,
    byte[] Data);

internal sealed record TerminalReplayPage(
    string SessionId,
    long Offset,
    int Limit,
    bool HasMore,
    IReadOnlyList<TerminalRecordingEntry> Entries);

internal sealed record TerminalSessionPage(
    int Page,
    int PageSize,
    long Total,
    IReadOnlyList<TerminalSessionSnapshot> Items);

internal sealed record TerminalExportAuditSnapshot(
    string Id,
    string SessionId,
    string Format,
    long StartedMs,
    long? EndedMs,
    string IntegrityStatus,
    string OperatorIdentity,
    string State,
    long? Bytes,
    string? Detail);

internal sealed record TerminalSessionQuery(
    string? TargetId,
    string? SiteName,
    string? Protocol,
    long? FromMs,
    long? ToMs,
    string? State,
    string? Integrity,
    string? Keyword,
    int Page,
    int PageSize,
    string Sort,
    bool Descending)
{
    public static TerminalSessionQuery FromQuery(IQueryCollection query) => new(
        EmptyToNull(query["targetId"]), EmptyToNull(query["site"]), EmptyToNull(query["protocol"]),
        ParseLong(query["fromMs"]), ParseLong(query["toMs"]), EmptyToNull(query["state"]),
        EmptyToNull(query["integrity"]), EmptyToNull(query["keyword"]),
        ParsePositive(query["page"], 1, 100000), ParsePositive(query["pageSize"], 20, 200),
        EmptyToNull(query["sort"]) ?? "started", !string.Equals(query["order"], "asc", StringComparison.OrdinalIgnoreCase));

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static long? ParseLong(string? value) => long.TryParse(value, out var parsed) && parsed >= 0 ? parsed : null;
    private static int ParsePositive(string? value, int fallback, int maximum) =>
        int.TryParse(value, out var parsed) ? Math.Clamp(parsed, 1, maximum) : fallback;
}

internal sealed record TerminalTicketResult(string Ticket, string SessionId, long ExpiresAtMs);

