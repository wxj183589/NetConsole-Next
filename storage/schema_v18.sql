-- Durable R3 wireless projection. Raw/Facts remain the evidence authority;
-- these tables retain the normalized identity and source observations used by
-- the cross-AC read model.
CREATE TABLE IF NOT EXISTS wireless_ac_domains(
    id TEXT PRIMARY KEY,
    domain_kind TEXT NOT NULL,
    topology_status TEXT NOT NULL,
    member_device_ids_json TEXT NOT NULL,
    source_evidence_refs_json TEXT NOT NULL,
    confirmed INTEGER NOT NULL CHECK(confirmed IN (0,1)),
    revision TEXT NOT NULL,
    as_of_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS wireless_ap_resources(
    id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    identity_status TEXT NOT NULL,
    serial_evidence TEXT,
    normalized_mac_set_json TEXT NOT NULL,
    source_observation_ids_json TEXT NOT NULL,
    source_ac_domain_ids_json TEXT NOT NULL,
    scope_status TEXT NOT NULL,
    first_seen_ms INTEGER NOT NULL,
    last_seen_ms INTEGER NOT NULL,
    source_revision TEXT NOT NULL,
    projection_revision TEXT NOT NULL,
    conflict_reason TEXT,
    state_conflict_reason TEXT,
    updated_ms INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS wireless_ap_observations(
    id TEXT PRIMARY KEY,
    resource_id TEXT NOT NULL REFERENCES wireless_ap_resources(id) ON DELETE CASCADE,
    source_ac_device_id TEXT NOT NULL,
    source_ac_domain_id TEXT NOT NULL,
    source_site_id TEXT NOT NULL,
    observed_ap_name TEXT NOT NULL,
    serial_evidence TEXT,
    normalized_mac_set_json TEXT NOT NULL,
    ap_model TEXT,
    ap_group TEXT,
    raw_state TEXT NOT NULL,
    normalized_state TEXT NOT NULL,
    state_confidence TEXT NOT NULL,
    captured_ms INTEGER NOT NULL,
    received_ms INTEGER NOT NULL,
    raw_manifest_ref TEXT NOT NULL,
    parser_revision TEXT NOT NULL,
    source_revision TEXT NOT NULL,
    relation_status TEXT NOT NULL,
    UNIQUE(resource_id,source_ac_device_id,source_revision,captured_ms)
);
CREATE INDEX IF NOT EXISTS idx_wireless_ap_observations_resource
    ON wireless_ap_observations(resource_id,captured_ms DESC);
CREATE INDEX IF NOT EXISTS idx_wireless_ap_observations_source
    ON wireless_ap_observations(source_ac_device_id,source_site_id,captured_ms DESC);
CREATE TABLE IF NOT EXISTS wireless_ap_groups(
    id TEXT PRIMARY KEY,
    source_ac_device_id TEXT NOT NULL,
    source_ac_domain_id TEXT NOT NULL,
    source_group_key TEXT NOT NULL,
    display_name TEXT NOT NULL,
    group_scope TEXT NOT NULL,
    member_resource_ids_json TEXT NOT NULL,
    source_revision TEXT NOT NULL,
    scope_status TEXT NOT NULL,
    updated_ms INTEGER NOT NULL,
    UNIQUE(source_ac_device_id,source_group_key)
);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(18,CAST(strftime('%s','now') AS INTEGER)*1000);
