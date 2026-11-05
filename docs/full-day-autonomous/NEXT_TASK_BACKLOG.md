# 全天候动态任务池

| ID | 工作流 | 任务 | 优先级 | 依赖 | 状态 | 验收 |
|---|---|---|---|---|---|---|
| FD-P0-01 | Security | 凭据、密码、HostKey、Site 权限无泄漏 | P0 | 现有 DPAPI/claims | COMPLETED_BASELINE | 定向安全回归 |
| FD-P1-01 | Scheduler | 多设备批量子任务、并发上限、同设备互斥 | P1 | 现有 BoundedCollectionScheduler | COMPLETED_LOCAL | `CONCURRENCY_P1_TESTS=5/5 PASS` |
| FD-P1-02 | Scheduler | 单任务多命令顺序、命令级状态、独立 Raw | P1 | Raw manifest 合同 | COMPLETED_LOCAL | 11/11 H3C fact tests |
| FD-P1-03 | Connection | SSH 首次自动信任、指纹轮换、并发原子更新 | P1 | DPAPI、host-key history | COMPLETED_LOCAL | 服务/隔离 transport |
| FD-P1-04 | Connection | 一键测试连接和友好错误映射 | P1 | profile/credential/session | COMPLETED_LOCAL | API + device list action |
| FD-P1-05 | Wireless | 双 AC/AP identity、Observation、group 和 runtime | P1 | 已完成 R3 projection | PARTIAL_EXTERNAL | fixture + four simulator evidence |
| FD-P1-06 | Facts | 多命令 collection manifest 与 Raw→Facts→Current | P1 | FD-P1-02 | COMPLETED_LOCAL | coherent isolated fixture |
| FD-P1-07 | Task UI | 任务中心按业务语言展示进度、设备明细、重试/取消 | P1 | FD-P1-01/02 | COMPLETED_LOCAL | command detail API/UI typecheck |
| FD-P1-08 | Device UI | 新增设备默认协议/端口、当前 Site、测试连接 | P1 | FD-P1-04 | COMPLETED_LOCAL | device list quick test + typecheck |
| FD-P2-01 | Diagnostics | Ping/TCP/DNS/LLDP 诊断批量任务、进度和重启历史 | P2 | scheduler/catalog | COMPLETED_LOCAL | `NETWORK_DIAGNOSTICS_TESTS=4/4 PASS` |
| FD-P2-02 | Backup | 配置备份、历史版本、差异和下载授权 | P2 | Raw/archive/claims | COMPLETED_LOCAL | CONFIG_BACKUP_TESTS=1/1 PASS |
| FD-P2-03 | Topology | LLDP 关系与变化历史 | P2 | verified facts | COMPLETED_LOCAL | H3C_LLDP_TOPOLOGY_TESTS=2/2 PASS |
| FD-P3-01 | UI | 1920/2560 任务/设备/无线交互验收 | P3 | UI workstream | COMPLETED_LOCAL | Chrome `1920/2560` fixture |
| FD-P3-02 | Integration | module registry/navigation and error states | P3 | frontend build | COMPLETED_LOCAL | typecheck/browser |
| FD-P2-04 | Integration/Security | 服务端模块状态持久化、SUPERADMIN 启停 API、核心模块保护 | P2 | Catalog schema v17、WebAuth | COMPLETED_LOCAL | module registry API/restart e2e |
| FD-P1-09 | Wireless/Facts | R3 AP identity、AC Domain、跨 AC Observation、AP Group 持久化 | P1 | Wireless projection、schema v18 | COMPLETED_LOCAL | dual-AC isolated persistence/restart |
| FD-P1-10 | Wireless/Facts | 跨 AC collection manifest 与 observation provenance 绑定 | P1 | FD-P1-09、Raw manifest | COMPLETED_LOCAL | schema v19 manifest/restart e2e |
| FD-P3-03 | Docs | 全天候 16 项交付报告、矩阵、回执、handoff | P3 | all workstreams | IN_PROGRESS | 本轮交接继续更新 |
| FD-P1-11 | Wireless/Facts | AP Group 详情、成员与 provenance 只读 API/UI | P1 | FD-P1-09、Site claims | COMPLETED_LOCAL | group scope/detail API、浏览器点击验收 |
| FD-P1-12 | Wireless/Facts | AC Domain AP association 与 observation provenance 只读 API | P1 | FD-P1-09、Site claims | COMPLETED_LOCAL | API-W04 fixture/filter 验收 |
| FD-P1-13 | Wireless/Facts | 跨 AC collection manifest 查询、详情与采集记录 UI | P1 | FD-P1-10、Site claims | COMPLETED_LOCAL | manifest API 过滤/详情、Catalog scope 回归、浏览器点击验收 |
| FD-P1-14 | Connection/Device UI | 快速添加设备：默认协议端口、自动 HostKey、Credential Reference 与一键测试 | P1 | DPAPI、现有 H3C Profile API | COMPLETED_LOCAL | onboarding API、vue-tsc、Vitest、Release build |
| FD-P1-15 | Task Center | 历史任务超过最近 100 条仍可按 ID 重试/恢复 | P1 | task_runs persistence | COMPLETED_LOCAL | `CONCURRENCY_P1_TESTS=5/5 PASS` |
