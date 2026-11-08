# R7.3 故障中心

2026-11-08，codex-A，接续基线 `220c979`。本专项结论 **PARTIAL**，实现与定向合成验证通过，真实模拟器故障与长期运行由主集成另行记录。

## 已实现

第一批将LLDP邻居消失接入现有故障中心。查询当前页授权设备的同一连接档案，独立读取每个来源/捕获范围/邻居身份的最后观测，再取最多1000个身份，以及1000条Current完整表。只有Raw验证、同profile、同source authority、同captured scope、当前且新鲜的PARSED完整表，才可对照历史的精确本地接口/远端端口/chassis身份产生 `LLDP_NEIGHBOR_LOST`。空完整表可证明此前邻居消失，partial、分页、未验证、UNSCOPED、未来或过期表均不能产生该结论。范围授权仍复用Catalog冻结scope谓词，当前库存端点或Site变更不会重标旧事实。

事件复用持久OPEN/ACKNOWLEDGED/PROCESSING/RESOLVED/REOPENED生命周期、去重、统计、历史及CSV接口。恢复必须由更新、同authority、同捕获范围的完整Current表重新包含相同邻居身份证明。后台有限补采策略对该类事件额外读取允许的LLDP表，默认策略仍关闭，成功连接和任务入队不会直接解除故障。

UI新增邻居消失筛选和事件来源/捕获范围展开。CSV在旧字段之后追加 `source_authority`、`captured_scope_key`，事件与每条历史均保留分区来源，原脱敏和公式保护继续适用。

第二批修复健康指标对完整空LLDP表显示UNKNOWN的问题。新鲜且Raw验证的完整空表显示带表事实引用的OBSERVED 0。缺失、partial、未验证、过期或损坏表仍保留UNKNOWN，邻居数量仅代表被观测条目。

## 定向测试

| 检查 | 数量 | 实际范围 |
| --- | --- | --- |
| OperationalHealth | 15/15 | LLDP精确分区/空表/同邻居、非法证据、旧健康与路由/重试回归 |
| FaultCenter | 23/23 | 精确恢复、不同来源/范围拒绝、重复故障、持久生命周期及既有策略回归 |
| H3cTaskFactPublication | 20/20 | 正常scheduler、合成transport、真实Raw落盘/hash、Facts、故障Sync/去重/恢复、服务对象重建及复发 |
| FaultCenter Vue | 16/16 | 真实组件、mock API，新增筛选、来源展示、CSV分区字段及既有脱敏/权限/生命周期 |
| Web typecheck | PASS | vue-tsc |
| Release build | PASS | 独立 `build/r73-fault`，0警告0错误 |

这些检查使用Next Workspace的最小fixture和合成transport，未连接模拟器。没有启动第二个Service进程。HTTP、真实浏览器、完整回归与实际运行时长以主集成验收为准。

## 剩余

历史身份查询有界，超过1000个独立来源/捕获范围/邻居身份仍可能遗漏消失检测。设备初次出现的空表缺历史对照时不会制造故障。跨来源独立观测分别保留，不能据邻居消失判断设备离线或无线业务故障。配置变化、外部Syslog全身份关联、全部AC状态变化、外部通知及长时无人值守仍未完整迁移。

本专项不读取旧真实数据，不执行设备配置写入、链路故障注入或修改系统时间。原版只读参考25项迁移矩阵与 `src/netconsole/services/ground_unattended/syslog_runtime.py` 的持续接收/有界故障处理，完整专业业务仍有差距。

## 对抗性复核

发现原先最近1000条revision会被高频邻居挤占，导致另一邻居的最后基线遗漏。新增故障专用Catalog查询，按profile、identity、authority和capturedScope选择各自最新版本后再限1000身份，原通用历史API语义保持。1100条高频revision回归证明旧基线保留，并验证另一Site不可读。新增文件 `LldpFaultHistory.cs`。定向Publication20/20、Operational15/15、Fault22/22通过。

过期、partial、未来、UNSCOPED、不同authority/scope、损坏payload和不同邻居identity均不能创建或解除该事件。邻居更换本地口或远端口会保留旧精确链路的消失事件，新链路不作为旧链路恢复证据。此事件是观测的链路变化提示，不能据此声明设备离线或无线业务故障。Catalog的冻结scope谓词按当前设备、profile、Site、source和端点验证历史访问。

## 第三批采集失败资源分区

实际发现同设备不同命令计划失败仍以device作为ResourceKey聚合，后一次失败会覆盖前一种失败的来源证据，恢复核验可能只解除后一次计划而掩盖另一未恢复计划。本批以任务类型、完整有序命令、Target及冻结device/profile/Site/host/port生成稳定collection-plan SHA256资源键，每种采集计划分别聚合。临时definition ID与采集时间不改变资源键。新键记录验证恢复前重新核对原定义，定义变更不重新解释原故障。

历史device聚合事件保持原有ID、resource、状态与历史，不自动迁移、合并或解除。新增正常Service Sync回归保留旧事件，version与LLDP失败生成两项，一次version成功仅解除对应项，另一个计划继续未恢复，重复Sync不新增事件。FaultCenter最终23/23通过，Operational15/15与Publication20/20回归通过。

配置变化事件没有仓促接入。既有完整备份manifest虽有前后hash与设备/Site/端点，但缺历史冻结SourceId与明确authority，不能直接视为同来源配置变化。新增配置authority及其兼容合同需与配置工作流单独闭环，当前仍保留此缺口。
