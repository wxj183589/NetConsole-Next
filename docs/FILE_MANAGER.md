# File manager

files exposes only these logical roots: raw, terminal-recordings, inbox, archive, backup, published, analysis, exports and logs. The browser receives root IDs and relative paths, never the absolute data root.

Every request rejects absolute paths, drive-qualified paths, dot-dot segments, control characters and reparse-point traversal. SQLite catalog files, local API tokens, credentials, password material, .open files and recovery markers are blocked. The implementation does not expose delete, move or arbitrary host-shell operations.

Single files are streamed with range support. A file/folder/mixed selection is expanded server-side into an owner-bound background ZIP job. Job state survives a browser refresh while the service process remains alive; it includes file/byte progress, cancellation and periodic cleanup.

## Archive resource budget

The default service budget is deliberately finite:

- one running job and eight queued jobs;
- two active jobs per Web user/session identity;
- 10,000 file entries and 20 GiB of input per job;
- 5,000 scanned directories, depth 32 and 25,000 metadata entries per scan;
- 64 MiB metadata budget per scan and 32 GiB aggregate reserved input bytes;
- 128 retained terminal job records, with 24-hour completed and one-hour failed/cancelled retention.

These limits can be lowered or raised within bounded environment-variable ranges. Admission reserves a job slot before directory expansion and reserves input bytes before enqueue. A bounded channel rejects excess requests with `429` and stable codes such as `ARCHIVE_QUEUE_FULL`, `ARCHIVE_USER_LIMIT` and `ARCHIVE_RESOURCE_LIMIT`; rejected requests do not create a job or temporary file.

Before directory expansion, admission checks the available space on the volume containing the service `archive-tmp` directory. The check uses `ArchiveMinFreeDiskBytes`; space below the threshold returns HTTP `503` with `ARCHIVE_DISK_SPACE_LOW`, without scanning, creating a job, creating a temporary file or retaining a reservation. If the volume cannot be identified or its free space cannot be read, admission fails closed with HTTP `503` and `ARCHIVE_STORAGE_UNAVAILABLE`. Space exactly at the threshold is accepted. This is a TOCTOU gate, not a guarantee: the worker repeats the check before writing and during ZIP output, and still enforces the per-job temporary-file limit.

Directory expansion is iterative and cancellable. It counts files, directories and metadata as it walks, rejects reparse points, and fails with an explicit scan/input-change error if a selected path changes. The service never holds the admission lock during disk traversal.

## ZIP lifecycle and cancellation

Jobs use `QUEUED -> RUNNING -> COMPLETED|FAILED|CANCELLED`. A task cancellation token is linked with the service stop token and is passed into every file read/write. A cancelled job cannot become `COMPLETED`; its `.zip.part` file is removed and its active/input reservation is released. A completed ZIP is renamed from `.zip.part` only after the archive is closed. Downloads use a lease so maintenance cannot remove an actively streamed ZIP.

An independent five-minute maintenance timer removes expired terminal jobs and orphan temporary files even when the queue is idle. Startup repeats orphan cleanup, and service shutdown cancels outstanding work. Cleanup only touches files owned by the archive job directory; Raw, Syslog, SQLite, terminal recordings and published reports are outside its scope. ZIP64 is provided by .NET `ZipArchive` when required; a real multi-gigabyte ZIP64 run remains unexecuted in CI.

Disk-failure tests use an injected `IArchiveDiskSpaceProvider`, so CI can deterministically cover below-threshold, exact-threshold, recovered-space, unavailable-volume and runtime-space-drop paths without filling a runner disk. The runtime failure path removes `.zip.part`, does not publish an incomplete ZIP, and releases the user and aggregate reservations.
