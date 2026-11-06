# NetConsole-Next R4 本地验收报告

日期：2026-11-07；Branch：`codex-A/netconsole-next-r3-dual-ac`；HEAD：`2a4d65f`。

整体结论：**PARTIAL / REAL_DEVICE_PENDING**。本轮只做本地代码、隔离 fixture 和既有回环证据；未 Push、远程合并或 Release。

| 模块 | 结果 | 证据与边界 |
| --- | --- | --- |
| A 设备管理 | PARTIAL | 既有 API/UI 回归通过；现场库存和跨型号兼容待验收 |
| B 设备连接 | PARTIAL | Netmiko/SSH/Telnet 本机与历史回环证据；本轮未重新认证设备 |
| C 多任务调度 | PASS_LOCAL | 完整 Service harness EXIT 0；并发 `6/6`；run ID scoped lookup 已合入 |
| D R3 无线业务 | PARTIAL | projection/group/runtime 回归通过；实时多 AC 业务待验收 |
| E 双 AC 1+1 | BLOCKED_EXTERNAL | AC1/AC2/SW/FIT-AP 本轮仅保留授权端点边界；无安全凭据，未做登录、切换或写配置 |
| F AP identity lifecycle | PASS_LOCAL_MUTATION | ACCEPT/REJECT/CONFLICT/SUPERSEDE、证据、Site、SUPERADMIN、revision、历史、audit；5/5 + contract 3/3 |
| G 配置备份 | PASS_LOCAL | `CONFIG_BACKUP_TESTS=11/11`、schedule `15/15` |
| H 配置恢复 | PARTIAL_SAFE_EXECUTE | preview/diff/compatibility/dry-run/token/audit；11/11；默认 writer 明确拒绝设备写入 |
| I 智能配置 | NOT_IMPLEMENTED | 无当前产品闭环 |
| J 网络诊断 | PASS_LOCAL | diagnostics `9/9`、traceroute `15/15` |
| K LLDP/拓扑/资产 | PARTIAL | LLDP/关系回归通过；完整资产工作台待补 |
| L 权限/审计/完整性 | PARTIAL | Site claims、identity/config audit 已覆盖；远程 TLS 审计待补 |
| M UI | PASS_LOCAL | Vitest 17 files/200 tests、typecheck、Vite 2089 modules PASS；真实设备态待验收 |
| N 跨机部署 | BLOCKED_EXTERNAL | loopback/offline 证据；TLS、安装、SCM、升级/回滚未验收 |

## 数据与安全边界

只使用 NetConsole-Next Workspace 隔离数据和最小 fixture；未访问、复制或修改 `D:\NetConsoleData`、`D:\NetConsoleData-dev`。设备配置写入 0。没有密码、token、cookie、私钥进入代码、日志或报告。
