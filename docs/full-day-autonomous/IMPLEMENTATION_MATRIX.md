# 实现矩阵

| Capability | Existing evidence | Current stage | Strong evidence required |
|---|---|---|---|
| Multi-device parallel | bounded scheduler fixture/concurrency tests | PASS (fixture) | batch child task and per-device isolation |
| Multi-command task | ordered plan, command-level Raw table, complete-manifest success guard, command detail API/UI | PASS (isolated fixture) | four-simulator coherent collection evidence |
| Retry/cancel/recovery | bounded cancellation and startup recovery; task-center cancel/retry/resume routes and UI | PASS (isolated fixture) | real device restart/recovery evidence |
| SSH auto-trust | host-key check/commit/history fixture, automatic first trust/rotation and AC1/AC2 live Host Key AUTO_UPDATE | PASS (local + live AC1/AC2) | changed-key live rotation on simulator |
| Connection simplification | profile APIs, profile-scoped credential resolution, one-click test API, device list action and bounded result mapping | PARTIAL (AC1/AC2 live; SW Exec/FIT-AP credential gaps) | credential-backed four-device outcome |
| Netmiko worker / driver mapping | CPython JSON-lines worker, explicit installed-driver allowlist, paging preparation, bounded output and no secret echo | PASS (offline 2/2 tests) | real H3C/ZTE/Cisco/Huawei interoperability and packaging gate |
| Site Jump Host probe / refresh | Site-scoped persistence, redacted API, one-shot worker probe, fail-closed fingerprint comparison and explicit refresh route | PASS (isolated service/API) | live bastion authentication and changed-key evidence |
| Historical task lookup by ID | direct ID query used by task detail/retry/resume, no recent-100 window dependency | PASS (isolated regression) | long-running production history and restart evidence |
| AP identity / dual AC | exact Serial+MAC projection, unique MAC partial-evidence association, conflict-preserving observations and runtime fixtures | PARTIAL | formal replacement/candidate review plus simulator login and HA/failover evidence |
| Raw→Facts→Current | single and multi-command publication with collection provenance; incomplete success manifest rejected | PASS (isolated fixture) | four-simulator live collection |
| Site authorization | current scope guards across task/files/terminal/wireless | baseline pass | task center and diagnostics every read/write |
| Device management | P2A batch and CSV contracts | baseline pass | current UI integration and connection test |
| Diagnostics/backup/topology | Ping/TCP/DNS diagnostics with restart-durable history, versioned redacted config backup/diff/download, LLDP relation/history/claims API and UI | PASS (isolated fixture/API/UI) | four-simulator credential-backed evidence |
| UI/UX | device test-connection action, task command detail and task actions | PASS (1920/2560 offline browser + 1920 service-backed smoke) | service-backed mutation/browser gate |
| Module governance | schema v17 persisted module registry, SUPERADMIN enable/disable API, core-module protection, restart persistence, shell status overlay and API capability gates | PASS (isolated service/API/UI) | settings management UI |
| R3 wireless persistence | schema v18 AC domains, AP resources, per-AC observations and AC-owned groups; full-scope transactional upsert and stable IDs | PASS (isolated Catalog/restart) | live AC/AP collection and HA evidence |
| Cross-AC collection manifest | schema v19 immutable source revision manifest and per-observation source AC/domain/Site/raw manifest references; idempotent upsert | PASS (isolated Catalog/restart) | live multi-AC collection |
| Collection manifest read API/UI | Domain-scoped manifest list/detail, source AC filter and current-claims aggregation | PASS (isolated API/Catalog/UI) | live multi-AC collection |
