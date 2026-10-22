CREATE TABLE IF NOT EXISTS terminal_sessions(
    id TEXT PRIMARY KEY,
    target_id TEXT NOT NULL,
    target_name TEXT NOT NULL,
    protocol TEXT NOT NULL CHECK(protocol IN ('SSH','TELNET','SERIAL')),
    mode TEXT NOT NULL CHECK(mode IN ('RESTRICTED_READONLY')),
    host TEXT,
    port INTEGER,
    serial_port TEXT,
    serial_config TEXT,
    state TEXT NOT NULL CHECK(state IN ('CONNECTING','CONNECTED','DISCONNECTED','FAILED','CLOSED')),
    started_ms INTEGER NOT NULL,
    ended_ms INTEGER,
    last_activity_ms INTEGER NOT NULL,
    connection_result TEXT,
    disconnect_reason TEXT,
    negotiated_algorithm TEXT,
    host_key_event TEXT,
    recording_relative_path TEXT,
    recording_bytes INTEGER NOT NULL DEFAULT 0,
    recording_sha256 TEXT,
    recording_complete INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_terminal_sessions_target_started
    ON terminal_sessions(target_id, started_ms DESC);
CREATE INDEX IF NOT EXISTS idx_terminal_sessions_started
    ON terminal_sessions(started_ms DESC);

CREATE TABLE IF NOT EXISTS terminal_events(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL REFERENCES terminal_sessions(id) ON DELETE CASCADE,
    at_ms INTEGER NOT NULL,
    event_type TEXT NOT NULL CHECK(event_type IN (
        'CONNECTING','CONNECTED','AUTHENTICATED','COMMAND_SUBMITTED','COMMAND_REJECTED',
        'OUTPUT_RECEIVED','HOST_KEY_CHANGED','RECONNECTING','DISCONNECTED','SESSION_CLOSED',
        'STORAGE_ERROR','INPUT_REJECTED','BACKPRESSURE')),
    detail_json TEXT NOT NULL DEFAULT '{}'
);
CREATE INDEX IF NOT EXISTS idx_terminal_events_session_time
    ON terminal_events(session_id, at_ms, id);

INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(6,CAST(strftime('%s','now') AS INTEGER)*1000);
