# R7.6 设备互操作事实报告

日期：2026-11-08。Agent：codex-A。报告基线：`2520082`。总体状态：**PARTIAL / SIMULATOR_READONLY_OBSERVATION**。

本轮双 AC 通过产品 Netmiko Task 完成只读采集与 Raw 校验。SW 认证采集仍阻断，FIT-AP 仅到无凭据 Telnet 探测。没有配置写入、HA 切换、故障注入或恢复。模拟器上的真实 SSH/Telnet 传输不能代替独立物理硬件验收。

## 本轮证据与设备范围

双 AC 原始报告：`D:\study\NetConsole-Next-Workspace\reports\r72-product-dual-ac-20261108-211219\summary.json`。采集窗口为 `2026-11-08T13:12:19.243713+00:00` 至 `2026-11-08T13:12:31.397229+00:00`，本地时间约 21:12。传输标记 `PRODUCT_NETMIKO_TASK`，整体校验为 `SIMULATOR_PASS_READONLY_OBSERVATION`。

| 设备 | 产品身份 / 范围 | 本轮证据与结论 |
| --- | --- | --- |
| AC1 `192.168.56.2:22` | `r68-ac1` / `r68-simulator`；profile `b9feedeb32e54ed0a99c113cf84263d6` | run `672a4e028f8c40efbe96a52843ff6675`，任务 SUCCEEDED、validation PASS |
| AC2 `192.168.56.5:22` | `r68-ac2` / `r68-simulator`；profile `fc7145961a2a4bcfbabb2d14347c2d85` | run `64eee15fc8364043bd95d2ec131594be`，任务 SUCCEEDED、validation PASS |
| SW `192.168.56.3:22` | 本轮仅 SSH banner 范围 | 已认证采集 BLOCKED；此前唯一启用且绑定 inventory 的 profile 认证失败，本轮未重试、未尝试替代凭据 |
| FIT-AP `192.168.56.4:23` | AC Domain 下的 FIT-AP | TCP/Telnet 到 Password 提示；未发送用户名或密码，认证 `AUTH_CREDENTIAL_UNAVAILABLE` |

两台 AC 均报告 `WX5540H-HCL`、Comware `7.1.064 / Alpha 7165`。每台各采集下列六条命令，全部 SUCCEEDED、capture COMPLETE、parse PARSED；合计 12 个命令 Raw 均有原报告的 `rawVerification[].verified=true`、分段/输出 SHA256 和字节数校验：

```text
display version
display wlan ac-role
display wlan ap all
display wlan ap all address
display wlan ap all radio
display wlan ap all verbose
```

Facts 来源分别为 `H3C_TASK_RAW:672a4e028f8c40efbe96a52843ff6675` 与 `H3C_TASK_RAW:64eee15fc8364043bd95d2ec131594be`。采集时 capability snapshot 对应能力为 RECENT / VERIFIED；保存的 Facts 中已有 STALE 状态，不能把历史采集成功描述为报告阅读时保证在线。两台控制器角色均为原始 `Common` / 规范化 `COMMON`，不能当作 HA active/standby 证明。

## 同一 AP 的独立来源

精确资源 `ap-resource:60d0aad0d9f1b381c501aff3`，`identityStatus=CONFIRMED`；显示名 `4c48-cb6e-0400`，序列号 `H3C_4C-48-CB-6E-04-00`，规范 MAC `4C:48:CB:6E:04:00`，型号 `WA6320-HCL`。`apGroup=null`，表示未知。

| 来源 | 原始状态 / 规范状态 | 独立观察与原始引用 |
| --- | --- | --- |
| AC1 / r68-simulator | `R/M` / `RUNNING_MASTER` | `ap-observation:8087afdc376049d496bb339e`；capturedMs `1794143545156`；manifest `fact:451c05ea1b22b2cea5868a8927b1679a` |
| AC2 / r68-simulator | `R/B` / `RUNNING_BACKUP` | `ap-observation:bdfaaccf9c48bbac2ab2cdfb`；capturedMs `1794143551284`；manifest `fact:da4922644c3145927879e598b73ba0f5` |

关联依赖精确序列号/MAC并保留 AC、站点、Raw 和采集时间，不能按名称模糊合并。R/M 与 R/B 是各自采集时的观察，没有执行切换、回切、收敛或终端连续性验证。两台 AC 的 radio 快照均为 Down、clients=0；AP 角色观察不证明无线客户端业务可用。

## 阻断与最终候选

| 验收项 | 当前结论 |
| --- | --- |
| 双 AC 产品只读任务与 Raw 完整性 | SIMULATOR_PASS；仅上述采集窗口与命令范围 |
| SW 认证、版本、接口、LLDP 和 Raw/Facts | BLOCKED；需要既有有效 profile，不重试失败凭据 |
| FIT-AP 直接认证与原生身份 | BLOCKED；参见 FIT-AP 报告 |
| 物理硬件、HA 切换/回切、端到端业务 | NOT_TESTED |
| 最终 R7.6 候选产品/API/浏览器验收 | 回环合成设备的 Netmiko 连接/重连、AC 12 固定命令及 scoped Facts/Raw 子项两模式均 PASS；完整离线两模式均 EXIT 1（必需 Traceroute 权限拒绝）；共享 18771 / PID 49344 HEALTHY，双分辨率只读浏览器已验收，范围如下 |

最终候选为 `D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63`，Service.exe ProductVersion=`1.0.0+7099775c7ded206d2b04d40faf4930e4d59c9297`。证据为 `reports/r76-offline-apphost.log` 与 `reports/r76-offline-dotnet.log`；其中合成 SSH 与上文 21:12 的双 AC 产品采集是不同来源。最终候选权限 HTTP 报告 `reports/r72-feature-http-20261108-220844-e695e3.json` 为 PASS / 40 checks，标记 SYNTHETIC_REAL_HTTP_AUTHORIZATION、deviceConnections=0、recoverySuccessClaimed=false，也不构成新的设备互操作或恢复证据。

SW 本轮范围来自 [R7.6 work pool](R7_6_WORK_POOL.md)，此前认证失败来自 [R7.5 设备报告](../r7-5-autonomous/R7_5_DEVICE_INTEROP_REPORT.md)；不把 FIT 探测 JSON 当作 SW 证据。FIT 本轮报告为 `D:\study\NetConsole-Next-Workspace\reports\r71-device-20261108-211219\summary.json`。

`reports/r76-browser-final-20261108/result.json` 的共享只读浏览器在 1920×1080 / 2560×1440 对 faultFilters、wirelessCurrentAcAndExport、pingP95HistoryCsv、configurationHistory 均 PASS；wirelessSelectedApHistoryCsv 为 PARTIAL / NO_STORED_EFFECTIVE_CHANGES，导出成功但没有有效变化，不证明 HA。`reports/r76-broad-browser-final-20261108/result.json` 的四类页面双视口 PASS；均未发起设备连接/写入。`reports/r76-post-upgrade-integrity.json` 记录共享 SQLite ok、140 个 Raw 长度/SHA256 验证、incomplete/missing=0、recovery issues=0、checkpoint/19 资产不变；Vite 5174 / PID 32812 不动。这些产品读验收不消除 SW/FIT 认证和物理硬件阻断。

本报告编写阶段只读复核既有证据，没有再次建立设备会话、写配置或复制数据。未访问旧 NetConsole 数据根。相关：[无线报告](R7_6_WIRELESS_REPORT.md)、[FIT-AP 报告](R7_6_FIT_AP_REPORT.md)。
