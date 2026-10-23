# Host tool and file design (Web PR-2)

Status: `NOT_STARTED`; this document is an interface contract, not an implementation claim.

External SecureCRT/Xshell/PuTTY and Explorer operations run on the NetConsole Next Windows host. The Service never tries to display GUI from Session 0. A separately installed Desktop Helper, started inside the logged-in Windows desktop session, accepts only signed/authorized operation names over constrained local IPC.

Required operation envelope:

```json
{ "operation": "OPEN_EXTERNAL_TERMINAL", "toolId": "securecrt", "deviceId": "...", "args": ["..."] }
```

The Service validates `SUPERADMIN`, tool registration, device authorization, and an allow-listed argument schema before IPC. No arbitrary command line, shell string, or browser Local Helper is permitted. If no interactive desktop is available, the result is `HOST_DESKTOP_UNAVAILABLE`.

File management will expose logical roots only: data, raw, syslog, terminal recordings, analysis, exports and service logs. The implementation must reject traversal, symlink/reparse-point escapes, credentials/token files, and direct SQLite downloads. Single files stream directly; selections become cancellable background ZIP jobs with queryable state and temp cleanup. Deletion/move is out of scope for PR-2 initial delivery.

