# Continuation Development Log

日期：2026-11-07（Asia/Shanghai）

## 恢复

- 以主仓库 `codex-A/netconsole-next-r3-dual-ac` 的真实状态恢复，保护了会话开始时的 5 个未提交文件。
- 旧 autonomous worktree `codex-A/netconsole-next-autonomous-full-feature` 停在 `816e144`；未切回旧 HEAD，未删除 worktree。
- 创建恢复报告 `CONTINUATION_RECOVERY_REPORT.md`。

## 本轮批次

### 调度并发

提交 `6809701` 合入同设备互斥等待取消修复。取消发生在获取设备 semaphore 之前时，任务保持 `CANCELLED`，不会误报 `FAILED`，也不会创建第二个设备会话。

### 启动配置正文

提交 `cf0e172`、`86d3190`、`3c0e15a` 补齐 `STARTUP_BODY` 两阶段只读采集：先采集 `display startup`，再对设备返回的经过严格校验的 `.cfg` 路径发送 `more ...`，并用回归测试断言命令顺序、快照和下载正文。正文沿用现有 claims、任务、Raw、脱敏、历史、下载和差异边界。`STARTUP_BODY` 被排除在持久化定时计划之外。恢复路由只返回 `409 CONFIG_RESTORE_UNSUPPORTED`，没有设备写命令。

### R3 identity review

提交 `2a58ae9` 增加受 Site claims 保护的 AP identity review API 和 `identityRevision`。revision 只依赖精确 Serial/MAC、来源 Observation、Raw manifest 和 source revision，不随运行态刷新变化。当前仅提供审查与阻断原因，未实现权威确认、拒绝、合并、拆分或 replacement 写入。

提交 `41bd2e2` 将 `STARTUP_BODY` 接入配置备份页面和 API client；页面隐藏该类别的定时计划入口，并新增前端回归。

## 约束

- 未访问旧 NetConsole 真实数据根。
- 未向四台模拟器写配置，设备配置写入数为 0。
- 未 push、远程 merge、release 或删除有效 worktree。
- 真实设备认证、双 AC HA、切换/恢复、实时 AP 归属和现场互操作仍为 `REAL_DEVICE_PENDING`。
