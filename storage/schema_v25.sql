-- Durable, fail-closed configuration restore intent. Snapshot bodies remain
-- in the backup authority and are never copied into the operation record.
CREATE TABLE IF NOT EXISTS config_restore_operations(
    id TEXT PRIMARY KEY,
    actor_user_id TEXT NOT NULL,
    source_snapshot_id TEXT NOT NULL,
    target_profile_id TEXT NOT NULL,
    target_device_id TEXT NOT NULL,
    target_site_id TEXT NOT NULL,
    source_sha256 TEXT NOT NULL,
    expected_current_snapshot_id TEXT,
    confirmation_token_sha256 TEXT NOT NULL,
    expires_ms INTEGER NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('PREVIEWED','CLAIMED','EXECUTING','VERIFYING','SUCCEEDED','FAILED','ROLLBACK_PENDING','ROLLING_BACK','ROLLBACK_VERIFIED','REQUIRES_REVIEW')),
    attempt_id TEXT,
    detail TEXT,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_config_restore_operations_target
    ON config_restore_operations(target_profile_id,updated_ms);
CREATE INDEX IF NOT EXISTS ix_config_restore_operations_actor
    ON config_restore_operations(actor_user_id,updated_ms);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(25,CAST(strftime('%s','now') AS INTEGER)*1000);
