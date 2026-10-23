-- Web PR-1: local Web users and independent browser sessions.
-- Password material is stored as PBKDF2 salt/hash blobs; plaintext never enters SQLite.
CREATE TABLE IF NOT EXISTS web_users(
    id TEXT PRIMARY KEY,
    username TEXT NOT NULL UNIQUE COLLATE NOCASE,
    role TEXT NOT NULL CHECK(role IN ('ADMIN','SUPERADMIN')),
    password_salt BLOB NOT NULL,
    password_hash BLOB NOT NULL,
    password_iterations INTEGER NOT NULL,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    disabled INTEGER NOT NULL DEFAULT 0 CHECK(disabled IN (0,1))
);
CREATE TABLE IF NOT EXISTS web_sessions(
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL REFERENCES web_users(id) ON DELETE CASCADE,
    token_hash BLOB NOT NULL UNIQUE,
    csrf_hash BLOB NOT NULL,
    created_ms INTEGER NOT NULL,
    last_seen_ms INTEGER NOT NULL,
    expires_ms INTEGER NOT NULL,
    revoked_ms INTEGER,
    remote_ip TEXT
);
CREATE INDEX IF NOT EXISTS idx_web_sessions_user ON web_sessions(user_id,expires_ms);
CREATE TABLE IF NOT EXISTS instance_identity(
    id INTEGER PRIMARY KEY CHECK(id=1),
    instance_id TEXT NOT NULL UNIQUE,
    created_ms INTEGER NOT NULL
);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(9,CAST(strftime('%s','now') AS INTEGER)*1000);
