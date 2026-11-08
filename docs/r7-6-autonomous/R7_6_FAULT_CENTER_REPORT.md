# R7.6 故障中心事实报告

日期：2026-11-08。Agent：codex-A。源码基线：`50db848`。总体状态：**PARTIAL**。

当前主线保留有界持久故障生命周期与同资源恢复证据；本轮修复取消任务误报。没有本轮诱发的真实 AP 离线、AC 切换、接口故障或完整现场恢复循环，不能以自动化 fixture 或 TCP 连通性取代业务故障恢复验收。

## 本轮取消修复

提交 `bcdeb355b631b0dc9895036110fac155543e9df9` 在 `OperationalHealthService.cs` 提取 `IsCollectionFailure(state, captureStatus)`：`state=CANCELLED` 时，即使 captureStatus 为 PARTIAL，也不创建 COLLECTION_FAILURE 告警。取消仍保留在任务历史，不伪装成业务故障或设备离线。

FAILED、TIMED_OUT 以及非取消的 FAILED/PARTIAL capture 仍为失败证据；SUCCEEDED/COMPLETE 与 RUNNING/PENDING 不制造故障。新增 `CancelledTaskIsNotFault` 回归覆盖这些分支。修复只控制新故障投影，不声称迁移或删除已持久化的历史误报。

## 当前业务链与边界

| 环节 | 已实现行为 | 证据约束 |
| --- | --- | --- |
| 检测 | INTERFACE_DOWN、LLDP_NEIGHBOR_LOST、COLLECTION_FAILURE、EVIDENCE_STALE、PROBE_PACKET_LOSS、TRACEROUTE_ANOMALY、AP_OFFLINE/AP_CONFLICT | 缺证据 UNKNOWN、过期 STALE；关闭会话或采集失败不推断离线；权限拒绝等非业务路由失败不冒充路径异常 |
| 精确聚合 | Site/device/profile/kind/resource 指纹；有来源时加入 SourceAuthority 与 CapturedScopeKey；重复或旧证据不增加有效次数 | 不合并不同命令计划、不同接口或捕获范围；collection-plan 指纹包含命令及冻结端点 |
| 生命周期 | OPEN、ACKNOWLEDGED、PROCESSING、RESOLVED、REOPENED；处理历史和恢复证据持久化 | 确认/开始处理不等于恢复；只有更新的有效故障证据才复发 |
| 查询和交接 | 总览故障中心按精确 device/state/kind/time 筛选，分页、范围统计、来源证据、设备配置历史入口和当前页 CSV | CSV 为 `CURRENT_PAGE_LOADED_QUERY_ONLY`，保留已应用筛选和本页历史，具名凭据模式脱敏/公式防护不保证任意业务文本无敏感内容 |
| 只读补采 | 显式策略默认关闭，5–1440 分钟间隔，最多 3 轮；复用现有连接和 TaskPlan | 认证失败、目标变化、撤权停止；仅已知超时/断连可重试；连接/入队/补采成功不等于恢复，不执行设备修复写入 |
| 持久化 | `operations/fault-center.json`：最多 1000 事件、每项最近 50 次转换、64 策略、8 MiB | 磁盘提交成功才推进内存；满容量仅淘汰已恢复事件；损坏失败封闭并保留原件；重启中断策略停为 INTERRUPTED，不自动重放 |

API 复用 `/api/system/operations/faults` 的 query、`/sync`、`/{id}/ack`、`/{id}/process`、`/{id}/verify` 和 `/policies/{deviceId}`，执行当前身份、角色及 Site scope 检查。后台约每 15 秒轮巡一页 25 台授权设备；单次手动同步也只读一页，不把未扫描设备视为恢复。

健康投影有界：近期最多 100 任务、每设备 1000 Current Facts、100 显示事件，诊断取最近 20 个授权任务中 5 分钟内完成且目标精确匹配的样本。无线每次最多检查当前页的 10 个 AP，并轮转后续资源；这修复只查前 10 项的覆盖遗漏，但不代表全局同时扫描或没有发现延迟。

## 恢复核验保持同资源的新证据

| 故障 | 恢复要求 |
| --- | --- |
| 接口/证据过期/LLDP | 更新且在 5 分钟窗口内的 Current、Raw-verified、完整有效事实；同 profile/来源/捕获范围；接口必须相同且 UP；LLDP 必须完整表重新包含精确邻居 |
| 采集失败 | 相同类型、目标、完整命令计划、冻结 device/profile/Site/endpoint 的更新 SUCCEEDED / COMPLETE 任务；无关命令成功不能恢复 |
| 丢包/路由 | 同 protocol/address/port 或同路由目标的更新终态诊断；丢包为 0 或路径明确 reached，且晚于故障 |
| AP_OFFLINE | 同 AP Resource，所有参与来源均为新鲜 `LIVE_VERIFIED` / SUCCESS，晚于故障；来源包含原 AC/Site；RUNNING_MASTER 或 RUNNING_BACKUP 可作为证据 |
| AP_CONFLICT | 仍需满足同资源、全来源有效且聚合 RUNNING_MASTER 的既有强约束；仅 backup 观察不能解除冲突 |

没有满足要求时返回 `RECOVERY_NOT_VERIFIED`，保留故障状态。`AP_EFFECTIVE_STATE_CHANGED` 为信息事件，不因缺采样推断切换，也不会单独作为 WARNING 故障入库。双 AC 的 R/M、R/B 或控制器 Common 不证明 HA 切换和客户端业务恢复。

## 覆盖与剩余验收

已复核源码回归入口：`OperationalHealthTests.cs` 新增取消误报并保留轮转、UNKNOWN/STALE、严格 LLDP、路由精确目标和认证不重试；`FaultCenterTests.cs` 覆盖指纹/历史、源范围、同命令恢复、AP全来源、持久化/损坏、撤权及策略；`web/src/components/FaultCenter.test.ts` 覆盖 UI 行为。只读核对实际全量日志 `reports/r76-service-full-final.log`，基线 `50db848` EXIT 0，健康 17/17、故障中心 23/23 PASS；主集成确认最终 Web 35 files / 370 tests、typecheck PASS。本报告未重跑测试，不以源码常量作为执行证据。

| 验收项 | 当前结论 |
| --- | --- |
| 取消任务误报修复 | 已实现；实际全量 Service 健康 17/17 与故障中心 23/23 PASS |
| 当前主线 Service / Web 故障门禁 | Service 全量 50db848 EXIT 0；最终 Web 35 files / 370 tests 与 typecheck PASS 为主集成确认；最终候选合成权限 HTTP 40 项 PASS，范围如下 |
| 最终候选故障历史/API/CSV 双分辨率浏览器 | 合成 HTTP 的范围、PROCESSING 历史重启持久化、撤权与策略准入通过；共享 1920×1080 / 2560×1440 的 faultFilters 与 broad faultLldpFilter PASS；不扩张为故障生命周期写操作、故障 CSV 下载或真实恢复专项验收 |
| 真实 AP/接口/设备业务故障→恢复循环 | NOT_TESTED；未诱发现场事件，也不把旧事件升级为本轮实时证据 |
| 车辆移动中丢包、AP状态、Site/时间轴关联 | PARTIAL / NOT_TESTED；需精确身份和同步实际观测，不根据同名、IP或邻近时间补造关联 |
| Mesh/轨交专项根因、全通知与长期规模负载 | PARTIAL；通用故障中心不等于完整 Mesh/MR 分析、消息交付和长期自治运维闭环 |

最终候选 `D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63` 的 Service.exe ProductVersion=`1.0.0+7099775c7ded206d2b04d40faf4930e4d59c9297`。`reports/r72-feature-http-20261108-220844-e695e3.json` 标记 SYNTHETIC_REAL_HTTP_AUTHORIZATION / PASS，共 40 checks、deviceConnections=0、recoverySuccessClaimed=false。其中使用合成故障与一条显式 synthetic Syslog，验证匿名 401、ADMIN 只读角色 403、CSRF、范围缩小/撤销、历史重启持久化、功能开关及损坏批处理存储隔离；未准入设备采集/探测。缺失计划的 run-now 只验证 HTTP admission，不证明执行成功。该结果与真实业务故障恢复明确分开。

完整离线两模式均因必需 Traceroute 的 ICMP_PERMISSION_DENIED 而 EXIT 1，不能借其他 HTTP 子项 PASS 宣称完整候选通过。独立 Long Ping HTTP 的本机 ICMP 成功也不能作为现场故障解除证据。

共享候选 18771 / PID 49344 HEALTHY，Vite 5174 / PID 32812 保持不动。最终浏览器原件 `reports/r76-browser-final-20261108/result.json` 与 `reports/r76-broad-browser-final-20261108/result.json` 在两视口均以 LLDP_NEIGHBOR_LOST 筛选得到 HTTP 200，当前匹配 0 条；无 pageerrors/横向溢出/设备写入，不能将空列表推定为全局无故障或已恢复。共享只读整体 PARTIAL 来自 AP 无已存有效变化，不是故障筛选失败；隔离 Long Ping 浏览器通过同样不能作为现场故障解除证据。

本报告只新增文档；没有开启 Service、配置策略、设备重连/写入或业务数据复制。相关：[功能对等](R7_6_FEATURE_PARITY_REPORT.md)、[无线](R7_6_WIRELESS_REPORT.md)、[设备互操作](R7_6_DEVICE_INTEROP_REPORT.md)、[R7.4 故障来源边界](../r7-4-autonomous/R7_4_FAULT_CENTER_REPORT.md)。
