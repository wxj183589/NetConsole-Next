# R7.6 Ping 产品与实际采样报告

日期：2026-11-08。Agent：codex-A。源码基线：`cef8273`。状态：**PARTIAL / LOCAL_LOOPBACK_SERVICE_E2E_PASS**。

本轮有界 Long Ping 的 HTTP 生命周期已取得最终集成候选的真实本机 ICMP 证据，历史趋势已补齐 Quick/Long 类型隔离。没有远端设备、无线空口、车辆移动或长期负载验收。最终候选完整离线两模式均因必需 Traceroute 失败而 EXIT 1；Long Ping 独立 HTTP 通过与该失败分别记录。

## 产品行为

Quick Ping 支持 2–60 样本、100–5000ms 间隔/超时、最多 8 目标、每目标 120 秒预算。Long Ping 支持 10–3600 秒、1000–5000ms 间隔、100–5000ms 超时、最多 8 目标。ICMP 与 TCP 保留不同协议语义；TCP connect 成功不证明无线业务恢复。

`cdaac2b` 增加 Long Ping 暂停/继续：当前 probe 排空前为 PAUSING，排空后为 PAUSED；暂停不延长原墙钟窗口、不补发未执行样本，暂停期间继续复核当前身份与目标。停止保留已取得样本，未发送不计丢失。既有授权、CSRF 和当前 device/Site/profile/host/port 检查继续生效。

`efdfa8b` 的 P95 按实际已回复延迟排序，取第 `ceil(0.95 × 回复数)` 个值；没有回复时为未知。跨任务统计使用全部有效回复计算，不能平均各任务 P95。`d9041b3` 限制样本位于对应 admission/开始与结束时间内，并核对计数、序号和延迟，避免坏历史制造确定指标。

`91d0a63` 将 LONG_PING 纳入历史趋势和 CSV，并按 actor/Site/device/**采样类型**/protocol/endpoint/port 分组，Quick 与 Long 不混算。RUNNING、QUEUED、PAUSING、PAUSED 不纳入终态汇总；身份或端点缺失保持未知。界面只读取最近 50 条历史中的最多 20 个相关任务，不发起探测。CSV 含 `diagnostic_kind`、原始任务/样本指标、失败读取列表与 `LOADED_SCOPED_HISTORY_ONLY_MAX_20_RUNS`，不宣称无界历史。

## 本轮 HTTP 实际证据

最终候选原始报告：`D:\study\NetConsole-Next-Workspace\reports\r76-long-ping-http-25e00169234f\result.json`，`status=PASS`、`evidenceLevel=SERVICE_E2E`，共 13 项实际核验。目标为 `127.0.0.1`、协议 ICMP，使用最小合成库存与独立 Next fixture；真实采样由 Service 发出，不是注入的探测回复。`deviceConnections=0`、`configurationWrites=0`。

该报告实际使用 `D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63\NetConsole.Next.Service.dll`。此前 `r76-long-ping-http-fc7123eac86d` 使用另一构建目录，仅保留历史范围；下列任务 ID 和样本均取自最终候选报告。

| 核验 | 已有结果与范围 |
| --- | --- |
| 真实回环 ICMP | PASS，取得实际 REPLIED 样本 |
| 只读角色与缺 CSRF 暂停请求 | PASS，拒绝操作 |
| 暂停排空、无新增样本/丢失、原任务继续 | PASS |
| 停止、终态禁止继续 | PASS，已采样保留，不制造丢失 |
| 原窗口不延长或补满 | PASS，10 秒窗口按原截止结束 |
| P95 nearest-rank | 两任务均 PASS，实际回复延迟均为 0ms，因此 P95 为 0ms |
| 真实 Service 重启 | 终态历史保留且不重放 Ping；SQLite integrity OK |

取消任务 `484a1264fb0c4696ae3b165f090f3f24`：CANCELLED，requested=30、sent=3、received=3、lost=0、P95=0ms；未执行 27 次不能算丢包。窗口任务 `0ef49792b14a40659a287997d38086e0`：SUCCEEDED / LONG_PING_WINDOW_ENDED，requested=10、sent=2、received=2、lost=0、P95=0ms，elapsed=10006ms，未执行 8 次不计丢失，来源 `SERVICE_HOST_ICMP`。取消快照的 evidenceCount=0，但保留上述 quickPing 样本；不将其改写为完整成功诊断。

## 自动化与候选边界

主任务确认 Service 全量基线 `50db848` EXIT 0；已只读核对 `reports/r76-service-full-final.log`，包含 Quick Ping 9/9、Long Ping control 3/3、Service authority 7/7、Network Diagnostics 10/10、Operational Health 17/17、Fault Center 23/23 PASS。主任务同时确认最终 Web 370/370 与 typecheck PASS；具体执行命令和最终制品验收统一由主任务记录，本报告未重跑测试。

候选目录为 `D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63`。只读检查 Service.exe 的 ProductVersion 为 `1.0.0+7099775c7ded206d2b04d40faf4930e4d59c9297`；目录名不是可替代的制品 commit 证明。

| 待验范围 | 结论 |
| --- | --- |
| 最终候选 apphost / 显式 dotnet DLL | 完整离线 APPHOST / DOTNET_DLL_EXPLICIT 均 EXIT 1，必需 Traceroute 为 PARTIAL / ICMP_PERMISSION_DENIED；日志为 reports/r76-offline-apphost.log 与 r76-offline-dotnet.log；独立 Long Ping HTTP 13 项 PASS，限上文实际 DLL 路径 |
| 最终候选双分辨率浏览器、历史趋势与 CSV | NOT_VERIFIED；1920×1080 / 2560×1440 的最终实例、历史趋势操作、下载和类型隔离尚无实际浏览器完成证据 |
| 一小时上限、多日监测、规模与保留策略 | NOT_TESTED / PARTIAL；短回环生命周期不能代替长期 soak 或原版车队 fping |
| 远端实际丢包、AP/Site/车辆时间关联 | NOT_TESTED；不能根据同名、IP或邻近时间补造关联 |
| AP离线/切换后的无线业务恢复 | NOT_TESTED；ICMP回环和TCP连通均不能代替 |

本报告编写仅只读复核既有证据，未启动 Service、设备会话或配置写入，未复制业务数据。相关：[Traceroute](R7_6_TRACEROUTE_REPORT.md)、[故障中心](R7_6_FAULT_CENTER_REPORT.md)、[功能对等](R7_6_FEATURE_PARITY_REPORT.md)。
