-- Additive G1 metadata for incremental Raw verification and persistent simulated collection tasks.
CREATE TABLE IF NOT EXISTS raw_integrity_audits(
 id TEXT PRIMARY KEY,
 relative_path TEXT NOT NULL,
 detected_ms INTEGER NOT NULL,
 reason TEXT NOT NULL,
 expected_sha256 TEXT,
 observed_sha256 TEXT,
 expected_bytes INTEGER,
 observed_bytes INTEGER,
 evidence_path TEXT,
 detail TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_raw_audits_path_time ON raw_integrity_audits(relative_path,detected_ms);

CREATE TABLE IF NOT EXISTS task_definitions(
 id TEXT PRIMARY KEY,
 task_type TEXT NOT NULL,
 target TEXT NOT NULL,
 request_json TEXT NOT NULL,
 timeout_ms INTEGER NOT NULL CHECK(timeout_ms BETWEEN 100 AND 600000),
 enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
 restart_policy TEXT NOT NULL DEFAULT 'NEVER',
 created_ms INTEGER NOT NULL,
 updated_ms INTEGER NOT NULL
);
