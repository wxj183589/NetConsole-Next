-- OFFLINE ONLY: stop the Service and all writers before executing this script.
-- This removes only the additive v26 schema when it contains no retry history.
-- Never delete retry rows to enable rollback. With history, retain the v26
-- table/version and use an older binary that ignores this additive table.
-- Execute as one SQL script and stop on any error (sqlite3 CLI: .bail on).
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;
CREATE TEMP TABLE netconsole_v26_rollback_guard(value INTEGER);
CREATE TEMP TRIGGER netconsole_v26_rollback_check
BEFORE INSERT ON netconsole_v26_rollback_guard
BEGIN
    SELECT RAISE(ROLLBACK,'V26_ROLLBACK_VERSION_MISMATCH')
    WHERE NOT EXISTS(SELECT 1 FROM schema_versions WHERE version=26)
       OR EXISTS(SELECT 1 FROM schema_versions WHERE version>26);
    SELECT RAISE(ROLLBACK,'V26_ROLLBACK_RETRY_HISTORY_EXISTS')
    WHERE EXISTS(SELECT 1 FROM task_plan_retries);
END;
INSERT INTO netconsole_v26_rollback_guard VALUES(1);
DROP TABLE task_plan_retries;
DELETE FROM schema_versions WHERE version=26;
DROP TABLE netconsole_v26_rollback_guard;
COMMIT;
