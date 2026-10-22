using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed partial class Catalog
{
    private static long TerminalNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public string CreateTerminalSession(TerminalSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Mode != "RESTRICTED_READONLY")
            throw new ArgumentException("Only RESTRICTED_READONLY terminal sessions are enabled.", nameof(session));
        var now = TerminalNowMs();
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var insert = db.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = @"INSERT INTO terminal_sessions
            (id,target_id,target_name,protocol,mode,host,port,serial_port,serial_config,state,
             started_ms,last_activity_ms,recording_relative_path,recording_complete)
            VALUES($id,$target,$name,$protocol,$mode,$host,$port,$serialPort,$serialConfig,$state,
                   $started,$activity,$recordingPath,0)";
        insert.Parameters.AddWithValue("$id", session.Id);
        insert.Parameters.AddWithValue("$target", session.TargetId);
        insert.Parameters.AddWithValue("$name", session.TargetName);
        insert.Parameters.AddWithValue("$protocol", session.Protocol);
        insert.Parameters.AddWithValue("$mode", session.Mode);
        insert.Parameters.AddWithValue("$host", (object?)session.Host ?? DBNull.Value);
        insert.Parameters.AddWithValue("$port", (object?)session.Port ?? DBNull.Value);
        insert.Parameters.AddWithValue("$serialPort", (object?)session.SerialPort ?? DBNull.Value);
        insert.Parameters.AddWithValue("$serialConfig", (object?)session.SerialConfig ?? DBNull.Value);
        insert.Parameters.AddWithValue("$state", "CONNECTING");
        insert.Parameters.AddWithValue("$started", session.StartedMs > 0 ? session.StartedMs : now);
        insert.Parameters.AddWithValue("$activity", session.LastActivityMs > 0 ? session.LastActivityMs : now);
        insert.Parameters.AddWithValue("$recordingPath", (object?)session.RecordingRelativePath ?? DBNull.Value);
        insert.ExecuteNonQuery();
        InsertTerminalEvent(db, transaction, session.Id, now, "CONNECTING", "{}");
        transaction.Commit();
        return session.Id;
    }

    public void RecordTerminalEvent(string sessionId, TerminalEventInput eventInput)
    {
        ArgumentNullException.ThrowIfNull(eventInput);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        InsertTerminalEvent(db, transaction, sessionId, TerminalNowMs(), eventInput.EventType, eventInput.DetailJson);
        using var touch = db.CreateCommand();
        touch.Transaction = transaction;
        touch.CommandText = "UPDATE terminal_sessions SET last_activity_ms=$now WHERE id=$id";
        touch.Parameters.AddWithValue("$now", TerminalNowMs());
        touch.Parameters.AddWithValue("$id", sessionId);
        touch.ExecuteNonQuery();
        transaction.Commit();
    }

    public void UpdateTerminalState(string sessionId, string state, string? connectionResult = null,
        string? disconnectReason = null, string? negotiatedAlgorithm = null, string? hostKeyEvent = null)
    {
        if (state is not ("CONNECTING" or "CONNECTED" or "DISCONNECTED" or "FAILED" or "CLOSED"))
            throw new ArgumentException("Invalid terminal session state.", nameof(state));
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var now = TerminalNowMs();
        using var update = db.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = @"UPDATE terminal_sessions SET state=$state,
            ended_ms=CASE WHEN $ended=1 THEN COALESCE(ended_ms,$now) ELSE ended_ms END,
            last_activity_ms=$now, connection_result=COALESCE($result,connection_result),
            disconnect_reason=COALESCE($reason,disconnect_reason),
            negotiated_algorithm=COALESCE($algorithm,negotiated_algorithm),
            host_key_event=COALESCE($hostKey,host_key_event)
            WHERE id=$id";
        update.Parameters.AddWithValue("$state", state);
        update.Parameters.AddWithValue("$ended", state is "DISCONNECTED" or "FAILED" or "CLOSED" ? 1 : 0);
        update.Parameters.AddWithValue("$now", now);
        update.Parameters.AddWithValue("$result", (object?)connectionResult ?? DBNull.Value);
        update.Parameters.AddWithValue("$reason", (object?)disconnectReason ?? DBNull.Value);
        update.Parameters.AddWithValue("$algorithm", (object?)negotiatedAlgorithm ?? DBNull.Value);
        update.Parameters.AddWithValue("$hostKey", (object?)hostKeyEvent ?? DBNull.Value);
        update.Parameters.AddWithValue("$id", sessionId);
        if (update.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Terminal session was not found.");
        transaction.Commit();
    }

    public void CompleteTerminalRecording(string sessionId, TerminalRecordingResult recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        using var db = Open();
        using var update = db.CreateCommand();
        update.CommandText = @"UPDATE terminal_sessions SET recording_relative_path=$path,
            recording_bytes=$bytes,recording_sha256=$sha256,recording_complete=$complete,
            last_activity_ms=$now WHERE id=$id";
        update.Parameters.AddWithValue("$path", recording.RelativePath);
        update.Parameters.AddWithValue("$bytes", recording.Bytes);
        update.Parameters.AddWithValue("$sha256", recording.Sha256);
        update.Parameters.AddWithValue("$complete", recording.Complete ? 1 : 0);
        update.Parameters.AddWithValue("$now", TerminalNowMs());
        update.Parameters.AddWithValue("$id", sessionId);
        update.ExecuteNonQuery();
    }

    public TerminalSessionSnapshot? GetTerminalSession(string sessionId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = TerminalSessionSelect + " WHERE id=$id";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTerminalSession(reader) : null;
    }

    public IReadOnlyList<TerminalSessionSnapshot> RecentTerminalSessions(string? targetId = null, int limit = 50)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = TerminalSessionSelect +
            (targetId is null ? " ORDER BY started_ms DESC LIMIT $limit" :
                " WHERE target_id=$target ORDER BY started_ms DESC LIMIT $limit");
        if (targetId is not null) command.Parameters.AddWithValue("$target", targetId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        using var reader = command.ExecuteReader();
        var result = new List<TerminalSessionSnapshot>();
        while (reader.Read()) result.Add(ReadTerminalSession(reader));
        return result;
    }

    public IReadOnlyList<TerminalEventSnapshot> TerminalEvents(string sessionId, int limit = 500)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT id,session_id,at_ms,event_type,detail_json
            FROM terminal_events WHERE session_id=$id ORDER BY at_ms,id LIMIT $limit";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var result = new List<TerminalEventSnapshot>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetString(3), reader.GetString(4)));
        return result;
    }

    private static readonly string TerminalSessionSelect = @"SELECT id,target_id,target_name,protocol,mode,
        host,port,serial_port,serial_config,state,started_ms,ended_ms,last_activity_ms,connection_result,
        disconnect_reason,negotiated_algorithm,host_key_event,recording_relative_path,recording_bytes,
        recording_sha256,recording_complete FROM terminal_sessions";

    private static TerminalSessionSnapshot ReadTerminalSession(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        NullableString(reader, 5), NullableInt(reader, 6), NullableString(reader, 7), NullableString(reader, 8),
        reader.GetString(9), reader.GetInt64(10), NullableLong(reader, 11), reader.GetInt64(12),
        NullableString(reader, 13), NullableString(reader, 14), NullableString(reader, 15), NullableString(reader, 16),
        NullableString(reader, 17), reader.GetInt64(18), NullableString(reader, 19), reader.GetInt32(20) != 0);

    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static long? NullableLong(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);

    private static void InsertTerminalEvent(SqliteConnection db, SqliteTransaction transaction, string sessionId,
        long atMs, string eventType, string detailJson)
    {
        using var insert = db.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = @"INSERT INTO terminal_events(session_id,at_ms,event_type,detail_json)
            VALUES($session,$at,$type,$detail)";
        insert.Parameters.AddWithValue("$session", sessionId);
        insert.Parameters.AddWithValue("$at", atMs);
        insert.Parameters.AddWithValue("$type", eventType);
        insert.Parameters.AddWithValue("$detail", detailJson);
        insert.ExecuteNonQuery();
    }
}
