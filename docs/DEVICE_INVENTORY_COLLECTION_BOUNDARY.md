# Device Inventory / Terminal / Collection 边界

Phase 1 将设备资料、终端连接配置、采集源许可和任务运行状态明确分层。库存 CRUD 不应通过“方便同步”改变其它层的生命周期。

| 层 | 事实字段 | 含义 | Phase 1 设备资料操作是否修改 |
|---|---|---|---|
| 资料层 | `device_inventory.enabled` | 是否允许该设备作为库存资料被管理和展示 | 是，仅修改本表 |
| 终端层 | `h3c_device_profiles.enabled` | 终端连接 Profile 是否允许进入终端/采集 admission | 否 |
| 采集源层 | `collection_sources.enabled` | Source 是否允许采集调度使用 | 否 |
| 定义层 | `task_definitions.enabled` | 已配置任务是否允许排队 | 否 |
| 运行层 | `task_runs.state`、`terminal_sessions.state` | 某一次任务或会话的实际生命周期 | 否 |

## 规定

1. `device_inventory.enabled=false` 不等于设备离线，也不等于终端 Profile 禁用。
2. Terminal session disconnected 不等于物理设备离线；没有实时探测时显示 `UNKNOWN` / `未检测`。
3. 库存停用不会取消已排队、连接中或已完成的任务；采集与终端策略由各自 API/调度器判断。
4. 库存重新启用不会恢复管理员此前停用的 Profile 或 Source。
5. 设备重新关联 Profile 只更新 `terminal_profile_id`；旧 Profile 的 enabled/source 状态保持不变。
6. 既有 session 的 `target_id`、terminal recording、Raw segment、Catalog 索引不因库存编辑而删除或重写。

## Legacy bridge

v11 migration 和旧 `CreateH3cDeviceProfile` API 为历史 Profile 提供库存投影，但只在站点/IP 尚无库存行时创建 bridge。多个 Profile 指向同一站点/IP 时保留 Profile，库存只保留一个可管理投影，避免把 Profile 数量误报为设备数量。

GET `/api/system/devices` 和 detail 是只读查询，不再调用隐式 bridge writer。新增/更新库存通过显式 CRUD 或 CSV transaction 写入并审计。

## Phase 2 方向

若未来需要“停用设备即停止采集”，必须设计显式的受控动作或策略字段，记录 actor、原因、影响到的 Profile/Source/Task，并解决运行中任务的 cancel/admission race；不能恢复 Phase 1 的隐式联动。
