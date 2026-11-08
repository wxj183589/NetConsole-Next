# R7.4 Windows Deployment Report

LocalManager now waits up to 30 seconds after SCM reports the service started, polling only the configured loopback `/api/system/health` endpoint. It requires `serviceState=running`, `healthState=HEALTHY`, and a positive process ID; an oversized response or sustained degraded state fails with a Chinese `SERVICE_NOT_READY` diagnostic.

Validation: readiness 3/3, portable runtime 9/9 and deployment doctor 13/13 passed in the LocalManager harness. These are unit/fixture checks. The host is not elevated, so real SCM installation, restart, crash recovery, upgrade/rollback, and uninstall retention remain **BLOCKED / NOT_TESTED**. No system security settings were changed.

Overall: **PARTIAL**.
