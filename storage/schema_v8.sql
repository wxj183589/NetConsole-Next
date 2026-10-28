-- G2.4: device-scoped SSH compatibility and host-key lifecycle.
-- Only normalized SHA-256 fingerprints and metadata are retained.
-- `terminal_encoding` is a device-scoped presentation setting; AUTO probes
-- UTF-8 first and falls back to GB18030 for Telnet/console text.
-- Protocol AUTO is SSH-first and may use Telnet only when explicitly enabled.
CREATE TABLE IF NOT EXISTS ssh_host_keys(
    device_id TEXT PRIMARY KEY REFERENCES h3c_device_profiles(id) ON DELETE CASCADE,
    host TEXT NOT NULL,
    port INTEGER NOT NULL CHECK(port BETWEEN 1 AND 65535),
    update_policy TEXT NOT NULL CHECK(update_policy IN ('AUTO_UPDATE','STRICT')),
    current_key_type TEXT,
    current_sha256 TEXT,
    first_seen_ms INTEGER,
    last_verified_ms INTEGER,
    last_changed_ms INTEGER,
    revision INTEGER NOT NULL DEFAULT 0 CHECK(revision >= 0)
);
CREATE TABLE IF NOT EXISTS ssh_host_key_events(
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL REFERENCES h3c_device_profiles(id) ON DELETE CASCADE,
    host TEXT NOT NULL,
    port INTEGER NOT NULL,
    key_type TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    observed_ms INTEGER NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('PENDING','EFFECTIVE','REJECTED','FAILED')),
    reason TEXT,
    correlation_id TEXT NOT NULL,
    UNIQUE(device_id,key_type,sha256,state)
);
CREATE INDEX IF NOT EXISTS idx_ssh_host_key_events_device_time
    ON ssh_host_key_events(device_id,observed_ms DESC);
CREATE TABLE IF NOT EXISTS ssh_host_key_pending(
    correlation_id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL REFERENCES h3c_device_profiles(id) ON DELETE CASCADE,
    host TEXT NOT NULL,
    port INTEGER NOT NULL,
    key_type TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    observed_ms INTEGER NOT NULL,
    prior_revision INTEGER NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('PENDING','COMMITTED','FAILED')),
    detail TEXT
);
CREATE INDEX IF NOT EXISTS idx_ssh_host_key_pending_device
    ON ssh_host_key_pending(device_id,state,observed_ms DESC);
CREATE UNIQUE INDEX IF NOT EXISTS idx_ssh_host_key_pending_unique
    ON ssh_host_key_pending(device_id,key_type,sha256)
    WHERE state='PENDING';
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(8,CAST(strftime('%s','now') AS INTEGER)*1000);
