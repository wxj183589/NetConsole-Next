-- NetConsole Next V1: for a NEW isolated data root only.
CREATE TABLE IF NOT EXISTS schema_versions (
 version INTEGER PRIMARY KEY, applied_ms INTEGER NOT NULL
);
INSERT OR IGNORE INTO schema_versions VALUES (1,CAST(strftime('%s','now') AS INTEGER)*1000);
CREATE TABLE IF NOT EXISTS sites(id TEXT PRIMARY KEY,name TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS collection_sources(
 id TEXT PRIMARY KEY,
 site_id TEXT NOT NULL REFERENCES sites(id),
 kind TEXT NOT NULL,
 label TEXT NOT NULL,
 enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1))
);
CREATE TABLE IF NOT EXISTS raw_segments(
 id TEXT PRIMARY KEY,
 source_id TEXT NOT NULL REFERENCES collection_sources(id),
 relative_path TEXT NOT NULL UNIQUE,
 state TEXT NOT NULL CHECK(state IN ('FINALIZED','INCOMPLETE','MISSING')),
 first_received_ms INTEGER,
 last_received_ms INTEGER,
 record_count INTEGER,
 byte_count INTEGER NOT NULL CHECK(byte_count>=0),
 sha256 TEXT,
 sealed_ms INTEGER
);
CREATE INDEX IF NOT EXISTS idx_raw_source_time ON raw_segments(source_id,first_received_ms);
CREATE TABLE IF NOT EXISTS task_runs(
 id TEXT PRIMARY KEY,
 source_id TEXT REFERENCES collection_sources(id),
 task_type TEXT NOT NULL,
 state TEXT NOT NULL,
 started_ms INTEGER, ended_ms INTEGER
);
CREATE TABLE IF NOT EXISTS audit_events(
 id TEXT PRIMARY KEY, at_ms INTEGER NOT NULL,
 action TEXT NOT NULL, outcome TEXT NOT NULL,
 detail_json TEXT NOT NULL DEFAULT '{}'
);
INSERT OR IGNORE INTO sites VALUES ('local','Local P0');
INSERT OR IGNORE INTO collection_sources(id,site_id,kind,label)
 VALUES ('syslog-local','local','SYSLOG_UDP','Local P0 Syslog');
