# R7.2 原版功能迁移与运维 UI

2026-11-08，codex-A。分支 `codex-A/netconsole-next-r3-dual-ac`，本轮接续基线 `065a749` 加既有 R7.2 未提交工作树。整体状态 **PARTIAL**。

## 核对范围

基准沿用 [25 项迁移矩阵](../r6-feature-parity/ORIGINAL_TO_NEXT_FEATURE_MATRIX.md)，逐项区分已实现子链、现场证据和完整迁移语义。本次只读核对原版 `src/netconsole/services/ground_unattended/quick_ping.py`、`config_lifecycle_service.py`，以及相关 Mesh、Syslog 源码位置。旧真实数据根未读取，原版源码未修改。

原版 Quick Ping 复用 fping v5，具备独立运行、目标停止、7 天时间预算、分段压缩和丢包历史。Next 当前有界采样与跨任务历史聚合保持独立范围，长期无人值守、完整保留策略及车队业务仍开放。

## 本轮产品增量

- 工具页新增 Quick Ping 历史工作台，通过现有真实诊断历史 API 明确加载最多 20 个任务，最多 4 个并发只读请求。按执行账号、Site、设备 UUID、协议、目标 IP 与端口分别统计。RTT 从已回复原始样本计算加权平均，丢失率分母为已发送样本，未发送、缺失身份、异常快照均保留未知。可按 Site、设备和账号筛选并导出含范围及失败任务元数据的 CSV。
- 故障中心支持 PROCESSING 与 REOPENED，统计覆盖当前筛选匹配的全部授权记录。后台每轮有界核验 25 个事件，避免早期阻断接口让后续资源长期饥饿。重试只接受明确瞬态错误，保持每轮预算和最短间隔。
- 修复故障恢复误判。COLLECTION_FAILURE 只能由相同任务类型、完整有序命令计划及冻结设备、Site、profile、端点的后续完整成功任务恢复。版本任务成功无法解除 LLDP 采集失败。缺失原任务定义时保留未核验状态。
- 故障类型筛选补充 TRACEROUTE_ANOMALY，对接真实已有后端事件。路由观测保持目标、Site、新鲜度、终态与跳点证据检查。
- 设备详情增加精确 LLDP 邻居及来源信息，拓扑按 authority 和捕获范围隔离。无线采集支持实际八列 Radio 输出，展示 Down 和零客户端。以上专项证据见 [来源范围报告](R7_2_FACT_SOURCE_SCOPE_REPORT.md) 和 [设备互操作报告](R7_2_DEVICE_INTEROP_REPORT.md)。
- 配置页增加逐设备/批量恢复准备检查，LocalManager 提供单入口运行管理，分别见配置恢复及 Windows 部署专项。

## 25 项迁移状态复核

| 功能范围 | 已实现且可操作的子链 | 完整迁移剩余内容 |
| --- | --- | --- |
| 设备管理、设备连接、设备采集 | 库存 CRUD、凭据复用、批量连接、任务 Raw/Facts、详情能力 | 全厂商/现场型号覆盖及 FIT 原生认证 |
| SSH/Telnet、跳板 | 现有终端、录制回放、Site 跳板配置与测试 | 真实跳板拓扑、全部 Telnet 交互、跨机验收 |
| 接口、VLAN | 接口观测和健康状态、VLAN 规划及事实 | 接口编辑/流量错误趋势、通用真实 VLAN Writer |
| LLDP、拓扑 | Current 来源/范围隔离、精确筛选、邻居详情、CSV | 全现场身份消歧、图布局、跨站及长期变化分析 |
| AC、FIT、AP Group | 双 AC 独立 Observation、身份与角色、Radio、组与成员 | 受控 HA/回切、无线业务恢复、FIT 原生认证、组完整生命周期 |
| Mesh | 通用命令和部分事实入口 | 专项链路时序、质量/掉线分析和报表 |
| Ping、Quick Ping | 有界多设备诊断、取消、历史、样本及跨任务汇总 | 长期无人值守、fping 压缩保留、车队/IP占用语义 |
| Syslog | UDP Raw、查询、分类、过滤、原文和 CSV | 设备发日志、全 payload/身份关联、长稳负载 |
| 配置备份、配置恢复 | 完整备份、批量、计划、历史、比较、准备检查和阻断原因 | 原生整文件替换及真实恢复/回滚五阶段验收 |
| 智能配置 | 模板预览和变量校验 | 批量真实 Writer、失败恢复和回滚 |
| 批量任务、任务历史 | 有界队列、并发、取消、任务历史/Raw/诊断导出 | 全任务种类一致语义、长期历史、未知远程结果后的恢复 |
| 导入导出、报表 | 明确格式设备/规划导入及 CSV/ZIP/配置导出 | 全部 Excel、Mesh/MR 业务格式和专业报表 |
| 故障分析、设备状态监控 | 持久生命周期、范围统计、同资源恢复核验、有界补采、健康观测 | 离线/HA/LLDP/配置/Syslog 全事件关联、现场自动恢复、通知、长时自治 |

全部 25 项继续标记 PARTIAL，具体子链的测试 PASS 单独记录。整机恢复、HA、FIT 或 SCM 的外部阻断保留在专项报告。

## 本接续批验证

- Web 定向 `FaultCenter.test.ts`、`QuickPingHistoryTrend.test.ts`、`quickPingTrend.test.ts`、`ToolsView.test.ts` 共 50/50 通过，typecheck 通过。其中新增路由筛选正常 API 参数回归，修正 Quick Ping 测试 ApiRequestError 参数类型。
- 故障中心前一轮 20/20 与 OperationalHealth 13/13 通过。随后增加正常 Service 入口的“版本成功无法解除 LLDP 失败”回归，最终总数见主集成测试证据。
- 集成首次发现路由健康测试的设备 fixture 仅填 ManagementAddress，遗漏规范 ManagementIp。修正 fixture 后原精确资源、新鲜度及异常结果断言全部通过，产品范围检查保持一致。
- 浏览器、HTTP、完整 Service/Web/Worker、持久化及运行健康结果由主集成 [测试证据](R7_2_TEST_EVIDENCE.md) 统一记录。此处组件结果仅用于对应代码范围。

本工作流未执行设备写入，未触发新网络探测，未访问旧真实数据。目录和 Git 集成由主代理统一交接。
