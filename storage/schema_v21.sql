-- Device-bound H3C credentials. The service never returns this column through
-- an API or writes it to task/raw/audit payloads. Host keys remain separate.
INSERT OR IGNORE INTO schema_versions(version,applied_ms)
VALUES(21,CAST(strftime('%s','now') AS INTEGER)*1000);
