# R7.0 双 AC HA 与无线能力证据

- 日期：2026-11-07；Agent：codex-A。
- 分支：`codex-A/netconsole-next-r3-dual-ac`；本工作流由集成负责人统一提交。
- 状态：**PARTIAL**。双 AC 读取、AP 主备观察和能力展示已完成；实际 HA 切换与业务恢复 **NOT_TESTED**。

## 实际产品成果

新增 `src/NetConsole.Next.Service/H3cOperationalCapabilities.cs`：

- 根据已授权、已限定 Site 的 Current 事实投影 H3C 能力，保留 evidence ID、来源、捕获时间和时效。
- 区分近期 Raw 已验证、过期证据、fixture、未支持、不完整及未知；不能因设备型号就声明命令支持。
- 无线能力读模型保留每条 AC Observation 的 R/M、R/B 原文、来源 AC、Raw 引用及 revision。
- 用户确认拓扑、设备报告的 AP 角色与 HA 实际切换验证分别输出。没有切换证据时 `HaVerification=NOT_TESTED`。
- 不把接口/AP 读取成功推导为整机恢复或 HA 写操作能力。

API 与无线页面由集成工作流接入；最终 HTTP/浏览器验证以 `R7_FINAL_ACCEPTANCE.md` 为准。

## 新的模拟器实证

探查脚本：`tests/r70_device_capability_probe.py`。仅连接授权的 AC1 `192.168.56.2:22`、AC2 `192.168.56.5:22`、SW `192.168.56.3:22`，复用现有 Netmiko worker、已存凭据与主机密钥固定值。

有效证据目录：

`D:\study\NetConsole-Next-Workspace\reports\r70-device-20261107-233336`

| 检查 | AC1 | AC2 | 含义 |
| --- | --- | --- | --- |
| SSH 认证及主机密钥检查 | PASS | PASS | 真实会话，非 TCP 探测 |
| 型号/版本 | WX5540H-HCL / 7.1.064 Alpha 7165 | 同左 | 模拟器版本 |
| `display wlan ac-role` | Common | Common | 控制器未报告 Master/Backup |
| `display wlan ap all` | 同一 AP R/M | 同一 AP R/B | AP 的独立主备 Observation |
| IRF | 本机单成员 Master | 本机单成员 Master | 不代表两 AC 组成一个 IRF |
| `display ha service-group all` | 本机 1/0 / Realtime Backup | 本机 1/0 / Realtime Backup | 本地服务组状态，不能证明跨 AC HA |

AP：`4c48-cb6e-0400`，型号 `WA6320-HCL`，序列证据 `H3C_4C-48-CB-6E-04-00`。无线资源仍须通过既有身份合同生成一个 Resource、多条来源 Observation，不能用显示名合并跨站设备。

本轮修正了探查方式：R6.9 的部分后续帮助命令使用 Ctrl-C 清行，实际输出存在 `display ha display mad display wlan` 拼接；本轮用 Ctrl-U 在每次 `?` 前后明确清除编辑行。因此不能将旧的拼接命令错误当作 WLAN 能力缺失。

有效帮助树列出 `display wlan ac-role`、`display wlan client-backup`，system-view 列出 `wlan ap-backup hot-backup`、`wlan client-backup hot-backup`、`wlan master-ac enable/port`。这些是能力广告，不是已验证的强制切换命令。没有执行这些配置命令，也没有通过重启、断链或关闭接口模拟切换。

配置只在内存中检查有限冗余指令数量；未保存或提交完整配置。部分全局指令为零不等于已排除全部 AP 级冗余配置。

## 配置恢复的关联证据

在集成负责人的协调下，SW 执行过一次 `configuration commit`，返回 `[sw1]`，但随后的普通及 verbose commit 列表都仍为 `No commit point exists.`。

证据：`reports\r70-sw-checkpoint-20261107-234349`。该次探查结果明确为 `NATIVE_CHECKPOINT_NOT_CREATED`，不能将返回 prompt 视为 checkpoint 成功，更不能据此执行未验证的 HA/整机恢复动作。完整配置 canonical SHA256 前后相同；未执行 replace。

## 测试与边界

- `H3cOperationalCapabilityTests.Run()` 定向 PASS：来源、Current/历史/未来时间、跨 profile 隔离、证据时效、最新失败不回退旧成功、原始敏感字段不回显，以及拓扑/AP 角色/HA 分离。
- Python 探查脚本语法检查 PASS；新 AC/SW 只读探查全部完成，无设备配置变更。
- 设备切换耗时、AP 重新关联耗时、真实无线业务恢复：**NOT_TESTED**。现有证据尚不能构成 `DEVICE_VERIFIED_HA`。
- 下一步须先明确当前模拟器支持的 AP 冗余切换路径，备齐实际可用恢复机制和业务探针，再执行受控切换。当前不能断言模拟器绝对不支持 HA。

未访问旧 NetConsole 数据根；未改两台 AC 的配置；未重启任何现有运行进程。本工作流未执行 push、merge 或发布。
