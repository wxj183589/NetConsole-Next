-- Site-scoped SSH Jump Host configuration. Password is device-local data and
-- never returned by API or written to task/raw/audit payloads.
CREATE TABLE IF NOT EXISTS site_ssh_jump_hosts(
    site_id TEXT PRIMARY KEY REFERENCES sites(id) ON DELETE CASCADE,
    host TEXT NOT NULL,
    port INTEGER NOT NULL CHECK(port BETWEEN 1 AND 65535),
    username TEXT NOT NULL,
    credential_reference TEXT NOT NULL,
    credential_password TEXT,
    host_key_sha256 TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
    updated_ms INTEGER NOT NULL
);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(22,CAST(strftime('%s','now') AS INTEGER)*1000);
