# R7.6 FIT-AP 原生登录事实报告

日期：2026-11-08。Agent：codex-A。报告基线：`2520082`。状态：**BLOCKED / AUTH_CREDENTIAL_UNAVAILABLE**。

本轮 FIT-AP `192.168.56.4:23` 的无凭据探测完成 TCP 连接、Telnet 协商并到达 Password 提示。没有发送用户名或密码，没有完成认证；原生版本、身份、接口和 Raw/Facts 采集仍未验证。FIT-AP 继续属于 AC Domain。

## 本轮探测证据

原始报告：`D:\study\NetConsole-Next-Workspace\reports\r71-device-20261108-211219\summary.json`，开始时间 `2026-11-08T13:12:19.249263+00:00`，约本地 21:12。

| 字段 | 原始值与范围 |
| --- | --- |
| host / port | `192.168.56.4` / `23` |
| tcpConnected | true |
| telnetNegotiations | 8 |
| passwordPrompt / usernamePrompt | true / false |
| applicationBytes / elapsedMs | 414 / 16 |
| usernameSubmitted / passwordSubmitted | false / false |
| authenticated | false |
| state | `AUTH_CREDENTIAL_UNAVAILABLE` |
| configurationWrites / haSwitchExecuted | false / false |

这证明上述时间点的 TCP/Telnet 服务可达并呈现密码提示。它不证明正确凭据、成功登录、exec-console 当前生效、AP 原生身份或命令执行能力。报告 `devices=[]`，不能把它当作 SW 或 AC 已认证采集证据。

## AC 远程登录前置条件

用户明确给出的前置命令为：

```text
sys
probe
wlan ap-execute all exec-console enable
```

[R7.5 FIT-AP 报告](../r7-5-autonomous/R7_5_FIT_AP_REPORT.md)保留此前已授权 R7.4 AC1 running-config 操作的事实：最后一条命令被接受，但 running-config/help 未给出确定的生效读回；未执行 `save`。这是历史证据，不等于本轮重新执行或当前状态确认。本轮没有重发上述命令，没有配置写入或凭据猜测。

下一步需要已有且获准使用的有效 FIT-AP 凭据，以及 exec-console 的有效状态证据，再执行直接只读登录、版本和身份核对。不得重试先前失败的凭据或因出现 Password 提示就宣称登录成功。

## AC 观察与直接登录的证据边界

同轮 AC 只读报告观察到精确资源 `ap-resource:60d0aad0d9f1b381c501aff3`，序列号 `H3C_4C-48-CB-6E-04-00`、MAC `4C:48:CB:6E:04:00`、型号 `WA6320-HCL`，AC1 为 R/M、AC2 为 R/B。该证据属于 AC 管理观察；不能用它代替 `192.168.56.4` 直接登录后的 MAC/序列号核对，也不能只凭显示名认定目标 IP 与资源完全等同。来源与 Raw 详见 [设备互操作报告](R7_6_DEVICE_INTEROP_REPORT.md)。

| 验收项 | 当前结论 |
| --- | --- |
| FIT TCP/Telnet 无凭据探测 | PASS，限上述时间点与 Password 提示 |
| AC 管理观察 | SIMULATOR_READONLY_PASS；保留两台 AC 独立来源 |
| exec-console 当前有效状态 | UNVERIFIED；历史命令 accepted 不足以证明 |
| 直接认证、原生版本/MAC/序列号/接口 | BLOCKED / NOT_TESTED |
| 原生 Raw/Facts 与运行状态 | NOT_TESTED；AC Facts 不能冒充原生证据 |
| HA、无线终端业务、配置保存 | NOT_TESTED |
| 最终候选 FIT 展示与阻断 UI 浏览器验收 | NOT_TESTED，FIT 专项直接登录/阻断交互未取得证据；共享无线观察页面双视口 PASS、所选 AP 历史 CSV 为无有效变化的 PARTIAL，均不能消除 FIT 原生认证阻断 |

最终候选为 `D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63`，Service.exe ProductVersion=`1.0.0+7099775c7ded206d2b04d40faf4930e4d59c9297`。主集成确认 Web 35 files / 370 tests 与 typecheck PASS；两种启动方式的完整离线门禁均因必需 Traceroute 权限拒绝而 EXIT 1。它们没有提供 FIT 原生凭据、直接登录或当前 exec-console 生效证据。

最终共享候选 18771 / PID 49344 HEALTHY，Vite 5174 / PID 32812 不动。`reports/r76-browser-final-20261108/result.json` 与 `reports/r76-broad-browser-final-20261108/result.json` 的通用无线只读检查不构成 FIT 原生登录或专项阻断操作验收；当前导出没有有效 AP 状态变化，不能据此宣称 HA。

本报告只读复核既有报告，没有重连设备、重试凭据、写配置或复制业务数据，未访问旧 NetConsole 数据根。相关：[无线报告](R7_6_WIRELESS_REPORT.md)。
