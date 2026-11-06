# R4 Remaining Gaps

当前 HEAD：`2a4d65f`；整体状态仍 `PARTIAL / REAL_DEVICE_PENDING`。

本轮已关闭的本地缺口：

- identity lifecycle 已具备隔离 JSON authority、Site/SUPERADMIN/evidence gate、乐观 revision、唯一性检查、有限历史和审计；`WIRELESS_IDENTITY_LIFECYCLE_TESTS=5/5`、contract `3/3`。
- configuration restore 已具备 preview、差异、兼容检查、dry-run、五分钟确认 token 和 audit；`CONFIG_RESTORE_TESTS=11/11`。默认 writer 仍返回 `CONFIG_RESTORE_WRITE_ADAPTER_UNAVAILABLE`，不会写设备。
- scheduler 已具备按 run ID 的 Site-scoped 查询和 URL 编码回归；完整 Service harness 与并发 `6/6` 通过。

## P0/P1 本地可继续

1. AP identity production authority：schema-backed candidate/review、merge/split、replacement revoke 和跨进程并发；当前 JSON authority 只在隔离临时根启用。
2. Configuration restore：真实 writer、失败回滚和模拟器/现场安全执行；当前默认 writer 永久拒绝设备写入。
3. Task/connection integration：任务 ID 查询已补齐；继续验证权限撤销、重启恢复、单设备重试和审计。
4. 工作台和资产视图：将真实任务、AP、设备、LLDP、失败项汇总到现有模块 registry/UI。

## 外部阻断

- 四台白名单模拟器没有可解析的安全凭据时，认证和配置写入保持 `BLOCKED_CREDENTIALS`。
- 双 AC HA、主备切换、AP ownership、恢复状态不能由配置或 UI 推断。
- 真实 H3C/ZTE/Linux/bastion 互操作、目标机安装、跨机 TLS 和 Windows 原生 125% 仍未验收。

## 数据边界

本轮只使用 NetConsole-Next Workspace 隔离数据和现有测试 fixture；不复制、不修改 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`，不把密码、Cookie、Token、私钥或真实数据库加入 Git。
