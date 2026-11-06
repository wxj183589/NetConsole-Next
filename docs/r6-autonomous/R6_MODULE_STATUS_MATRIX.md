# R6 模块状态矩阵

| 模块 | 状态 | 证据与剩余问题 |
| --- | --- | --- |
| 工作台 | PARTIAL | 真实健康、设备、任务、AC/AP API；告警与趋势源不完整 |
| 设备管理与连接 | PARTIAL | 本机 Netmiko 闭环；目标设备互操作待验 |
| Linux SSH、跳板、Console | PARTIAL | 本地/回环证据；真实目标待验 |
| 无线 AC/AP 与双 AC | PARTIAL | 本地身份/投影；HA 和 live ownership 待验 |
| 配置备份/差异/恢复 | PARTIAL | 读取与预览可用；恢复 writer fail-closed |
| 智能配置 | NOT_IMPLEMENTED | 无完整用户闭环 |
| 诊断、LLDP、拓扑、资产 | PARTIAL | 基础 API/任务存在；完整工作台缺失 |
| 多设备任务与任务中心 | PASS_LOCAL | 本地测试和既有 Service 验收通过 |
| 权限、审计、Site 隔离 | PASS_LOCAL | 既有本地契约/测试；现场部署验收待做 |
| 导入导出与报表 | PARTIAL | 部分下载/导出可用；全功能 parity 未完成 |
| 安装、TLS、升级回滚 | BLOCKED | 跨机器和目标机验收未完成 |
