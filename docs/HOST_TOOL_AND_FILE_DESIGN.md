# Host tool and file design (Web PR-2)

Status: `IMPLEMENTED/PARTIALLY_VERIFIED`; PR-2 source and isolated API tests are present. Real Windows GUI acceptance remains pending.

External SecureCRT/Xshell/PuTTY and Explorer operations run on the NetConsole Next Windows host. The Service never tries to display GUI from Session 0. A separately installed Desktop Helper, started inside the logged-in Windows desktop session, accepts only signed/authorized operation names over constrained local IPC.

Required operation envelope (the Service wraps the operation in a request envelope before sending it to the local pipe):

```json
{ "operation": "OPEN_EXTERNAL_TERMINAL", "toolId": "securecrt", "deviceId": "...", "args": ["..."] }
```

The Service validates `SUPERADMIN`, tool registration, device authorization, and an allow-listed argument schema before IPC. No arbitrary command line, shell string, or browser Local Helper is permitted. If no interactive desktop is available, the result is `HOST_DESKTOP_UNAVAILABLE`.

The implementation is in HostToolRegistry, HostToolLaunchService, DesktopHelperIpc, FileManagerService and FileArchiveService. The service never calls Process.Start for a GUI program. Desktop Helper validates the operation, executable, arguments and directory again and uses ArgumentList without a shell.

File management exposes logical roots only: raw, terminal recordings, inbox, archive, backup, published, analysis, exports and service logs. Traversal, reparse/symlink escapes, credential/token files, .open files and direct SQLite downloads are rejected or omitted. Single files stream directly; files/folders/mixed selections become cancellable background ZIP jobs with owner-bound status, progress and temp cleanup. .NET ZipArchive provides ZIP64 when required. Deletion/move is out of scope.

The helper is a host-side Windows interactive-session executable, not a browser extension or a LAN endpoint. A service running in Session 0 cannot prove that a user desktop is available; HOST_DESKTOP_UNAVAILABLE is an expected result until the helper is installed and running in that session.

## Archive reliability budget

ZIP work is intentionally a small, bounded background subsystem: one worker, eight queued jobs, two active jobs per Web user, 10,000 files/20 GiB input per job, depth 32, 5,000 directories, 25,000 metadata entries and 32 GiB aggregate reserved input by default. The admission reservation is acquired before directory expansion; queue, per-user, input and disk-budget failures return stable `429`/`503` codes instead of HTTP 500.

The scanner is iterative and cancellable. Running copies use a linked per-job/service-stop token, publish only after closing and renaming a `.zip.part`, and cannot overwrite a `CANCELLED` state with `COMPLETED`. A periodic maintenance loop runs every five minutes, enforces terminal-job retention, protects active download leases, and removes only archive-owned temporary files. Startup and shutdown paths also handle orphan/incomplete ZIP resources. Resource-pressure and cancellation evidence is isolated to CI test data roots; no user Windows data root is used.

