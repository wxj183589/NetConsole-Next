-- Minimal H3C read-only collection identity and durable Raw result index.
CREATE TABLE IF NOT EXISTS h3c_device_profiles(
 id TEXT PRIMARY KEY,
 source_id TEXT NOT NULL UNIQUE REFERENCES collection_sources(id),
 host TEXT NOT NULL,
 port INTEGER NOT NULL CHECK(port BETWEEN 1 AND 65535),
 username TEXT NOT NULL,
 credential_reference TEXT NOT NULL,
 host_key_sha256 TEXT NOT NULL,
 enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
 created_ms INTEGER NOT NULL,
 updated_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_h3c_profiles_enabled ON h3c_device_profiles(enabled,updated_ms);

CREATE TABLE IF NOT EXISTS task_run_raw_outputs(
 task_run_id TEXT PRIMARY KEY REFERENCES task_runs(id),
 relative_path TEXT NOT NULL UNIQUE REFERENCES raw_segments(relative_path),
 command TEXT NOT NULL,
 byte_count INTEGER NOT NULL CHECK(byte_count>=0),
 sha256 TEXT NOT NULL,
 raw_segment_sha256 TEXT NOT NULL,
 parsed_json TEXT NOT NULL,
 captured_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_task_raw_output_path ON task_run_raw_outputs(relative_path);

INSERT OR IGNORE INTO schema_versions(version,applied_ms)
 VALUES(4,CAST(strftime('%s','now') AS INTEGER)*1000);
