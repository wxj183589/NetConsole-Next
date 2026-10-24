CREATE TABLE IF NOT EXISTS web_password_reset_audit(
    id TEXT PRIMARY KEY,
    username TEXT NOT NULL,
    at_ms INTEGER NOT NULL,
    source TEXT NOT NULL,
    result TEXT NOT NULL
);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(10,CAST(strftime('%s','now') AS INTEGER)*1000);
