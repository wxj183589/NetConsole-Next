# P2B5 Rail Base Data Boundary and Acceptance Plan

日期：2026-10-27  
Branch：`codex-A/next-device-management-rail-base-boundary-20261027`  
基线：Export boundary `0fa13da`  
状态：Draft；仅冻结轨交基础数据边界，未写入业务数据

## 本阶段结论

当前没有经过授权、可追溯且可复核的线路/区段/车站/设备基础数据源可供导入。因此本阶段只定义 reference data contract 和导入门槛，不写入任何站点、设备、IP、VLAN 或现场数据。

## 数据模型边界

- reference 层只描述 `line`、`section`、`station`、`site`、设备角色和可选标签；它不替代设备 inventory、planned address、observed fact 或 review 状态。
- 每条记录必须携带稳定 ID、来源名称、来源 revision、effectiveAt、capturedAt、schema version、content hash 和审核状态。
- 站点关系必须明确为 `line -> section -> station -> site`，跨线路或跨站点冲突返回 conflict；不得依靠名称模糊匹配、站点别名猜测或硬编码 `hzl10`。
- 设备与车站关联必须使用显式 approved identity（稳定 ID、受控 MAC/serial claim 或人工审核映射）；仅凭 IP、设备名或字符串相似度不能建立关系。

## 导入与变更门槛

- 首次接入必须先做 dry-run/preview，显示新增、更新、冲突、未知引用、删除候选和未覆盖字段；没有显式 confirm 不写入。
- revision、source hash、scope 或 schema 不匹配时拒绝导入；不把旧版本数据自动覆盖新版本。
- 删除和移动必须是可审计的显式操作，保留前后值、actor、reason、operation ID 和 rollback 线索。
- 数据源缺失、重复 ID、同名多义、站点范围不可验证时，状态为 `RAIL_BASE_DATA_PENDING` 或 `CONFLICT`，不生成推断结果。

## 与网络规划的关系

- 网络规划 UI 可以显示 reference label，但 planned/observed address、Network Domain、VLAN 和 review 仍由 P2B v13/v14 契约管理。
- 轨交 reference data 不得触发设备采集、SSH/Telnet/COM 连接、配置写入、management_ip 更新或 schema migration。
- 导出必须引用固定 revision；reference 变更后旧导出继续指向原 revision，不静默重算。

## 安全与数据边界

- 不访问 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`，不复制完整 Site/数据库，不连接真实设备。
- 不把真实车站清单、现场凭据、Host Key、原始命令或生产拓扑作为 fixture 提交。
- 任何未来真实导入必须有可信 actor/site claim、来源授权、字段字典、冲突处理方案和隔离回滚验证。

## 验收声明

本 Draft 的完成标准是 reference boundary 可审查、身份规则拒绝模糊匹配且不产生副作用。它不代表轨交基础数据已导入，不代表杭州 10 号线或任何现场数据已验证，也不代表真实设备或 VLAN 规划已完成。
