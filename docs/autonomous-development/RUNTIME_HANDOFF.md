# R3.1 运行交接

更新时间：2026-11-14（Asia/Shanghai）

## 当前基线

```text
PROJECT=NetConsole-Next
PHASE=R3.1-Credential-Authority-Wireless-Integration
BRANCH=codex-A/netconsole-next-r3-dual-ac
PRODUCT_COMMIT=e39c0f94cf2dd3162246e8bd6ba2ac2464c4122d
R3_PUSH=NO
REMOTE_MAIN_MERGE=NO
RELEASE=NO
```

本地产品提交包含：

- R3 H3C profile 凭据 authority：生产服务默认使用设备绑定 Windows DPAPI；采集、终端和完整配置读取共用同一 authority；旧 Catalog 明文只触发迁移门禁，不自动批量迁移。
- 无线 AP 精确身份、双 AC observation/runtime 历史和隔离写入合同。
- sealed Syslog MESH 事件到 OperationalHealth/FaultCenter 的证据投影。
- 对应定向测试和业务报告。

## 已完成验证

- 前端完整测试：36 files / 451 tests PASS。
- 前端类型检查和生产构建 PASS。
- 服务端 Release build 和默认全量回归 PASS。
- Syslog、OperationalHealth、FaultCenter、无线运行态、生命周期、凭据 authority 定向测试 PASS。

## 运行边界

- 真实开发数据 authority 仍为 `D:\NetConsoleData-dev`；本次未读写生产数据或真实开发数据。
- R3 分支禁止 Push、禁止合并远端 main、禁止发布；当前只保留本地提交。
- 现场验收仍需真实 AC/AP、双 AC HA/failover、真实 Syslog、SCM/GUI 和长时间运行证据。
- `github/main` 本轮没有刷新成功，不能据此宣称远端主线最新；Draft PR #49 仍作为独立历史参考，不整支移植。

## 下一步

1. 在明确授权和隔离窗口内，逐设备显式重录 H3C 凭据并验证旧字段清除；不得批量复制或覆盖真实数据根。
2. 用真实双 AC 和 sealed Raw 证据验收主备切换、MESH 链路变化、故障生成与恢复；若证据缺失，保留 `NOT_VERIFIED`。
3. 若要生产化 AP lifecycle authority，先补正式 schema、跨进程并发、merge/split、replacement revoke、迁移、备份和回滚门禁。

