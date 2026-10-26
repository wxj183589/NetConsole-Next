-- NetConsole Next V12: persistent device inventory batch operations.
-- This migration only adds operation/audit state; it does not rewrite inventory history.
CREATE TABLE IF NOT EXISTS device_operations(
    id TEXT PRIMARY KEY,
    actor TEXT NOT NULL,
    kind TEXT NOT NULL CHECK(kind IN ('BATCH_ENABLE','BATCH_DISABLE','BATCH_SET_GROUP','BATCH_SET_SITE','BATCH_UPDATE_METADATA')),
    state TEXT NOT NULL CHECK(state IN ('PREVIEWED','PENDING_CONFIRM','QUEUED','RUNNING','SUCCEEDED','FAILED','CONFLICT','CANCELLED','INTERRUPTED')),
    request_json TEXT NOT NULL,
    progress_json TEXT NOT NULL DEFAULT '{}',
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    started_ms INTEGER,
    completed_ms INTEGER,
    idempotency_key TEXT NOT NULL,
    UNIQUE(actor,idempotency_key)
);
CREATE INDEX IF NOT EXISTS idx_device_operations_actor_state ON device_operations(actor,state,updated_ms);
CREATE INDEX IF NOT EXISTS idx_device_operations_state_time ON device_operations(state,updated_ms);

CREATE TABLE IF NOT EXISTS device_operation_items(
    operation_id TEXT NOT NULL REFERENCES device_operations(id) ON DELETE CASCADE,
    -- Keep the explicit target even when the device disappeared between preview and readback;
    -- the confirm step records that as DEVICE_NOT_FOUND instead of losing the audit item.
    device_id TEXT NOT NULL,
    expected_revision INTEGER NOT NULL,
    before_json TEXT NOT NULL,
    requested_json TEXT NOT NULL,
    outcome TEXT NOT NULL DEFAULT 'PENDING' CHECK(outcome IN ('PENDING','UPDATED','CONFLICT','FAILED','CANCELLED')),
    detail TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(operation_id,device_id)
);
CREATE INDEX IF NOT EXISTS idx_device_operation_items_device ON device_operation_items(device_id,operation_id);

INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(12,CAST(strftime('%s','now') AS INTEGER)*1000);
