-- Device inventory is intentionally separate from terminal connection profiles.
CREATE TABLE IF NOT EXISTS device_groups(
    id TEXT PRIMARY KEY,
    site_id TEXT NOT NULL REFERENCES sites(id),
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    UNIQUE(site_id,name)
);
CREATE INDEX IF NOT EXISTS idx_device_groups_site ON device_groups(site_id,name);

CREATE TABLE IF NOT EXISTS device_inventory(
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    management_ip TEXT NOT NULL,
    vendor TEXT NOT NULL DEFAULT '',
    model TEXT NOT NULL DEFAULT '',
    device_type TEXT NOT NULL DEFAULT 'NETWORK_DEVICE',
    site_id TEXT NOT NULL REFERENCES sites(id),
    group_id TEXT REFERENCES device_groups(id),
    description TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    terminal_profile_id TEXT UNIQUE REFERENCES h3c_device_profiles(id)
);
CREATE INDEX IF NOT EXISTS idx_device_inventory_site_group ON device_inventory(site_id,group_id,updated_ms);
CREATE INDEX IF NOT EXISTS idx_device_inventory_ip ON device_inventory(site_id,management_ip);
CREATE INDEX IF NOT EXISTS idx_device_inventory_enabled ON device_inventory(enabled,updated_ms);

INSERT OR IGNORE INTO device_inventory
    (id,name,management_ip,vendor,model,device_type,site_id,description,enabled,created_ms,updated_ms,terminal_profile_id)
SELECT p.id,p.id,p.host,'H3C','', 'H3C_TERMINAL',COALESCE(cs.site_id,'local'),'Legacy terminal profile bridge',
    p.enabled,p.created_ms,p.updated_ms,p.id
FROM h3c_device_profiles p
LEFT JOIN collection_sources cs ON cs.id=p.source_id;

INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(11,CAST(strftime('%s','now') AS INTEGER)*1000);
