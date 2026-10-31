-- Device creation idempotency keeps a repeated browser submit from creating
-- a second inventory/profile pair. It contains no credential secret.
CREATE TABLE IF NOT EXISTS device_create_idempotency(
    idempotency_key TEXT PRIMARY KEY,
    actor TEXT NOT NULL,
    device_id TEXT NOT NULL REFERENCES device_inventory(id),
    request_sha256 TEXT NOT NULL,
    created_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_device_create_idempotency_created
    ON device_create_idempotency(created_ms);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(17,CAST(strftime('%s','now') AS INTEGER)*1000);
