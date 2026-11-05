-- Immutable logical collection manifests bind one wireless projection revision
-- to every source AC observation without copying raw command output.
CREATE TABLE IF NOT EXISTS wireless_collection_manifests(
    id TEXT PRIMARY KEY,
    source_revision TEXT NOT NULL UNIQUE,
    scope_status TEXT NOT NULL,
    source_ac_device_ids_json TEXT NOT NULL,
    first_captured_ms INTEGER NOT NULL,
    last_captured_ms INTEGER NOT NULL,
    observation_count INTEGER NOT NULL CHECK(observation_count >= 0),
    status TEXT NOT NULL,
    created_ms INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS wireless_collection_manifest_items(
    manifest_id TEXT NOT NULL REFERENCES wireless_collection_manifests(id) ON DELETE CASCADE,
    observation_id TEXT NOT NULL,
    source_ac_device_id TEXT NOT NULL,
    source_ac_domain_id TEXT NOT NULL,
    source_site_id TEXT NOT NULL,
    raw_manifest_ref TEXT NOT NULL,
    source_revision TEXT NOT NULL,
    captured_ms INTEGER NOT NULL,
    outcome TEXT NOT NULL,
    PRIMARY KEY(manifest_id,observation_id)
);
CREATE INDEX IF NOT EXISTS idx_wireless_collection_manifest_items_source
    ON wireless_collection_manifest_items(source_ac_device_id,captured_ms DESC);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(19,CAST(strftime('%s','now') AS INTEGER)*1000);
