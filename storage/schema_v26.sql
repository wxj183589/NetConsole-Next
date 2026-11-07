-- Preserve wireless plan retries independently from the original plan item run.
CREATE TABLE IF NOT EXISTS task_plan_retries(
    id TEXT PRIMARY KEY,
    plan_id TEXT NOT NULL REFERENCES task_plans(id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,
    original_run_id TEXT NOT NULL REFERENCES task_runs(id),
    retry_run_id TEXT NOT NULL UNIQUE REFERENCES task_runs(id),
    command_indexes_json TEXT NOT NULL,
    actor_user_id TEXT NOT NULL,
    created_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_task_plan_retries_plan
    ON task_plan_retries(plan_id,ordinal,created_ms,id);
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(26,CAST(strftime('%s','now') AS INTEGER)*1000);
