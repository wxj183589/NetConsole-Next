# R7.1 双 AC 运行能力与 HA 实证

- 日期：2026-11-08；Agent：codex-A；分支：`codex-A/netconsole-next-r3-dual-ac`。
- 状态：**PARTIAL**。两台 AC 的产品采集、身份和控制器角色证据已验证；受控 HA 切换、回切和无线业务恢复仍为 **NOT_TESTED**。
- 设备操作串行执行；仅限现有白名单模拟器，未访问旧 NetConsole 数据根。

## 产品闭环

`H3cComware.cs` 从明确的 `H3C <model> uptime is ...` 行提取设备型号，支持真实 `Alpha 7165` 版本，并保留原 Release 格式兼容。板卡 Type、镜像文件名和冲突型号不能成为设备型号。

新增固定只读命令 `display wlan ac-role`、严格角色解析、封存 Raw 支撑的 `controller.role` 事实和 `AC_ROLE_READ` 能力。保留 Common/Master/Backup 原文及规范值；权限、未支持、空、不完整、冲突和未知角色均不能成为成功证据。最新失败不会回退使用旧成功角色。

设备详情展示型号、版本、控制器角色和证据来源/时效；任务、无线任务和 H3C 回放提供该固定命令入口。无线能力面板分别展示两台 AC 的 AP 角色原文、规范状态、时间、Raw 和 revision，并处理切换选择时的迟到响应。`Common`、AP `R/M` 和 `R/B` 均不自动验证 `HA_SWITCH`。

## 通过产品 HTTP 的真实采集

运行 `tests/r71_product_identity_probe.py`，使用现有本机 API token；不读取设备密码，不直接连接设备。分别创建有界任务，等上一台明确终态后才启动下一台。

证据：`D:\study\NetConsole-Next-Workspace\reports\r71-product-identity-20261108-005330\summary.json`。

| 设备 | 产品 run ID | 结果 |
| --- | --- | --- |
| AC1 | `d9bcc2a2c5114f6fb2097a0a9b897a02` | SUCCEEDED，两个命令 COMPLETE/PARSED |
| AC2 | `358161ffeb8247a4abff74428acc422e` | SUCCEEDED，两个命令 COMPLETE/PARSED |

两台均发布 `WX5540H-HCL / 7.1.064 / Alpha 7165`、`Common → COMMON`，`DEVICE_IDENTITY`、`AC_ROLE_READ` 为近期 Raw 已验证，`HA_SWITCH` 仍 UNKNOWN。证据已持久化到隔离测试数据；这些字段会按五分钟时效自然变为 STALE。

探针初次运行错误地将 CONNECTING 当作终态，首次两条只读任务后来均 SUCCEEDED。探针已修复为仅接受明确终态，并在 admission/非终态结果不明时停止后续设备；最终验收采用上表重新严格串行的两次采集，不把首次探针失败写成产品失败或最终成功证据。

## HA 准入与安全路径

只读实证目录：

- `reports\r71-device-20261108-003716`：身份、控制器角色、AP 表和 CLI 命令树。
- `reports\r71-ha-admission-20261108-004028`：AP verbose、地址/Radio、SW ARP/MAC/LLDP、仅非秘密网络配置摘要。
- `reports\r71-ap-help-20261108-004245`、`004707`、`004850`、`005003`、`005711`：已有 system/AP-group 视图帮助；两台配置 SHA 前后相同。

这里的 `reports` 均指 Workspace 的 `D:\study\NetConsole-Next-Workspace\reports`，不在 Git 保存完整配置或认证信息。

两台 AC 都观察到同一个完整 MAC `4c48-cb6e-0400`、serial `H3C_4C-48-CB-6E-04-00` 的 WA6320-HCL。AC1 为 Master/R/M，AC2 为 Backup/R/B；两侧 `Ready-for-Switchover: Ready`，当前 AC IP `.2`，CAPWAP data-tunnel Up。AC1 default-group priority 7、backup-ac `.5`；AC2 backup-ac `.2`、观察 priority 4。**确有 AP 备份配置，不能因控制器 Common 或缺少某一种 hot-backup 指令就说未配置冗余。**

SW 的 ARP/MAC 证明主机 `.1` 与 AC1 `.2` 同经 GE1/0/1；AC2 `.5` 经 GE1/0/3，AP `.4` 经 GE1/0/2。因此 shutdown SW GE1/0/1 会同时破坏管理回退路径，候选被否决，未执行。

已核实 `wlan ap-backup hot-backup` 只广告 enable，未发现显式手动 switch；AP-group 广告 priority/backup-ac，不能盲目改变优先级就声称切换。AP 是 auto AP，无已有显式 AP 配置段，未新建/固化 AP。`wlan enable` 和 `undo wlan enable` 有完整语法，但原始配置没有显式 enable，且其 CAPWAP 切换语义未被证明，未将猜测命令用于实际故障注入。最新 `005711` 证据进一步显示 AP-group `tunnel ?` 仅有 encryption，`switch-back ?` 仅有 enable/disable（自动从 central AC 回 local AC），`discovery-response ?` 仅有 wait-time，`wlan execute ?` 仅有 module；这些帮助均未证明存在可精确控制且可恢复的手动 AP 切换路径。两台配置 SHA 前后保持相同。本轮受控切换和回切最终记为 **NOT_TESTED**。

当前 Radio 1/2 均 Down，未发现已验证无线客户端。即使后续控制隧道切换成功，也必须单独标注业务恢复未测；不能把 AP Ready 或管理 ping 等价为客户端业务恢复。

## 验证

- C# AC Resource 测试：4/4 PASS，覆盖固定命令拒绝注入、角色解析/错误状态、封存 Raw→角色事实→能力投影与 Catalog 重启。
- C# operational capability 定向测试：PASS，涵盖型号、Alpha、最新失败、角色/HA 分离、scope、freshness 和 provenance。
- Web 五个相关文件：87/87 PASS；typecheck PASS。
- Python 四个探针语法检查 PASS；本工作流文件 `git diff --check` PASS。
- 连接探针恢复边界：`H3cConnectionTestService` 接收冻结授权复核 callback，创建前后、连接后、设备信息结果/降级结果前复核，并传递至真实 session 的 AUTO fallback / Netmiko reconnect 边界；撤销 scope 原样抛出，不能转为连接成功。Connection policy 8 组通过，覆盖 factory 重绑定、连接后撤销、命令后撤销、降级失败/超时，以及真实 H3cSsh alternate、connect retry、command replay 前拒绝第二 transport。

未执行交换机端口关闭、AP reboot/reset、无线配置写入、HA 切换或回切。完整 Service harness/build 和最终浏览器验收由集成负责人记录。
