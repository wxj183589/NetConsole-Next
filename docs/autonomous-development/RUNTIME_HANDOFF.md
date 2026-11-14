# R3.2 运行交接

更新时间：2026-11-15（Asia/Shanghai）

## 当前基线

```text
PROJECT=NetConsole-Next
PHASE=R3.2-Windows-Runtime-H3C-Interop-Wireless-Fault-E2E
BRANCH=codex-A/netconsole-next-r3-dual-ac
PRODUCT_COMMIT=58febd2
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

## R3.2 本轮交接

- A：服务和 LocalManager Release build 通过；LocalManager/安装计划测试通过；真实 SCM lifecycle 因非管理员权限保持 `BLOCKED_ADMIN_PRIVILEGE`。
- B：AC1 `192.168.56.2:22` 和 AC2 `192.168.56.5:22` 只读认证、Comware 识别和命令采集通过；SW 使用用户提供的用户名密码直接 SSH 登录并完成只读命令；已确认 AC1 为 FIT-AP `4c48-cb6e-0400` 的当前主用 AC，在隔离 AC1 执行远程控制台命令后，FIT-AP 以空用户名、Password-only 模式 Telnet 登录并完成 `display version`。AC1 复查字段仍为 `Remote configuration : Disabled`，因此只记录动作已执行和实际登录成功，不声称状态字段 Enabled。探测结果详见 `R3_2_B_H3C_INTEROPERABILITY.md`。
- C：精确 AP 资源键和 Mesh link-up 恢复逻辑已提交；服务端全量回归、前端 451 项测试、Syslog/OperationalHealth/FaultCenter 定向测试通过。当前代码 HTTP E2E 仍被已有 R7.8 Service 的全局 mutex 阻断，未停止或复用该实例。
- 隔离证据：`reports/r3.2-b-h3c-20261114-235450`、`reports/r3.2-c-e2e-20261115-000539`、`reports/r3.2-direct-connection-20261115-004737`、`reports/r3.2-ac-ap-ownership-20261115-004807`、`reports/r3.2-ac-console-status-20261115-004850`、`reports/r3.2-ac-enable-console-20261115-0054`、`reports/r3.2-fit-ap-post-console-20261115-0058`。
- 数据安全：生产/真实开发数据未改；唯一设备配置写入为已授权隔离 AC1 的 `exec-console enable` 尝试；R3 Push、main merge、release 均为 NO。测试密码仅运行时使用，未写入代码、报告或日志。

## 下一步

1. 先确认现有 `NetConsole.Next.Service` 实例的归属并结束维护窗口，再执行真实 SCM lifecycle；不得强制停止其他 Agent 或用户进程。
2. 在明确授权和隔离窗口内，补齐产品端 FIT-AP 主用 AC 检查、远程控制台状态和一次性确认动作；按 AP 精确执行，不静默扩大为 `all`。
3. 关闭单实例阻断后，用当前产品 Service 重新验收 sealed Raw/Facts、双 AC、MESH 链路变化、故障生成与恢复；独立 worker 登录结果不能替代产品持久化证据。
4. 若要生产化 AP lifecycle authority，先补正式 schema、跨进程并发、merge/split、replacement revoke、迁移、备份和回滚门禁。

## FIT-AP 远程连接专项交接

- 已实现无线页“远程连接”和“连接设置”：默认 Telnet 23、Password-only、空用户名；密码可由运行环境默认值提供，也可按全局/AC/AP 范围覆盖，API 只返回是否已配置。
- 已实现新鲜唯一主用 AC 解析、同一 AC 观察中的 AP 管理地址匹配、控制台状态识别、`all` 范围开启动作的显式确认和直接临时 FIT-AP 终端会话。
- 连接会先采集 `display version`，将结果写入 FIT-AP 专用 sealed Raw，再登记 `fit_ap_facts`；事实查询 API 和无线设置弹窗的最近版本反馈已接通。
- 服务端和前端回归均通过；当前产品 HTTP 真实设备录制、浏览器 E2E 和现有互斥 Service 释放后的重新验收仍为 `NOT_VERIFIED`。不要把历史独立 worker 登录证据写成当前产品 Raw/Facts PASS。
- 默认密码环境变量的具体值不进入代码、配置文件、日志、报告或提交；FIT-AP 仍不创建普通设备库存条目。

