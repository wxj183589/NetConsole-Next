# R3.2-C 无线故障闭环验收

更新时间：2026-11-15（Asia/Shanghai）

## 结论

```text
R3_2_C_STATUS=PARTIAL
WIRELESS_SYSLOG_PROJECTION=PASS
AP_IDENTITY_ASSOCIATION=PASS_EXACT_ONLY
FAULT_RECOVERY_RULE=PASS_UNIT_AND_FULL_REGRESSION
SERVICE_HTTP_E2E=BLOCKED_EXISTING_ISOLATED_SERVICE_MUTEX
RAW_TO_OPERATIONAL_HEALTH_E2E=NOT_VERIFIED
OPERATIONAL_HEALTH_TO_FAULT_CENTER_E2E=NOT_VERIFIED
BROWSER_E2E=NOT_RUN_GLOBAL_SINGLE_INSTANCE_MUTEX
EXISTING_RUNTIME_HEALTH=PASS_READ_ONLY_ONLY
```

## 本轮实现

- sealed Syslog 无线事件现在使用稳定资源键：唯一精确匹配时为 `ap:<resource-id>`，无匹配或多匹配时保留 `mesh:<sender>:<peer-or-UNKNOWN>`，不进行模糊或跨站点关联。
- AP 关联同时要求发送 AC、Site 和规范化 peer MAC 在同一 Wireless observation 中精确一致；不满足唯一性时显式保持 UNKNOWN。
- `MESH_LINK_UP` 现在只会在同一设备、Site、资源键、source authority、scope、更新且未过期的条件下，对 `MESH_LINK_DOWN` 或 `MESH_ACTIVE_LINK_SWITCH` 故障写入 `RECOVERY_VERIFIED` 并关闭 incident。
- 重复、过期、跨资源、跨来源或跨 scope 的恢复事件不会关闭故障。

代码提交：`6a6239c`（`补齐无线 Syslog 故障恢复闭环`）。

## 自动化证据

- OperationalHealth：27/27 PASS；包含精确 AC、freshness、peer MAC、唯一 AP 资源关联和 UNKNOWN 边界。
- FaultCenter：31/31 PASS；新增无线 Syslog 恢复的资源、来源、时间窗口和持久转换断言。
- SyslogQuery：8/8 PASS，包括真实 UDP timed seal。
- SyslogEvents：6/6 PASS，包括 sealed Raw 过滤、事件分类、严重性和 scope 校验。
- 服务默认全量回归 PASS；前端 `FaultCenter` 20 项、`SyslogWorkspace` 6 项以及全套 451 项测试 PASS。

## 服务级 E2E 阻断

本轮创建了仅含一个候选 `catalog.db` 和一个合成本地 AC 记录的隔离证据根：

`D:\study\NetConsole-Next-Workspace\reports\r3.2-c-e2e-20261115-000539`

尝试以独立端口启动当前 Release Service 时，被已有的全局互斥锁拒绝。现有实例来自另一个候选：

`D:\study\NetConsole-Next-Workspace\build\r78-handoff-candidate\NetConsole.Next.Service.dll`

本轮没有停止、复用或向该实例发送 Syslog。隔离尝试日志为 `service.stderr.log`，明确记录 `Another NetConsole Next service instance is already running.`；没有创建 token、没有监听端口、没有写入该候选 Raw 或 FaultCenter。待维护窗口结束并确认实例归属后，需重新执行：UDP down → sealed Raw → OperationalHealth → FaultCenter open → UDP up → FaultCenter resolved → UI evidence drill-down。

本轮只读检查确认该已有候选的 `/api/system/health` 为 `HEALTHY`、SQLite `ok`、Raw `176 finalized / 0 incomplete / 0 missing`，但它来自 R7.8 构建，不是当前 `6a6239c` 产品代码；因此这些健康字段只能作为现有隔离运行实例的完整性证据，不能提升为当前 R3.2 HTTP E2E PASS。当前运行快照见 `D:\study\NetConsole-Next-Workspace\reports\r3.2-blocker-runtime-20261115-004027\runtime-state.json`。

