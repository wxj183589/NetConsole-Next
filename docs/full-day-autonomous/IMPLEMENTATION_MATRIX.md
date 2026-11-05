# 实现矩阵

| Capability | Existing evidence | Current stage | Strong evidence required |
|---|---|---|---|
| Multi-device parallel | bounded scheduler fixture/concurrency tests | PASS (fixture) | batch child task and per-device isolation |
| Multi-command task | ordered plan, command-level Raw table, command detail API/UI | PASS (isolated fixture) | four-simulator coherent collection evidence |
| Retry/cancel/recovery | bounded cancellation and startup recovery | partial | isolated per-child retry and restart |
| SSH auto-trust | host-key check/commit/history fixture | agent audit | AUTO_TRUST/AUTO_REPLACE policy and concurrent updates |
| Connection simplification | profile APIs, one-click test API and device list action | PASS (local/API contract) | credential-backed simulator outcome |
| AP identity / dual AC | offline projection and runtime fixtures | partial | simulator login and HA/failover evidence |
| Raw→Facts→Current | single and multi-command publication with collection provenance | PASS (isolated fixture) | four-simulator live collection |
| Site authorization | current scope guards across task/files/terminal/wireless | baseline pass | task center and diagnostics every read/write |
| Device management | P2A batch and CSV contracts | baseline pass | current UI integration and connection test |
| Diagnostics/backup/topology | Ping/TCP/DNS diagnostics with restart-durable history, versioned redacted config backup/diff/download, LLDP relation/history/claims API and UI | PASS (isolated fixture/API/UI) | four-simulator credential-backed evidence |
| UI/UX | device test-connection action and task command detail | PASS (typecheck/Vitest) | service-backed browser gate |
