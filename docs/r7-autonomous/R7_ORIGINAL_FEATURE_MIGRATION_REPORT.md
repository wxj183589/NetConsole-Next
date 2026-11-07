# R7.0 原版功能迁移报告

更新时间：2026-11-08。状态：**PARTIAL**。R7.0 已实施高价值产品代码并验证部分真实业务链；没有完成原版全功能替换。本报告覆盖请求的25项，完整逐项原版源码 / Next API / UI / 测试 / 可操作边界位于 [原版到Next迁移矩阵](../r6-feature-parity/ORIGINAL_TO_NEXT_FEATURE_MATRIX.md)，当前状态见 [功能迁移状态](../r6-autonomous/FEATURE_MIGRATION_STATUS.md)。

## 审计范围与依据

- 原版：`D:\study\NetConsole-Workspace\NetConsole` 的源码 `src/netconsole`、`apps/desktop_renderer/src`；只读，不启动、不连接旧数据。
- Next：`D:\study\NetConsole-Next-Workspace\NetConsole-Next`；分支 `codex-A/netconsole-next-r3-dual-ac`，R7起点 `2551b26`。最终提交由主集成记录，不在并行文档中猜测新SHA。
- 原版已读入口：device/ac/config/network/job router，`config_lifecycle_service.py`、`site_ssh_relay.py`、`ground_unattended/quick_ping.py`、`syslog_runtime.py`、QuickPingPage、SyslogPage及专业报表源码路径。功能名称不能证明完整执行链：本轮未从配置生命周期源码定位任意历史整机恢复或独立智能配置模板执行API。
- Next按实际 `Program.cs` 路由、moduleRegistry、Service/Vue和测试入口核对。API存在、fixture通过、真实设备成功和最终浏览器验收分别记录。

## 25项覆盖索引

| 请求项 | 当前可用范围 | 主要欠缺 |
| --- | --- | --- |
| 1–4 设备管理、连接、SSH/Telnet、SSH中转 | 库存/作用域/批量/详情、Netmiko、WebShell、Linux独立入口与Site跳板 | 全现场协议/型号、真实Linux与中转验收 |
| 5–9 信息采集、接口、VLAN、LLDP、拓扑 | Raw/Facts/Current、设备能力、接口/LLDP查询与健康、VLAN规划/整表、只读资产工作台 | 完整设备配置管理/性能趋势、图形拓扑和长期关联 |
| 10–13 AC、FIT-AP、AP Group、Mesh | Domain/AP Resource/独立Observation、Current/PreviousEffective/Recent10、组来源和任务历史 | HA切换、FIT原生认证、生命周期权威、Mesh专项分析/报表 |
| 14–16 Ping、Quick Ping、Syslog | 有界诊断和采样；Syslog后端UDP/Raw基础 | 长期fping语义未迁移；Syslog无Vue viewer且本轮监听端口0 |
| 17–19 备份、恢复、智能配置 | 脱敏批量计划、完整私有单设备、比较/预览、检查点状态机、模板dry-run | 私有批量/定时、整机恢复、真实replace、智能配置执行 |
| 20–22 批量任务、任务历史、导入导出 | 有界TaskPlan、Run/Command/Raw、重试、任务/无线ZIP、CSV/规划 | 全任务类型/专业格式与长期策略 |
| 23–25 故障分析、报表、状态监控 | 证据健康/事件、受控只读补采、统计与具体导出 | 全域故障诊断、通知和业务恢复、专业报表、长期趋势 |

## 本轮实际产品增量

1. `NetworkQuickPing.cs` 与既有诊断服务/UI整合：ICMP或TCP连续采样、每次结果、成功/丢失/时延统计、取消和当前目标复核。复用授权库存/诊断历史，没有引入第二套网络设备协议。
2. `OperationalHealthService.cs` 和 `OperationalHealth.vue`：从授权Current Facts、Raw、任务、诊断与无线状态产生健康/事件；缺失、过期、fixture、未来或不完整证据不伪装实时指标。用户显式选择设备后最多3次/120秒连接探测；认证失败不重试，成功仅提交既有版本/接口只读补采计划。
3. `H3cOperationalCapabilities.cs`、设备详情和无线页面：显示型号/版本、读能力、来源/证据/时间、新鲜度，区分UNKNOWN/VERIFIED/STALE。设备详情真实API替换旧“没有API”占位，切换设备/Site/组/权限/关闭/卸载后旧响应失效。FIT不作为独立新建设备。
4. 完整配置按上下文比较、敏感变化提示、任意两份历史对比/脱敏导出、实时兼容性恢复预览；原生检查点验证状态机必须有唯一新增ID与已验证transport才能继续。授权SW实际上commit无效果，链路在任何描述写入/replace前阻断。
5. `LocalDeploymentDoctor.cs` 和LocalManager doctor：受管根/SQLite实例身份/loopback health/PID/Raw/Netmiko close握手检查；SCM start/stop过渡状态等待。源码运行通过不能代表Windows安装/升级/跨机部署已验收。

## 不能合并的产品语义

**Quick Ping**：原版 `quick_ping.py` 使用fping v5，`duration_seconds=None`表示不限时，定时上限7天，且有进程身份、IP占用、分段压缩和保留治理。Next是.NET ICMP/TCP的有界诊断：2–60样本、最多8目标、单目标预算不超过120秒、间隔和超时100–5000ms。真实TCP三次成功证明服务主机TCP探测可操作，不能称原版长期fping已迁移，也不能把TCP时延写成ICMP。

**Syslog**：原版有 `SyslogPage.vue` 与事件解析/运行态。Next有 `UdpSyslogWorker`、有界队列、Raw基础和health启用状态；本轮运行配置端口0，未监听，moduleRegistry没有独立Syslog Vue viewer。后台能力/单元测试不等于工程师能收取、筛选、查看真实日志。

**配置备份**：原有ConfigBackupService是脱敏备份链，提供批量与定时；完整私有备份保存原件用于hash/验证，当前为单设备受控采集。公开下载重新校验后返回脱敏副本，不能当完整恢复原件。现有脱敏批量/Site/Domain/计划入口不能证明完整私有原件同样支持。

**恢复和智能配置**：受控接口描述变更/恢复不是任意整机恢复。完整历史预览始终`writeAllowed=false`，`FULL_DEVICE_RESTORE_NOT_IMPLEMENTED`；SW原生commit不产生检查点，`NATIVE_CHECKPOINT_NOT_CREATED`，replace交互未验证且transport禁止。写入返回成功、全配置读回、关键接口/LLDP验证、失败回滚必须分开，不能互相替代。SmartConfig是两模板`DRY_RUN_ONLY`，校验后不连接、不写入设备。

**无线与故障**：AC1/AC2均Common、同AP的R/M与R/B仅为独立设备报告角色；未取得HA切换与业务恢复证据。FIT AC托管身份/历史可用，原生Telnet缺可用凭据。健康事件为有界证据投影，没有完整事件历史库；正常连接/补采成功不代表真实宕机后的业务恢复。

## 测试与实际证据

- 本子工作流设备详情：`DevicesView.test.ts` 13/13、typecheck PASS；覆盖真实DTO展示、UNKNOWN/空态、加载/失败/重试、错profile拒绝、设备/Site/权限变更后旧响应丢弃。
- 能力投影 targeted PASS；LocalManager测试10/10，实际源码doctor EXIT0。SCM脚本语法PASS，安装/崩溃恢复NOT_TESTED。
- 健康定向7/7、Web健康5/5及真实浏览器AC只读连接/补采由B报告记录。完整比较10/10、checkpoint14/14和配置Vue10/10由A报告记录；fixture成功不提升真实恢复状态。
- `reports/r70-device-20261107-233336`：两AC真实身份、Common、R/M/R/B；`reports/r70-sw-checkpoint-20261107-234349`：commit后无检查点、配置hash相同；`reports/r70-sw-checkpoint-20261107-234525`：真实接口/LLDP。
- 浏览器初轮设备详情因旧占位失败，修复后等待主集成新部署复验；初轮失败证据保留。最终全量Web/Worker/Service/HTTP/浏览器/Release构建、最终PID和Git结果以 [R7最终验收](R7_FINAL_ACCEPTANCE.md)、[UI验收](R7_UI_ACCEPTANCE_REPORT.md) 为准。

## 下一优先级

P0：完成最终新部署/doctor/浏览器验收，保持恢复失败封闭和正确UNKNOWN边界；实际支持的checkpoint/replace设备到位后才继续整机恢复。P1：完整私有备份批量/Site/Domain/定时；Syslog受权查询/筛选/原文viewer；定义长期Quick Ping的保留/资源/终止合同后迁移。P1外部依赖：可用FIT凭据、真实HA支持和现场业务验证。P2：Mesh/MR专业分析报表、长期健康趋势/通知、图形拓扑和剩余原版业务。

本审计与文档更新不访问或复制旧真实数据，不执行新设备写入，不运行build、不替主集成提交或发布。整体仍PARTIAL，未声称25项全迁移。
