-- H3C offline/live-fact projection. Current rows are the newest fact per
-- profile/kind/identity; `status=STALE` explicitly marks offline evidence.
CREATE TABLE IF NOT EXISTS h3c_facts(
    id TEXT PRIMARY KEY,
    profile_id TEXT NOT NULL REFERENCES h3c_device_profiles(id) ON DELETE CASCADE,
    kind TEXT NOT NULL,
    identity_key TEXT NOT NULL,
    status TEXT NOT NULL CHECK(status IN ('STALE','EMPTY','UNSUPPORTED','PARTIAL','FAILED')),
    source TEXT NOT NULL,
    captured_ms INTEGER NOT NULL,
    is_current INTEGER NOT NULL DEFAULT 1 CHECK(is_current IN (0,1)),
    payload_json TEXT NOT NULL,
    UNIQUE(profile_id,kind,identity_key,source,captured_ms)
);
CREATE INDEX IF NOT EXISTS idx_h3c_facts_current
    ON h3c_facts(profile_id,is_current,kind,captured_ms DESC);
CREATE INDEX IF NOT EXISTS idx_h3c_facts_history
    ON h3c_facts(profile_id,kind,captured_ms DESC);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(15,CAST(strftime('%s','now') AS INTEGER)*1000);
