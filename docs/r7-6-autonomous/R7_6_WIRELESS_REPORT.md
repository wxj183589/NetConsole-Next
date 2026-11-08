# R7.6 无线产品与证据报告

日期：2026-11-08。Agent：codex-A。报告基线：`2520082`。状态：**PARTIAL**。

本轮增加当前 AC 快速只读采集与 AP 有效状态历史 CSV 交接。双 AC 的产品采集和精确 AP 来源保留已有原始证据；最终 Web 自动化通过，最终候选无线导出实际浏览器验收尚无完成证据。

## 产品改动

提交 `afcd4f098a6a31beb0a945a74e48f34415279519` 在 `web/src/views/WirelessView.vue` 增加“采集当前 AC”。目标仅为所选 Domain 和所选 AC，固定快速命令为：

```text
display version
display wlan ap all
display wlan ap all address
```

该入口复用 `submitTask` / `createWirelessTask`、既有后端 admission、任务历史和详情；`taskSubmitting` 防重复提交。快速入口的三条命令与本轮设备验收探针的六条命令是不同范围。没有为 FIT-AP 创建独立无线 Domain。

提交 `09fe24fc92c746d60f8deca9580a2fe59d7acfff` 新增 `web/src/api/wirelessHistoryExport.ts`，由 WirelessView 导出当前已加载、已限定范围的 AP runtime：Current、PreviousEffective、Recent10 有效变化，包含聚合状态和各 AC 的独立来源。导出范围为 `LOADED_SCOPED_CURRENT_PREVIOUS_RECENT10_ONLY`，不是无界历史，导出本身不会重新采集。

CSV 保留资源 ID、序列号/MAC、源 AC/站点、原始状态、Raw 引用、source/runtime/parser revisions、capture/asOf/export UTC、freshness/outcome/history status、claims scope/warnings、Domain/AC 过滤以及聚合来源。继续保留 COLLECTION_FAILED、STALE、RECENT10_PARTIAL，不把采集失败制造成 OFFLINE 或 REAL_HA。

资源不匹配返回 `AP_HISTORY_RESOURCE_MISMATCH`。加载中、读取错误、runtime 缺失或 AP 不匹配时禁止导出，防止读取失败后导出旧数据。CSV 使用 UTF-8 BOM、CRLF、引号转义，并对空白/控制字符后的 `= + @ -` 起始单元格加公式注入保护。

## 定向回归与实采证据

源码已增加当前 Domain/AC 采集与导出禁用条件回归；独立 `wirelessHistoryExport.test.ts` 包含四项测试，覆盖 scoped 来源、资源错配、转义和公式防护。主集成确认最终 Web 为 35 files / 370 tests PASS、typecheck PASS；本报告只读复核提交和证据，没有重跑。自动化与 fixture 通过不能替代最终候选页面的实际采集与下载操作。

双 AC 原始报告为 `D:\study\NetConsole-Next-Workspace\reports\r72-product-dual-ac-20261108-211219\summary.json`。2026-11-08 21:12 本地时间，产品任务分别为 `672a4e028f8c40efbe96a52843ff6675` 与 `64eee15fc8364043bd95d2ec131594be`，各六条只读命令成功，合计 12 个 Raw 校验通过。详见 [设备互操作报告](R7_6_DEVICE_INTEROP_REPORT.md)。

同一精确资源 `ap-resource:60d0aad0d9f1b381c501aff3`，序列号 `H3C_4C-48-CB-6E-04-00`、MAC `4C:48:CB:6E:04:00`，分别保留 AC1 的 R/M 和 AC2 的 R/B。不能用 AP 名称模糊合并，不能覆盖独立来源；`apGroup=null` 保持未知。控制器 `Common` 不是 HA 主备证明。保存的 Facts 可为 STALE，导出必须继续携带对应 freshness 与时间，不能将其升级为当前实时状态。

| 验收项 | 本报告结论 |
| --- | --- |
| 所选 Domain/AC 快速采集入口 | 已实现并补充回归覆盖；复用产品任务链 |
| Scoped AP Current / PreviousEffective / Recent10 CSV | 已实现并补充回归覆盖；不代表全量历史 |
| 双 AC 六命令产品采集与 Raw | SIMULATOR_PASS_READONLY_OBSERVATION，限原始报告窗口 |
| AP group 修改、HA 切换/回切、客户端连续性 | NOT_TESTED；本轮没有设备配置写入 |
| 最终候选无线 API/导出实际浏览器链路 | 合成回环离线 AC_RESOURCES 子项 PASS（12 固定只读命令、Raw hashes、scoped Facts）；无线页面实际提交/CSV 下载尚未取得最终浏览器证据 |
| 最终候选双分辨率浏览器验收 | NOT_VERIFIED；1920×1080 / 2560×1440 的实际操作和截图尚未完成验收 |
| 最终主线 Renderer 测试与构建 | 主集成确认 Web 35 files / 370 tests、typecheck PASS；候选 STATIC_UI 资产 HTTP 哈希子项 PASS；本报告未独立重跑构建 |

候选为 `D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63`，Service.exe ProductVersion=`1.0.0+7099775c7ded206d2b04d40faf4930e4d59c9297`。`reports/r76-offline-apphost.log` 与 `reports/r76-offline-dotnet.log` 的完整离线门禁均 EXIT 1，必需 Traceroute 返回 PARTIAL / ICMP_PERMISSION_DENIED；无线相关子项通过不改变整体失败结论。候选目录后缀不能替代制品嵌入版本。

本报告未新建设备会话、未写配置、未运行导出采集，也未复制 Site 或旧 NetConsole 数据根。最终候选证据不能由源码、mock 浏览器或此前版本结果替代。相关：[FIT-AP 报告](R7_6_FIT_AP_REPORT.md)。
