# R3 Wireless UI Read-only Preparation

## 范围

本页只做只读产品/API 盘点，不实现 R3 无线页面，不修改 AC/AP 数据模型、采集器或 SmartHub。

## 当前状态

- AppShell 中 `/wireless` 导航明确标记为 disabled，点击提示“无线管理尚未接入真实业务 API”；对应路由使用 DisabledView。
- 当前已有设备管理把 AC、FAT_AP、普通网络设备作为库存类型边界；FIT_AP 不提供独立创建入口。
- 已有 H3C/ZTE profile、事实回放和离线 fixture 能力，但没有无线专用 AP inventory、AC↔AP association、AP group 或 R/M/R/B/I 状态 API。
- 现有 H3C facts 可作为后续事实来源，但不能从通用 facts API 推断无线 UI 已完成。

## R3 业务导航建议

```text
无线业务
└─ AC
   ├─ 概览
   ├─ AP 列表
   ├─ AP 组
   └─ 关联状态
```

AC 仍是设备管理中的普通设备；无线业务以 AC 为导航，FIT_AP 跟随 AC，同一 FIT_AP 可以在多个 AC 视图出现，但必须由稳定序列号/MAC 身份去重和关联，而不是名称模糊匹配。

## 后续所需 API 合同

1. **授权 AC inventory**：按当前用户 Site claims 返回可见 AC、默认 AC、Site、claims revision 和分页/筛选信息。
2. **AC/AP facts**：AP 稳定 serial、规范化 MAC、型号、版本、管理地址、来源、采集时间、完整性/新鲜度和不确定性。
3. **AC↔AP associations**：当前关联、历史关联、来源 AC、关联 revision、冲突/多 AC 结果和事实 provenance。
4. **AP groups**：AC 下 AP 组列表、成员、组状态和服务端授权边界。
5. **无线状态**：R/M、R/B、I 原始状态与规范化枚举；R/M 作为当前有效主运行状态的优先依据，同时保留原始证据。
6. **拓扑/部署形态**：堆叠、集群、1+1、独立多 AC 的角色和成员关系，不能用单一 AC 假设覆盖。
7. **查询能力**：按 Site/AC/AP 组/serial/MAC/状态分页、过滤和稳定排序；跨 AC 查看必须返回明确的授权与 provenance。
8. **任务与审计**：刷新/离线回放/采集任务状态、失败原因、原始证据引用和操作审计。

## 后端阻断

- 设备库存 CRUD 当前没有统一接入 P2B5 Site claims；R3 无线授权 API 不能直接假定设备管理的 `siteId` 查询已安全。
- 当前没有无线专用 schema/API 组合，也没有为多 AC 复用 FIT_AP 建模的已验证合同。
- 真实 AC/AP、堆叠/集群/1+1 和 R/M/R/B/I 运行期证据均未在本轮访问。

## 建议进入条件

先独立冻结授权/身份/事实字段和 AC↔AP association 合同，再以离线脱敏 fixture 实现 R3 页面；完成服务端 Site claims 与稳定 identity 回归后，才进入真实无线 UI 实现。当前结论为 `R3_WIRELESS_UI_READONLY_PREPARATION=PARTIAL`，不是 R3 implementation ready。
