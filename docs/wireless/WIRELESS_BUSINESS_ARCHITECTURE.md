# 无线业务 AC/AP 架构

状态：`DESIGN_READY`，本轮只冻结业务边界、身份模型、关系和 UI 契约；无线采集、数据库迁移和运行时投影均为 `NOT_STARTED`。

## 1. 目标与非目标

无线业务在 NetConsole-Next 中分为两层：

1. **设备层**：AC 是设备库存中的一种网络设备，沿用 `device_inventory.id`、厂商、管理地址、Site Scope、Terminal Profile、凭据引用、审计和权限规则。
2. **业务层**：FIT-AP 是由 AC 观测到的无线业务实体。它拥有自己的稳定身份、主 MAC、序列号、AP 组和跨 AC 观测记录，但不复制一份独立的 AC 设备库存。

本轮不实现：

- H3C AC/AP 采集器、SSH/Telnet 业务命令和真实无线数据同步；
- 现有数据库的完整迁移或回填；
- 将推测的 H3C `R/M/RB/I` 状态直接当成生产真值；
- 通过名称、IP 尾段、AP 组名或 AC 名称相似度自动合并实体。

## 2. 业务对象边界

| 对象 | 权威来源 | 稳定键 | 说明 |
| --- | --- | --- | --- |
| AC | Device Inventory | `device_inventory.id` | 一个受 Site Scope 保护的设备记录；无线扩展只引用它 |
| AC 成员/集群 | 无线关系层 | `ac_device_id + cluster_id` | 描述独立、堆叠、集群或主备成员，不改变 AC 设备身份 |
| FIT-AP | AP Identity Registry | `fit_ap_id` | 物理 AP 的业务身份；优先用确认过的序列号和主 MAC 对齐 |
| AP 观测 | AC 采集结果 | `observation_id` | 保留来源 AC、原始字段、原始状态、观测时间和版本 |
| AP 组 | AC 业务配置 | `ac_device_id + name` | 组名只在所属 AC 内唯一，不能作为 AP 身份 |

设备列表、设备详情和设备编辑页面只展示 AC 的设备库存身份。无线页面通过 `ac_device_id` 跳转回设备详情，并显示“来源 AC / 观测状态 / 身份冲突”而不是再建一套设备主数据。

## 3. 关键不变量

- 任何 AC/AP 业务记录都必须带 `site_id`；服务端先执行用户的 Site Scope，再执行资源权限。
- `ac_device_id` 必须外键到设备库存，且设备类型/能力校验不能被前端绕过。
- AP 以序列号、确认过的主 MAC 建立身份；序列号和 MAC 不一致时进入冲突/隔离，不自动覆盖。
- 同一 FIT-AP 可以有多个 AC 观测，但同一观测只能归属于一个来源 AC、一个 Site 和一个采集修订版本。
- AP 组名、管理 IP、连接顺序、表格行号、名称相似度不能单独产生合并关系。
- 当前状态必须可追溯到原始状态、来源 AC、观测时间、状态语义版本和解析规则版本。
- AC 离线不等于 AP 离线；过期观测只能产生 `STALE`，不能凭空生成新的 AP 状态。
- 任何跨 Site 引用、跨 Site 分组或未授权凭据使用均由服务端拒绝并记录审计。

## 4. 运行时数据流

```text
Device Inventory (AC)
        │ ac_device_id
        ▼
AC adapter / collector  ── raw observation + source revision ──► observation store
        │                                                          │
        │                                                          ▼
        └────────────── identity reconciliation ◄──────── AP Identity Registry
                                                                   │
                                                                   ▼
                                                        state projection / conflict view
                                                                   │
                                                                   ▼
                                                    Site-scoped wireless UI and audit
```

采集器只能写入其授权 Site 的观测；身份合并和状态解析必须是可重放、可解释的服务端过程。UI 不直接拼接 AC 返回的 AP 行，也不把一次采集结果当成永久主数据。

## 5. 未来接口契约

设计阶段建议提供以下资源，而不是让 UI 访问采集器内部结构：

- `GET /api/wireless/acs?siteId=...`：返回设备库存中的 AC 摘要及能力/在线信息；
- `GET /api/wireless/acs/{acDeviceId}/groups`：返回该 AC 的 AP 组；
- `GET /api/wireless/aps?siteId=...&acDeviceId=...&groupId=...`：返回身份解析后的 AP 及观测摘要；
- `GET /api/wireless/aps/{fitApId}/observations`：返回跨 AC 来源与原始字段；
- `POST /api/wireless/aps/{fitApId}/identity-confirmation`：仅有相应 Site/身份管理权限时确认冲突或替换；
- `POST /api/wireless/observations/{observationId}/reconcile`：重放指定观测，不允许通过请求体写入未经验证的身份。

所有接口应返回 `sourceRevision`、`observedAt`、`stateSemanticsVersion`、`conflictState` 和 `siteId`，以便 UI 不隐藏数据质量边界。

## 6. 权限模型

读取需要同时满足：用户可见 Site + 无线业务读取权限。创建/替换 AP 身份、确认冲突、修改 AP 组或使用 AC Terminal Profile，需要额外的设备/凭据/无线写权限。凭据本身仍由现有安全存储管理，AP 页面永不返回密码或内部 Credential Reference。

## 7. 假设与证据边界

本仓库当前没有足够的真实 H3C AC 多控制器、堆叠/主备和 FIT-AP 状态采集证据。因此 `R/M/RB/I` 的含义只在 [`MULTI_AC_STATE_RESOLUTION.md`](./MULTI_AC_STATE_RESOLUTION.md) 中作为待验证假设保存；实施前必须用受控的真实 H3C 输出、命令版本和时间语义补齐证据。
