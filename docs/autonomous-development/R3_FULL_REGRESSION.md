# R3.1 全量回归报告

更新时间：2026-11-14（Asia/Shanghai）

## 结果

自动化回归：`PASS`。

产品运行与真实设备验收：`PARTIAL / NOT_VERIFIED`。本次未连接真实 AC/AP、真实双 AC HA/failover、真实 Syslog 接收端、Windows SCM 或 GUI 安装环境，因此不将自动化通过扩展为现场验收通过。

## 前端

- `pnpm test`：`PASS`，36 个测试文件、451 个用例。
- `pnpm typecheck`：`PASS`。
- `pnpm build`：`PASS`，Vite 生产构建和 terminal bundle 构建完成。

构建产生的静态资源保留在仓库已有的 `wwwroot/app/assets` 工作区状态中；本批未将这些既有/生成资源加入提交，也未清理它们。

## 服务端

- Release build：`PASS`，0 errors、0 warnings。
- 默认 `SchedulerTests` 全量回归：`PASS`，覆盖 WirelessTask、Syslog、完整配置批处理/比较、FaultCenter、OperationalHealth、ICMP/Long Ping/Quick Ping、H3C checkpoint/restore、Netmiko worker/retry、供应商 parser replay、ZTE/Linux、DPAPI/Credential、迁移边界、Site Scope、无线 projection/runtime、设备连接、事实 scope、任务证据、配置备份/恢复、网络诊断/traceroute、并发、SQLite、归档和性能路径。
- 关键计数：`FAULT_CENTER_TESTS=30/30`、`OPERATIONAL_HEALTH_TESTS=27/27`、`CREDENTIAL_TESTS=5/5`、`DPAPI_AUTHORITY_TESTS=PASS`、`WIRELESS_RUNTIME_TESTS=14/14`、`TASK_DIAGNOSTIC_EVIDENCE_TESTS=96/96`、`H3C_DIAGNOSTIC_CAPTURE_TESTS=30/30`、`CONFIG_BACKUP_SCHEDULE_TESTS=19/19`、`NETWORK_TRACEROUTE_TESTS=15/15`。
- 回归中的 `P2B2 AFTER_COMMIT` 子进程故障注入按预期触发后被父测试验证，整体进程正常退出，不是产品失败。

## 数据影响

未修改 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`，未复制完整真实数据根，未执行生产迁移、删除、VACUUM、WAL/SHM 删除或真实设备破坏性测试。测试使用项目 Workspace/系统临时隔离数据。

## 未验证项

- 真实 H3C 凭据逐设备重录和旧 Catalog 明文迁移；
- 双 AC 控制器 failover/recovery、真实 MESH 链路切换和 8–24 小时历史；
- ZTE、Linux、Jump Host 的独立凭据 authority 合并；
- Windows SCM、GUI 安装、原生 125% 和现场网络验收；
- GitHub 远端刷新、R3 Push、远端 main 合并和发布。

