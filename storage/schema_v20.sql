-- Persisted parent plans for a bounded multi-target H3C collection.
-- Child task definitions/runs remain the execution authority; this schema only
-- records their immutable relationship and aggregate status.
CREATE TABLE IF NOT EXISTS task_plans(
    id TEXT PRIMARY KEY,
    task_type TEXT NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('QUEUED','RUNNING','SUCCEEDED','PARTIAL_FAILED','FAILED','CANCELLED')),
    actor_user_id TEXT NOT NULL,
    command_plan_json TEXT NOT NULL,
    claims_revision TEXT NOT NULL,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS task_plan_items(
    plan_id TEXT NOT NULL REFERENCES task_plans(id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
    target TEXT NOT NULL,
    definition_id TEXT NOT NULL REFERENCES task_definitions(id),
    run_id TEXT REFERENCES task_runs(id),
    state TEXT NOT NULL,
    error TEXT,
    PRIMARY KEY(plan_id, ordinal),
    UNIQUE(plan_id, target)
);
CREATE INDEX IF NOT EXISTS idx_task_plan_items_run ON task_plan_items(run_id);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(20,CAST(strftime('%s','now') AS INTEGER)*1000);
