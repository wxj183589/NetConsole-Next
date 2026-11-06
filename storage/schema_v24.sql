-- Rebuild only the disposable AP Group projection cache. Raw/Facts remain the
-- authority. Legacy rows used display names as keys and cannot be confirmed.
ALTER TABLE wireless_ap_groups RENAME TO wireless_ap_groups_v18;
CREATE TABLE wireless_ap_groups(
    id TEXT PRIMARY KEY,
    source_ac_device_id TEXT NOT NULL,
    source_ac_domain_id TEXT NOT NULL,
    source_group_key TEXT,
    display_name TEXT NOT NULL,
    group_scope TEXT NOT NULL,
    member_resource_ids_json TEXT NOT NULL,
    source_revision TEXT NOT NULL,
    scope_status TEXT NOT NULL,
    updated_ms INTEGER NOT NULL,
    group_identity_key TEXT UNIQUE,
    group_identity_status TEXT NOT NULL,
    membership_revision TEXT NOT NULL,
    source_observation_ids_json TEXT NOT NULL,
    source_ac_device_ids_json TEXT NOT NULL,
    source_evidence_refs_json TEXT NOT NULL,
    observed_at_ms INTEGER NOT NULL,
    conflict_reason TEXT,
    source_group_observations_json TEXT NOT NULL
);
INSERT INTO wireless_ap_groups
    (id,source_ac_device_id,source_ac_domain_id,source_group_key,display_name,group_scope,
     member_resource_ids_json,source_revision,scope_status,updated_ms,group_identity_key,
     group_identity_status,membership_revision,source_observation_ids_json,source_ac_device_ids_json,
     source_evidence_refs_json,observed_at_ms,conflict_reason,source_group_observations_json)
SELECT id,source_ac_device_id,source_ac_domain_id,NULL,display_name,'AC',
       member_resource_ids_json,source_revision,scope_status,updated_ms,NULL,
       'CANDIDATE','', '[]',json_array(source_ac_device_id),'[]',updated_ms,
       'LEGACY_GROUP_NAME_ONLY','[]'
FROM wireless_ap_groups_v18;
DROP TABLE wireless_ap_groups_v18;
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(24,CAST(strftime('%s','now') AS INTEGER)*1000);
