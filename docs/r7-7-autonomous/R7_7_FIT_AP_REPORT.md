# R7.7 FIT-AP 原生管理报告

## 最终集成状态

状态：**BLOCKED（有效原生凭据与exec-console有效读回不足）**。新Worker已打入build/r77-candidate-api并在双AC实采验证；下文“待主集成发布”已完成。原生FIT登录、MAC/Serial与Raw/Facts仍未完成，未重试未知密码；不能把Telnet提示符或AC表身份升级为FIT原生成功。

## 此前工作流记录

日期：2026-11-08。Agent：codex-A。本批结论：**原生认证与身份核对仍未验证；已修复可影响原生只读采集完整性的 Worker 缺口**。

## 已完成产品改动

Comware SSH/Telnet 普通 `display` 命令现要求完整的用户/系统视图终止提示符。正文中出现设备名不再足以结束读取；缺少提示符、提示符变化、超时、断连时拒绝把结果视为完整并关闭不确定会话，不自动重放。实现继续复用官方 Netmiko，适用于后续 FIT 版本、接口等原生读取。

`python tests/netmiko_worker_test.py`：57 tests PASS，新增 8 项覆盖上述边界，包含官方 Netmiko 的双传输分片读取回归。详见 [设备报告](R7_7_DEVICE_INTEROP_REPORT.md)。本批没有连接 FIT、发送用户名或密码，测试不构成 FIT 登录成功。

## 既有证据与未完成项

此前 [R7.6 FIT 报告](../r7-6-autonomous/R7_6_FIT_AP_REPORT.md) 的 `192.168.56.4:23` 无凭据探测只确认 TCP/Telnet 可达、8 次协商及 Password 提示，`authenticated=false`、`usernameSubmitted=false`、`passwordSubmitted=false`。该历史快照不能证明当前网络状态、有效密码、原生 MAC/序列号或当前 exec-console 生效。

用户给出的 AC 前置操作为：

```text
sys
probe
wlan ap-execute all exec-console enable
```

此前记录的命令 accepted 缺少确定的生效读回，也没有执行 `save`。本设备工作流未重发命令；新增 AC 操作与读回由主任务协调，不能把历史 accepted 宣称为当前远程登录已启用。

双 AC 历史观察中的 AP 为 MAC `4C:48:CB:6E:04:00`、序列号 `H3C_4C-48-CB-6E-04-00`、型号 `WA6320-HCL`；AC1 R/M、AC2 R/B。它们是 AC 管理来源，须保留各自控制器、时间和 Raw 引用。不能用 AC 表中的身份代替 `192.168.56.4` 原生认证后直接读回，也不能仅按显示名将 IP 与 AP 合并。

| 验收项 | 本批状态 |
| --- | --- |
| Comware 原生读取完整性回归 | PASS，57 项 Worker 单元测试，含官方 Netmiko 双传输解析 |
| 新 Worker 进入实际候选 | 待主集成发布并刷新清单 |
| FIT 当前 TCP/Telnet 与 exec-console 生效 | 本批未重测；历史证据不能证明当前状态 |
| FIT 有效凭据与成功认证 | 未验证；不猜测、不提交未知凭据 |
| 原生版本、接口、MAC/序列号与 Raw/Facts | 未验证；须认证后通过产品流程取得 |
| HA 切换、无线客户端连续性、物理硬件 | 本设备工作流未执行 |

下一步在已授权且有效的设备配置条件下，先取得 AC 远程登录有效状态证据，再用已有有效 FIT 凭据完成原生只读登录，核对版本/MAC/序列号、命令 Raw 与采集完整性。若仍只有密码提示，保留阻断并记录原因，不以 AC Facts 或 TCP 成功替代原生验收。

本批没有修改设备、业务数据或旧 NetConsole 数据根，没有启动/停止共享服务。运行态和主任务后续真实模拟器结果须以其新增证据另行更新。
