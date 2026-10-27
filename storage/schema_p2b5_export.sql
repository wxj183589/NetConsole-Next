-- NetConsole Next P2B5: isolated local export operation records.
-- This auxiliary schema is never registered by Catalog.Initialize.
CREATE TABLE IF NOT EXISTS p2b5_exports(
    id TEXT PRIMARY KEY,
    actor TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    format TEXT NOT NULL CHECK(format IN ('JSON','CSV','HTML','TXT')),
    status TEXT NOT NULL CHECK(status IN ('SUCCEEDED','FAILED','CANCELLED')),
    snapshot_json TEXT NOT NULL,
    manifest_json TEXT NOT NULL,
    relative_path TEXT,
    content_sha256 TEXT,
    row_count INTEGER NOT NULL CHECK(row_count>=0),
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    error_code TEXT,
    UNIQUE(actor,idempotency_key)
);
CREATE INDEX IF NOT EXISTS idx_p2b5_exports_actor_time ON p2b5_exports(actor,updated_ms);
