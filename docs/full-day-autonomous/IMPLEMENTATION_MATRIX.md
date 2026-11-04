# 实现矩阵

| Capability | Existing evidence | Current stage | Strong evidence required |
|---|---|---|---|
| Multi-device parallel | bounded scheduler fixture/concurrency tests | Agent audit | batch child task and per-device isolation |
| Multi-command task | one command per persisted run | queued | ordered command state and independent Raw |
| Retry/cancel/recovery | bounded cancellation and startup recovery | partial | isolated per-child retry and restart |
| SSH auto-trust | host-key check/commit/history fixture | agent audit | AUTO_TRUST/AUTO_REPLACE policy and concurrent updates |
| Connection simplification | profile APIs and terminal session | queued | UI defaults and friendly outcomes |
| AP identity / dual AC | offline projection and runtime fixtures | partial | simulator login and HA/failover evidence |
| Raw→Facts→Current | single-command publication | partial | collection-level coherence contract |
| Site authorization | current scope guards across task/files/terminal/wireless | baseline pass | task center and diagnostics every read/write |
| Device management | P2A batch and CSV contracts | baseline pass | current UI integration and connection test |
| Diagnostics/backup/topology | partial host tools/LLDP parser/archive | queued | business task APIs with real provenance |
| UI/UX | wireless/files browser fixture | queued | device/task flows against backend |
