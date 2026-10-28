CREATE TABLE IF NOT EXISTS terminal_recording_segments(
    session_id TEXT NOT NULL REFERENCES terminal_sessions(id) ON DELETE CASCADE,
    segment_number INTEGER NOT NULL,
    relative_path TEXT NOT NULL,
    started_ms INTEGER NOT NULL,
    ended_ms INTEGER NOT NULL,
    byte_count INTEGER NOT NULL,
    record_count INTEGER NOT NULL,
    sha256 TEXT,
    state TEXT NOT NULL CHECK(state IN ('COMPLETE','INCOMPLETE','MISSING','CORRUPTED')),
    PRIMARY KEY(session_id, segment_number),
    UNIQUE(relative_path)
);
CREATE INDEX IF NOT EXISTS idx_terminal_recording_segments_time
    ON terminal_recording_segments(session_id, started_ms, segment_number);

CREATE TABLE IF NOT EXISTS terminal_exports(
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES terminal_sessions(id) ON DELETE CASCADE,
    format TEXT NOT NULL CHECK(format IN ('TXT','JSONL','HTML','MANIFEST')),
    started_ms INTEGER NOT NULL,
    ended_ms INTEGER,
    integrity_status TEXT NOT NULL,
    operator_identity TEXT NOT NULL DEFAULT 'UNATTRIBUTED',
    state TEXT NOT NULL CHECK(state IN ('STARTED','COMPLETED','FAILED')),
    byte_count INTEGER,
    detail TEXT
);
CREATE INDEX IF NOT EXISTS idx_terminal_exports_session_started
    ON terminal_exports(session_id, started_ms DESC);

INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(7,CAST(strftime('%s','now') AS INTEGER)*1000);
