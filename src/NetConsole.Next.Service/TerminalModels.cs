using System.Text.Json;

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
    bool RecordingComplete);

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

internal sealed record TerminalRecordingResult(string RelativePath, long Bytes, string Sha256, bool Complete);

internal sealed record TerminalTicketResult(string Ticket, string SessionId, long ExpiresAtMs);

