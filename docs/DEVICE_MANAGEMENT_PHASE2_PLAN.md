# NetConsole Next 设备管理 Phase 2 计划

状态：`PHASE2_PLAN_ONLY`
本文件只定义拆分、边界和验收条件，不实施 Phase 2 业务代码。

## 目标与不做事项

Phase 2 目标是把设备资料、连接 Profile、采集 Source、任务、站点/IP/VLAN 和审计的关系做成可审查的业务边界，继续复用现有 .NET SSH/终端运行时。

本阶段不新增完整轨道交通业务、不引入 Python 采集运行时、不做全局 Vendor Adapter Framework、不开放 LAN 管理、不修改生产 SQLite、不连接真实设备。

## 领域模型

```text
Site 1---N DeviceInventory
Site 1---N DeviceGroup
DeviceInventory 0..1---1 TerminalProfile
TerminalProfile 1---1 CollectionSource
DeviceInventory 0..N---N DeviceAddress (Phase 2)
DeviceAddress 0..1---1 VlanPlan (Phase 2)
TerminalProfile 1---N TaskDefinition
TaskDefinition 1---N TaskRun
TaskRun 0..N---N RawSegment / Catalog record
Any mutation 1---N AuditEvent
```

`DeviceInventory` 是资料身份；`TerminalProfile` 是连接能力；`CollectionSource` 是调度许可；`TaskRun` 是一次运行。四者不得通过一个 `enabled` 字段互相隐式覆盖。

## API 草案

- `GET /api/system/devices`：分页、排序、搜索、site/group/status filter。
- `POST/PUT /api/system/devices/batch`：提交批量资料变更计划，返回 operation ID。
- `POST /api/system/devices/batch/{id}/confirm`：显式确认并执行。
- `GET /api/system/device-operations/{id}`：进度、成功、冲突、失败和可重试状态。
- `GET/POST /api/system/device-addresses`：管理地址/业务地址/VLAN 资料，不触发连接。
- `POST /api/system/devices/export`：权限审计后的脱敏导出；Excel 公式安全编码。
- `POST /api/system/devices/import/preview|commit`：沿用 Phase 1 token/版本/原子事务边界。

批量编辑、批量启停、批量归组、批量改局点和大批量导出进入后台 operation；小规模单行资料更新可同步完成。任何会影响采集或终端的动作必须是独立 operation，并显示影响范围。

## Schema 扩展方案

建议新增而非改写 Phase 1 核心表：

- `device_addresses(device_id, scope, address, family, vlan_id, valid_from_ms, valid_to_ms, revision)`；`scope` 区分 MANAGEMENT/BUSINESS。
- `vlans(site_id, vlan_id, name, purpose, enabled, revision)`；VLAN ID 只在站点内解释。
- `device_operations(id, actor, kind, state, request_json, progress_json, created_ms, updated_ms)`。
- `device_operation_items(operation_id, device_id, before_json, requested_json, outcome, detail)`。
- `device_audit_events` 或现有 audit_events 的结构化扩展：actor、action、subject、result、safe_changed_fields、correlation_id。

迁移必须：新根初始化、v11/Phase 2 重启、失败重启恢复、重复 migration、外键检查和历史重复 IP 报告均可验证。不得自动猜测历史 VLAN、线路、车站或设备身份。

## 权限与审计

- ADMIN：默认读；不能写设备资料、Profile、Source、批量 operation。
- SUPERADMIN：可提交设备资料和导入；高影响 operation 需要显式确认。
- Local API bearer：沿用本地服务边界，不作为浏览器 Cookie 替代。
- 所有写操作需要 Cookie Origin+CSRF 或既有 bearer 边界。
- 审计禁止明文密码、Token、私钥、Cookie 和凭据密文；失败、拒绝、冲突也应有安全摘要。

## IP/VLAN 与轨交衔接

地址唯一性始终以 `site_id`、地址族和地址作用域解释；不同局点可以复用相同地址。不能假设所有线路共用 VLAN 规划，也不能把管理地址等同于业务地址。

Phase 2 只预留 `line`、`station`、`section`、`station_switch`、`wireless_controller`、`trackside_ap`、`onboard_device` 的外部 reference/标签边界，不新增完整轨交业务表。轨交基础资料需另立 PR 并先给出来源 authority、生命周期和冲突策略。

## H3C 与多厂商边界

第一拆分只补 H3C Comware V5/V7 的能力声明、Profile 能力和采集结果映射；复用现有 .NET SSH、Host Key、Raw/Catalog 和受控命令策略。ZTE/Huawei 只形成 capability gap 清单，不在本计划创建全局 adapter framework。

## PR 拆分与依赖

1. `P2-A`：批量 operation、冲突模型和审计事件；依赖 Phase 1 review closure。
2. `P2-B`：多地址/VLAN 资料模型与 migration；依赖 P2-A 的 operation/audit contract。
3. `P2-C`：H3C capability/source/task 显式关系；依赖 P2-B 的 address/site scope。
4. `P2-D`：脱敏导出、CSV 版本化和大批量进度；依赖 P2-A/B。
5. `P2-E`：轨交 reference boundary；依赖上述模型稳定，只做基础资料接口，不接现场采集。

每个 PR 独立 CI、独立隔离数据、独立 migration/restart/rollback 测试；不把全部设备业务合成一个大 PR。

## 验收条件

- 批量操作可预览、确认、取消、重试，冲突不产生部分写入。
- 并发编辑使用版本条件；旧 preview 不能覆盖新修改。
- Site-scoped IP/VLAN 唯一性与历史重复诊断明确。
- Inventory、Profile、Source、TaskRun 状态可分别解释，状态不会隐式联动。
- ADMIN/SUPERADMIN/CSRF/Origin/本地 bearer 边界有 API 与真实浏览器证据。
- Raw/Catalog/terminal history/recording 不因资料维护被删除或重写。
- 0/100/1,000/10,000 条合成数据分页、筛选、导出和 preview 有可重复性能证据。
- 真实设备、生产数据、正式部署和现场签署仍需单独授权与验收。
