-- NetConsole Next V14: isolated P2B-4 write boundary.
-- This schema is additive and is applied only by the explicit isolated test
-- migration harness. Catalog.Initialize intentionally does not apply it.

CREATE TABLE IF NOT EXISTS p2b4_authority(
    id INTEGER PRIMARY KEY CHECK(id=1),
    mode TEXT NOT NULL CHECK(mode='ISOLATED_TEST'),
    root_path TEXT NOT NULL,
    migration_authorized INTEGER NOT NULL CHECK(migration_authorized=1),
    created_ms INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS p2b4_operations(
    id TEXT PRIMARY KEY,
    actor TEXT NOT NULL,
    kind TEXT NOT NULL CHECK(kind IN ('ADDRESS_CREATE','ADDRESS_UPDATE','MEMBERSHIP_CREATE','VLAN_PLAN_CREATE','VLAN_PLAN_UPDATE','NETWORK_REVIEW','IMPORT')),
    state TEXT NOT NULL CHECK(state IN ('PREVIEWED','PENDING_CONFIRM','QUEUED','RUNNING','SUCCEEDED','FAILED','CONFLICT','CANCELLED','INTERRUPTED')),
    request_json TEXT NOT NULL,
    progress_json TEXT NOT NULL DEFAULT '{}',
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    started_ms INTEGER,
    completed_ms INTEGER,
    idempotency_key TEXT NOT NULL,
    preview_expires_ms INTEGER,
    UNIQUE(actor,idempotency_key)
);
CREATE INDEX IF NOT EXISTS idx_p2b4_operations_actor_state ON p2b4_operations(actor,state,updated_ms);
CREATE INDEX IF NOT EXISTS idx_p2b4_operations_state_time ON p2b4_operations(state,updated_ms);

CREATE TABLE IF NOT EXISTS p2b4_operation_items(
    operation_id TEXT NOT NULL REFERENCES p2b4_operations(id) ON DELETE CASCADE,
    item_index INTEGER NOT NULL,
    entity_type TEXT NOT NULL,
    target_id TEXT,
    expected_revision INTEGER,
    before_json TEXT NOT NULL DEFAULT '{}',
    requested_json TEXT NOT NULL DEFAULT '{}',
    outcome TEXT NOT NULL DEFAULT 'PENDING' CHECK(outcome IN ('PENDING','UPDATED','CREATED','CONFLICT','FAILED','CANCELLED')),
    detail TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(operation_id,item_index)
);

CREATE TABLE IF NOT EXISTS p2b4_address_history(
    address_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    snapshot_json TEXT NOT NULL,
    archived_ms INTEGER NOT NULL,
    PRIMARY KEY(address_id,revision)
);

CREATE TABLE IF NOT EXISTS p2b4_vlan_history(
    plan_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    snapshot_json TEXT NOT NULL,
    archived_ms INTEGER NOT NULL,
    PRIMARY KEY(plan_id,revision)
);

CREATE TABLE IF NOT EXISTS network_reviews(
    id TEXT PRIMARY KEY,
    actor TEXT NOT NULL,
    target_type TEXT NOT NULL CHECK(target_type IN ('ADDRESS','MEMBERSHIP','DOMAIN','VLAN_PLAN')),
    target_id TEXT NOT NULL,
    source_kind TEXT NOT NULL CHECK(source_kind IN ('PLANNED','OBSERVED','IMPORTED','MANUAL')),
    source_reference TEXT NOT NULL,
    target_revision INTEGER NOT NULL CHECK(target_revision>0),
    decision TEXT NOT NULL CHECK(decision IN ('ACCEPT_PLANNED','ACCEPT_OBSERVED','REJECT','DEFER')),
    reason TEXT NOT NULL,
    conflict_state TEXT NOT NULL CHECK(conflict_state IN ('NONE','STALE_REVIEW','REVIEW_RECORDED')),
    created_ms INTEGER NOT NULL,
    UNIQUE(actor,target_type,target_id,target_revision,decision,source_reference)
);
CREATE INDEX IF NOT EXISTS idx_network_reviews_target ON network_reviews(target_type,target_id,target_revision,created_ms);

CREATE TABLE IF NOT EXISTS network_import_runs(
    id TEXT PRIMARY KEY,
    actor TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    payload_fingerprint TEXT NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('PREVIEWED','PENDING_CONFIRM','SUCCEEDED','CANCELLED','CONFLICT','FAILED')),
    request_json TEXT NOT NULL,
    preview_json TEXT NOT NULL,
    operation_id TEXT NOT NULL REFERENCES p2b4_operations(id) ON DELETE RESTRICT,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    expires_ms INTEGER NOT NULL,
    UNIQUE(actor,idempotency_key)
);
CREATE INDEX IF NOT EXISTS idx_network_import_runs_actor_state ON network_import_runs(actor,state,updated_ms);

INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(14,CAST(strftime('%s','now') AS INTEGER)*1000);
