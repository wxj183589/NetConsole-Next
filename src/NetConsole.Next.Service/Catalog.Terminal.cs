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

    public void UpdateTerminalProtocol(string sessionId, string protocol)
    {
        if (protocol is not ("SSH" or "TELNET" or "SERIAL"))
            throw new ArgumentException("Invalid terminal protocol.", nameof(protocol));
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE terminal_sessions SET protocol=$protocol WHERE id=$id";
        command.Parameters.AddWithValue("$protocol", protocol);
        command.Parameters.AddWithValue("$id", sessionId);
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Terminal session was not found.");
    }

    public void CompleteTerminalRecording(string sessionId, TerminalRecordingResult recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var segments = recording.Segments ?? [];
        foreach (var segment in segments)
        {
            using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO terminal_recording_segments
                (session_id,segment_number,relative_path,started_ms,ended_ms,byte_count,record_count,sha256,state)
                VALUES($session,$number,$path,$started,$ended,$bytes,$records,$sha,$state)
                ON CONFLICT(session_id,segment_number) DO UPDATE SET
                    relative_path=excluded.relative_path,started_ms=excluded.started_ms,ended_ms=excluded.ended_ms,
                    byte_count=excluded.byte_count,record_count=excluded.record_count,sha256=excluded.sha256,state=excluded.state
                WHERE terminal_recording_segments.relative_path=excluded.relative_path
                  AND COALESCE(terminal_recording_segments.sha256,'')=COALESCE(excluded.sha256,'')";
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$number", segment.Number);
            insert.Parameters.AddWithValue("$path", segment.RelativePath);
            insert.Parameters.AddWithValue("$started", segment.StartedMs);
            insert.Parameters.AddWithValue("$ended", segment.EndedMs);
            insert.Parameters.AddWithValue("$bytes", segment.Bytes);
            insert.Parameters.AddWithValue("$records", segment.RecordCount);
            insert.Parameters.AddWithValue("$sha", segment.Sha256);
            insert.Parameters.AddWithValue("$state", segment.Complete ? "COMPLETE" : "INCOMPLETE");
            insert.ExecuteNonQuery();
        }
        using var update = db.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = @"UPDATE terminal_sessions SET recording_relative_path=$path,
            recording_bytes=$bytes,recording_sha256=$sha256,recording_complete=$complete,
            recording_status=$status,command_count=$commands,output_bytes=$output,
            last_activity_ms=$now WHERE id=$id";
        update.Parameters.AddWithValue("$path", recording.RelativePath);
        update.Parameters.AddWithValue("$bytes", recording.Bytes);
        update.Parameters.AddWithValue("$sha256", recording.Sha256);
        update.Parameters.AddWithValue("$complete", recording.Complete ? 1 : 0);
        update.Parameters.AddWithValue("$status", recording.Complete ? "COMPLETE" : "INCOMPLETE");
        update.Parameters.AddWithValue("$commands", recording.InputCount);
        update.Parameters.AddWithValue("$output", recording.OutputBytes);
        update.Parameters.AddWithValue("$now", TerminalNowMs());
        update.Parameters.AddWithValue("$id", sessionId);
        if (update.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Terminal session was not found.");
        transaction.Commit();
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

    public TerminalSessionPage QueryTerminalSessions(TerminalSessionQuery query)
    {
        var where = new List<string>();
        using var db = Open();
        using var command = db.CreateCommand();
        void Add(string sql, string name, object value) { where.Add(sql); command.Parameters.AddWithValue(name, value); }
        if (query.TargetId is not null) Add("target_id=$target", "$target", query.TargetId);
        if (query.SiteName is not null) Add("site_name=$site", "$site", query.SiteName);
        if (query.Protocol is not null) Add("protocol=$protocol", "$protocol", query.Protocol.ToUpperInvariant());
        if (query.FromMs is not null) Add("started_ms >= $from", "$from", query.FromMs.Value);
        if (query.ToMs is not null) Add("started_ms <= $to", "$to", query.ToMs.Value);
        if (query.State is not null) Add("state=$state", "$state", query.State.ToUpperInvariant());
        if (query.Integrity is not null) Add("recording_status=$integrity", "$integrity", query.Integrity.ToUpperInvariant());
        if (query.Keyword is not null)
        {
            Add("(target_name LIKE $keyword OR id LIKE $keyword OR EXISTS (SELECT 1 FROM terminal_events e WHERE e.session_id=terminal_sessions.id AND e.detail_json LIKE $keyword))",
                "$keyword", "%" + query.Keyword + "%");
        }
        var filter = where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where);
        using var count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM terminal_sessions" + filter;
        foreach (Microsoft.Data.Sqlite.SqliteParameter parameter in command.Parameters)
            count.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
        var total = Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        var sort = query.Sort.ToLowerInvariant() switch
        {
            "ended" => "COALESCE(ended_ms,started_ms)",
            "target" => "target_name",
            "protocol" => "protocol",
            "state" => "state",
            "bytes" => "recording_bytes",
            "integrity" => "recording_status",
            _ => "started_ms"
        };
        command.CommandText = TerminalSessionSelect + filter + $" ORDER BY {sort} {(query.Descending ? "DESC" : "ASC")}, id DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", query.PageSize);
        command.Parameters.AddWithValue("$offset", (query.Page - 1) * query.PageSize);
        using var reader = command.ExecuteReader();
        var items = new List<TerminalSessionSnapshot>();
        while (reader.Read()) items.Add(ReadTerminalSession(reader));
        return new(query.Page, query.PageSize, total, items);
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

    public IReadOnlyList<TerminalRecordingSegmentSnapshot> TerminalRecordingSegments(string sessionId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT session_id,segment_number,relative_path,started_ms,ended_ms,
            byte_count,record_count,sha256,state FROM terminal_recording_segments
            WHERE session_id=$id ORDER BY segment_number";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var result = new List<TerminalRecordingSegmentSnapshot>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8)));
        return result;
    }

    public void SetTerminalRecordingIntegrity(string sessionId, TerminalRecordingIntegritySnapshot integrity)
    {
        using var db = Open();
        using var update = db.CreateCommand();
        update.CommandText = @"UPDATE terminal_sessions SET recording_status=$status,
            recording_complete=CASE WHEN $status='COMPLETE' THEN 1 ELSE 0 END,
            recording_verified_ms=$verified,recording_integrity_detail=$detail WHERE id=$id";
        update.Parameters.AddWithValue("$status", integrity.Status);
        update.Parameters.AddWithValue("$verified", integrity.VerifiedAtMs);
        update.Parameters.AddWithValue("$detail", integrity.Detail);
        update.Parameters.AddWithValue("$id", sessionId);
        update.ExecuteNonQuery();
    }

    public void StartTerminalExport(string id, string sessionId, string format, string integrity, string operatorIdentity)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO terminal_exports(id,session_id,format,started_ms,integrity_status,operator_identity,state)
            VALUES($id,$session,$format,$now,$integrity,$operator,'STARTED')";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$format", format);
        command.Parameters.AddWithValue("$now", TerminalNowMs());
        command.Parameters.AddWithValue("$integrity", integrity);
        command.Parameters.AddWithValue("$operator", operatorIdentity);
        command.ExecuteNonQuery();
    }

    public void CompleteTerminalExport(string id, string state, long bytes, string? detail)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE terminal_exports SET ended_ms=$now,state=$state,byte_count=$bytes,detail=$detail WHERE id=$id";
        command.Parameters.AddWithValue("$now", TerminalNowMs());
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TerminalExportAuditSnapshot> TerminalExports(string sessionId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT id,session_id,format,started_ms,ended_ms,integrity_status,
            operator_identity,state,byte_count,detail FROM terminal_exports WHERE session_id=$id ORDER BY started_ms DESC";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var result = new List<TerminalExportAuditSnapshot>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        return result;
    }

    private static readonly string TerminalSessionSelect = @"SELECT id,target_id,target_name,protocol,mode,
        host,port,serial_port,serial_config,state,started_ms,ended_ms,last_activity_ms,connection_result,
        disconnect_reason,negotiated_algorithm,host_key_event,recording_relative_path,recording_bytes,
        recording_sha256,recording_complete,operator_identity,site_name,command_count,output_bytes,
        recording_status,recording_verified_ms,recording_integrity_detail FROM terminal_sessions";

    private static TerminalSessionSnapshot ReadTerminalSession(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        NullableString(reader, 5), NullableInt(reader, 6), NullableString(reader, 7), NullableString(reader, 8),
        reader.GetString(9), reader.GetInt64(10), NullableLong(reader, 11), reader.GetInt64(12),
        NullableString(reader, 13), NullableString(reader, 14), NullableString(reader, 15), NullableString(reader, 16),
        NullableString(reader, 17), reader.GetInt64(18), NullableString(reader, 19), reader.GetInt32(20) != 0,
        reader.GetString(21), NullableString(reader, 22), reader.GetInt32(23), reader.GetInt64(24), reader.GetString(25),
        NullableLong(reader, 26), NullableString(reader, 27));

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
