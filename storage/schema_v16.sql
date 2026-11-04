-- Command-level Raw output ledger for multi-command task runs.  The legacy
-- task_run_raw_outputs table remains authoritative for single-command runs.
CREATE TABLE IF NOT EXISTS task_run_command_outputs(
 task_run_id TEXT NOT NULL REFERENCES task_runs(id) ON DELETE CASCADE,
 command_index INTEGER NOT NULL CHECK(command_index>=0 AND command_index<32),
 command TEXT NOT NULL,
 relative_path TEXT NOT NULL UNIQUE REFERENCES raw_segments(relative_path),
 byte_count INTEGER NOT NULL CHECK(byte_count>=0),
 sha256 TEXT NOT NULL,
 raw_segment_sha256 TEXT NOT NULL,
 parsed_json TEXT NOT NULL,
 captured_ms INTEGER NOT NULL,
 capture_status TEXT NOT NULL DEFAULT 'COMPLETE',
 parse_status TEXT NOT NULL DEFAULT 'UNCLASSIFIED',
 PRIMARY KEY(task_run_id,command_index),
 CHECK(capture_status IN ('COMPLETE','PARTIAL')),
 CHECK(parse_status IN ('PARSED','EMPTY','UNRECOGNIZED','PARTIAL','UNCLASSIFIED'))
);
CREATE INDEX IF NOT EXISTS idx_task_command_outputs_path
    ON task_run_command_outputs(relative_path);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(16,CAST(strftime('%s','now') AS INTEGER)*1000);
