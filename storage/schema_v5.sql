-- Atomic H3C task completion and Raw-to-task recovery linkage.
CREATE INDEX IF NOT EXISTS idx_raw_segments_task_run ON raw_segments(task_run_id) WHERE task_run_id IS NOT NULL;
CREATE TRIGGER IF NOT EXISTS task_raw_output_status_insert
BEFORE INSERT ON task_run_raw_outputs
WHEN NEW.capture_status NOT IN ('COMPLETE','PARTIAL') OR
     NEW.parse_status NOT IN ('PARSED','EMPTY','UNRECOGNIZED','PARTIAL','UNCLASSIFIED')
BEGIN
 SELECT RAISE(ABORT,'invalid H3C output status');
END;
CREATE TRIGGER IF NOT EXISTS task_raw_output_status_update
BEFORE UPDATE OF capture_status,parse_status ON task_run_raw_outputs
WHEN NEW.capture_status NOT IN ('COMPLETE','PARTIAL') OR
     NEW.parse_status NOT IN ('PARSED','EMPTY','UNRECOGNIZED','PARTIAL','UNCLASSIFIED')
BEGIN
 SELECT RAISE(ABORT,'invalid H3C output status');
END;
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
 VALUES(5,CAST(strftime('%s','now') AS INTEGER)*1000);
