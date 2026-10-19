-- Auditable dispositions do not delete Raw or recovery evidence.
CREATE TABLE IF NOT EXISTS raw_issue_dispositions(
 id TEXT PRIMARY KEY,
 audit_id TEXT NOT NULL UNIQUE REFERENCES raw_integrity_audits(id),
 disposition TEXT NOT NULL CHECK(disposition IN ('QUARANTINED','RESOLVED_VERIFIED')),
 actor TEXT NOT NULL,
 detail TEXT NOT NULL,
 evidence_path TEXT,
 observed_sha256 TEXT,
 disposed_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_raw_issue_disposition_time ON raw_issue_dispositions(disposed_ms);
