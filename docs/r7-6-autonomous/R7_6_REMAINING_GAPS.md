# NetConsole-Next R7.6 Remaining Gaps

日期：2026-11-08。总体状态：**PARTIAL**。

| 范围 | 状态 | 尚缺证据或条件 |
| --- | --- | --- |
| 候选离线 Traceroute | PARTIAL / BLOCKED_BY_HOST | APPHOST 与 DOTNET_DLL_EXPLICIT 完整门禁均为 `ICMP_PERMISSION_DENIED`；需在获准的独立 Windows 环境定位并验证原生 ICMP 权限，不得以 TCP 或窄诊断替代。 |
| AC1 / AC2 | SIMULATOR_READONLY_PASS | 已有只读 Raw/Facts；仍不是物理设备或 HA 证据。 |
| SW | BLOCKED | 现有 inventory profile 认证失败；需要新的有效凭据，不能重复失败认证。 |
| FIT-AP | BLOCKED / AUTH_CREDENTIAL_UNAVAILABLE | Telnet 到 Password 提示；需要批准的凭据、exec-console 生效读回和原生身份核对。 |
| 双 AC HA | NOT_TESTED | 未执行切换、回切、收敛、客户端连续性或业务恢复测量。 |
| 无线 AP 历史 | PARTIAL | 双分辨率导出通过，但当前数据没有有效变化记录；不从空历史推断 HA 或状态变化。 |
| Long Ping 远端业务 | PARTIAL | 回环 ICMP 生命周期和 CSV 已通过；远端设备、车辆移动、AP/Site 时间关联和多日运行未验。 |
| 丢包上下文 | PARTIAL | 历史上下文已实现且HTTP18/双视口18检查通过，真实客户端到AP关联仍UNKNOWN；不使用未来Observation。 |
| 故障中心 | PARTIAL | 合成权限、持久化和取消误报回归通过；真实设备故障、恢复、通知和复发未验。 |
| 配置恢复 | PARTIAL / HARDWARE_PENDING | 备份、预检、差异、脱敏和模拟器流程通过；设备 Writer、完整回读、业务验证和失败回滚未验。 |
| Windows 部署 | NOT_TESTED | LocalManager/安装计划离线检查通过；真实 elevated SCM 安装、自动启动、崩溃恢复、升级/回滚/卸载未执行。 |
| 跨机与版本 | NOT_TESTED | 独立 Windows 主机、TLS/远程访问和跨版本兼容仍待验收。 |
| 长稳 | NOT_TESTED | 保留 300 秒、1 次重启、30 条 UDP/Raw 校验历史；不能写成 24 小时稳定性。 |
| 原版专业功能 | PARTIAL | Mesh、轨交/MR 专业报表、完整 Excel 流程、跨站拓扑和通知仍未完整迁移。 |

未完成项不会通过状态文字掩盖。设备认证、配置写入、HA、SCM 和真实恢复均保持清晰边界；没有新增设备写入或旧数据访问。
