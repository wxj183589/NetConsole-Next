# 全天候动态任务池

| ID | 工作流 | 任务 | 优先级 | 依赖 | 状态 | 验收 |
|---|---|---|---|---|---|---|
| FD-P0-01 | Security | 凭据、密码、HostKey、Site 权限无泄漏 | P0 | 现有 DPAPI/claims | COMPLETED_BASELINE | 定向安全回归 |
| FD-P1-01 | Scheduler | 多设备批量子任务、并发上限、同设备互斥 | P1 | 现有 BoundedCollectionScheduler | IN_PROGRESS | 隔离 SQLite/调度 fixture |
| FD-P1-02 | Scheduler | 单任务多命令顺序、命令级状态、独立 Raw | P1 | Raw manifest 合同 | QUEUED | 原子失败/取消/重试测试 |
| FD-P1-03 | Connection | SSH 首次自动信任、指纹轮换、并发原子更新 | P1 | DPAPI、host-key history | IN_PROGRESS | Fake transport + history |
| FD-P1-04 | Connection | 一键测试连接和友好错误映射 | P1 | profile/credential/session | QUEUED | isolated service/API |
| FD-P1-05 | Wireless | 双 AC/AP identity、Observation、group 和 runtime | P1 | 已完成 R3 projection | PARTIAL_EXTERNAL | fixture + four simulator evidence |
| FD-P1-06 | Facts | 多命令 collection manifest 与 Raw→Facts→Current | P1 | FD-P1-02 | IN_PROGRESS | coherent collection fixture |
| FD-P1-07 | Task UI | 任务中心按业务语言展示进度、设备明细、重试/取消 | P1 | FD-P1-01/02 | IN_PROGRESS | browser fixture/backend API |
| FD-P1-08 | Device UI | 新增设备默认协议/端口、当前 Site、测试连接 | P1 | FD-P1-04 | IN_PROGRESS | typecheck/browser |
| FD-P2-01 | Diagnostics | Ping/TCP/LLDP 诊断批量任务和进度 | P2 | scheduler | QUEUED | whitelist fixture |
| FD-P2-02 | Backup | 配置备份、历史版本、差异和下载授权 | P2 | Raw/archive/claims | QUEUED | isolated archive |
| FD-P2-03 | Topology | LLDP 关系与变化历史 | P2 | verified facts | QUEUED | parser fixture |
| FD-P3-01 | UI | 1920/2560 任务/设备/无线交互验收 | P3 | UI workstream | QUEUED | Chrome screenshots |
| FD-P3-02 | Integration | module registry/navigation and error states | P3 | frontend build | QUEUED | typecheck/browser |
| FD-P3-03 | Docs | 全天候 16 项交付报告、矩阵、回执、handoff | P3 | all workstreams | IN_PROGRESS | files exist and current |
