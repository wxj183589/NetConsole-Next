# NetConsole-Next 功能迁移状态（R7.0 当前集成）

更新时间：2026-11-08；整体 **PARTIAL**。本文件由 R6.1 状态表更新为 R7.0，不再把“智能配置未实现”“所有备份仅待现场兼容”或“恢复已完成”等旧概括当作当前事实。原版源码、每项 API、UI、测试、操作边界见 [25项逐项迁移矩阵](../r6-feature-parity/ORIGINAL_TO_NEXT_FEATURE_MATRIX.md)；以下编号与矩阵严格对应。

| # | 功能 | 当前状态 | 可操作实现 / 验证入口 | 尚未闭环 |
| --- | --- | --- | --- | --- |
| 1 | 设备管理 | PARTIAL | 设备Site/分组/筛选/编辑/批量预览确认；详情能力API已接；DevicesView测试 | 资产全量对齐、更多现场设备 |
| 2 | 设备连接 | PARTIAL | Netmiko单台/批量连接；AC1/AC2/SW真实SSH | FIT认证、全部协议/型号 |
| 3 | SSH/Telnet控制台 | PARTIAL | WebShell/录制/回放与Linux独立页面；Terminal/Linux测试 | 本轮真实Linux/Telnet互操作未全验 |
| 4 | SSH中转 | PARTIAL | Site jump API/指纹/worker复用；既有连接策略测试 | 本轮真实中转 NOT_TESTED；不转发ICMP/UDP |
| 5 | 设备信息采集 | PARTIAL | Raw/Facts/Current、身份/能力API；AC型号版本实读 | 过期/无证据不能视为在线 |
| 6 | 接口管理 | PARTIAL | 事实/能力/健康工作台、真实brief补采 | 完整编辑、错误趋势、业务验证 |
| 7 | VLAN | PARTIAL | 规划CRUD、H3C整表事实、Access VLAN预览 | 规划与运行态分开；无通用配置Writer |
| 8 | LLDP | PARTIAL | claims/relations、来源时间、SW邻居实读 | 完整现场消歧/历史工作台 |
| 9 | 网络拓扑 | PARTIAL | 只读资产/LLDP/Domain；TopologyView测试 | 图形布局、跨域和长期变化分析 |
| 10 | 无线AC管理 | PARTIAL | Domain/Observation/Current/Recent10；两AC Common和R/M、R/B实读 | HA切换 NOT_TESTED，不能提升为DEVICE_VERIFIED_HA |
| 11 | FIT-AP管理 | PARTIAL / 直接接入BLOCKED | AC托管身份、组、状态、历史；禁止独立创建 | 原生Telnet无可用凭据，不重复撞密 |
| 12 | AP Group | PARTIAL | 组来源键/成员版本/AC范围API；WirelessGroup测试 | 完整权威生命周期与现场共享证据 |
| 13 | Mesh | PARTIAL | 部分命令/Raw/事实 | 无对等专项分析页、时序质量和报表迁移 |
| 14 | Ping | PARTIAL | 工具集PING/TCP/DNS/Traceroute、取消/历史 | 有界诊断不等于车队长期监控 |
| 15 | Quick Ping | PARTIAL | R7有界ICMP/TCP采样、逐样本/统计/进度；真实TCP链通过 | 原版fping不限时/最长7天、压缩保留/IP占用未迁移 |
| 16 | Syslog | PARTIAL | UDP ingress/Raw/health已有；当前SyslogPort=0 | 无独立Vue viewer；当前未监听，不能称可查收日志 |
| 17 | 配置备份 | PARTIAL | 脱敏批量/计划；完整私有单设备历史/hash/差异/脱敏下载 | 完整私有原件批量/Site/Domain/定时未闭环 |
| 18 | 配置恢复 | BLOCKED | 只读预览、受控描述恢复、checkpoint失败封闭 | 任意历史整机恢复未支持；真实commit无检查点；replace交互未验证 |
| 19 | 智能配置 | PARTIAL / DRY_RUN_ONLY | 两H3C模板、变量/接口/VLAN/注入校验；SmartConfiguration测试 | 不连接设备、不执行配置、无批量/回滚 |
| 20 | 批量任务 | PARTIAL | TaskPlan/连接/脱敏备份批次、取消/并发限制 | 完整私有备份/智能配置/恢复类型未全统一 |
| 21 | 任务历史 | PARTIAL | 任务Run/Command/Raw/Parse、无线重试与ZIP | 原版全部任务类型/长期策略未全对齐 |
| 22 | 数据导入导出 | PARTIAL | 设备CSV预览提交、规划和任务/无线ZIP | 专业业务格式不全；不自动迁移旧真实数据 |
| 23 | 故障分析 | PARTIAL | 证据健康/事件、显式只读恢复/补采；真实AC链通过 | 未制造故障，无盲目设备写入，不称业务恢复 |
| 24 | 报表 | PARTIAL | 规划/任务/诊断/无线导出、脱敏配置比较JSON | Mesh/MR等全部专业报表未迁移 |
| 25 | 设备状态监控 | PARTIAL | 健康分页/新鲜度/来源证据/设备详情；缺失CPU内存UNKNOWN | 长期趋势、接口错误、通知/确认/自动闭环不全 |

## 本轮验证与数据影响

R7 实际代码包括 Quick Ping、OperationalHealth/只读补采、H3cOperationalCapabilities、DevicesView能力详情、Wireless能力证据、完整配置比较/预览/检查点阻断和LocalManager doctor。设备详情13/13、typecheck通过；专项测试及最终全量/HTTP/浏览器/Release结果由 [R7最终验收](../r7-autonomous/R7_FINAL_ACCEPTANCE.md) 汇总，不能复用R6.9历史计数。

完整配置恢复须分别取得 CONFIG_WRITE_SUCCEEDED、CONFIG_READBACK_VERIFIED、SERVICE_VERIFIED、ROLLBACK_VERIFIED；当前真实SW `NATIVE_CHECKPOINT_NOT_CREATED`，未执行replace。两AC的AP主备观察不证明控制器HA切换。FIT的AC托管信息不证明原生登录。

本次迁移审计只读原版源码；未访问旧 `D:\NetConsoleData` / `D:\NetConsoleData-dev`，未复制真实数据根。当前运行/提交/最终浏览器复验以主集成验收为准，本文件不写入未验证的最终PID或提交SHA。
