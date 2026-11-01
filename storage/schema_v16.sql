-- Global Site management metadata. Existing Site IDs remain stable.
CREATE INDEX IF NOT EXISTS idx_sites_status_name ON sites(status,name);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(16,CAST(strftime('%s','now') AS INTEGER)*1000);
