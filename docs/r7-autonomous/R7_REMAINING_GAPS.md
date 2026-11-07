# R7.0 剩余差距

更新：2026-11-08。全产品结论 **PARTIAL**。PASS 仅适用于明确覆盖的功能与环境，未执行能力不因 fixture 通过而升级。

| 优先级 | 能力 | 状态与具体原因 | 下一步和验收条件 |
| --- | --- | --- | --- |
| P0 | 任意完整历史配置整机恢复 | BLOCKED：历史预览明确 `FULL_DEVICE_RESTORE_NOT_IMPLEMENTED`；SW commit 返回 prompt 但不生成检查点，`NATIVE_CHECKPOINT_NOT_CREATED`；真实 replace 交互未验证 | 使用支持原生检查点/替换的授权设备，观察确切确认交互，再实现 transport；分别验证写入、重新连接、完整回读、接口/LLDP及业务探针、失败回滚。不得盲猜确认或把描述 Writer 当整机恢复 |
| P0 | 双 AC 受控 HA 切换 | BLOCKED：两台 `display wlan ac-role` 均 Common；R/M、R/B 是 AP 观察，未找到已验证可用的强制切换路径 | 核实实际冗余配置与设备支持路径，保存基线/具备恢复能力和业务探针后再受控切换；记录两AC角色、AP关联、切换/恢复时间。尚不能断言模拟器绝对不支持HA |
| P0 | FIT 原生 Telnet | BLOCKED：既有凭据曾返回第二次 Password，未取得 prompt；本轮无新已验证凭据，因此不重复失败认证 | 已确认凭据/控制台策略后仅一次有界验证；经AC身份、观察、组和历史继续可用，直接访问保持UNKNOWN |
| P1 | 完整私有配置批量/Site/Domain/定时 | PARTIAL：完整原文仅单设备；现有批量/计划为脱敏备份，不能互称 | 复用计划与授权冻结合同，扩展私有完整捕获、容量/保留策略、逐设备失败和恢复适格性测试 |
| P1 | 自动巡检与恢复闭环 | PARTIAL：当前用户触发连接探测和已有版本/接口补采，最多3次/120秒；真实模拟器成功但未制造宕机 | 后台策略、持久事件生命周期、配置变化告警、AP恢复确认、通知去重；成功必须来自重新检测，连接成功/任务入队不是业务恢复 |
| P1 | 健康指标与事件覆盖 | PARTIAL：接口Down、证据过期、采集失败、部分AP异常/有效变化；CPU/内存/接口错误无当前事实为UNKNOWN | 先补受支持命令与解析证据，再添加指标及事件；补配置变化、LLDP变化、设备离线/恢复的证据合同，不从失败采集推定离线 |
| P1 | Windows安装、SCM及崩溃恢复 | NOT_TESTED：doctor与进程安全启停通过；安装/虚拟账户/开机自启/强制崩溃恢复/卸载数据保留本轮未执行 | 在隔离Windows部署目标运行既有lifecycle Gate和安装包门禁；验证离线依赖、ACL、升级/回滚与保留数据 |
| P1 | Syslog工作台 | PARTIAL：UDP队列/Raw/检索底座存在，无专用Vue viewer；当前实例syslog端口0禁用 | 实施查看/过滤/来源/丢弃诊断UI，授权独立端口下做接收与Raw完整性验收 |
| P2 | 原版长期Quick Ping | PARTIAL：Next有界2–60样本、8目标、单目标120秒；未迁移原版长期fping进程/分段/保留语义 | 单独设计长期任务、资源预算、进程归属、取消/重启恢复及分段治理，不无限扩大HTTP诊断 |
| P2 | 智能/批量配置 | PARTIAL：模板变量、接口/VLAN/注入校验与DRY_RUN_ONLY；无设备写入 | 逐型号支持、权限/基线/恢复可用性/逐设备回读门禁齐备后才开放写入 |
| P2 | 无线身份生命周期/图形Mesh/拓扑 | PARTIAL：Domain/AP/Observation和Current/PreviousEffective/Recent10可用；图形分析与权威生命周期仍缺口 | 对照R3合同补权威、冲突、状态有效变化和专项图形分析；不跨站模糊归并 |
| P2 | Linux/中转/现场兼容 | NOT_TESTED（本轮真实互操作）：保留Linux SSH.NET与网络设备Netmiko分离及Site跳板 | 授权Linux/跳板/真实型号回归，不能用模拟器SSH替代全部协议验收 |
| P3 | 全原版报告/导入导出/趋势 | PARTIAL：CSV、任务/无线ZIP及具体配置导出可用；全部专业报表、长期趋势与格式未齐 | 按25项迁移矩阵逐业务验收，避免把通用文件下载当全部报表已迁移 |

设备详情目前仅展示现有身份事实字段；当前型号可能为UNKNOWN（现有 `H3cVersionInfo` 无型号字段），不能用库存空值或外部报告填造为已采集型号。真实探查所得WX5540H-HCL/S6850型号见专项报告，后续需要进入受支持的事实解析合同。

所有未执行测试、物理设备/空口业务、跨机器安装和长期稳定性均保留 NOT_TESTED。完整25项差距见 `docs/r6-feature-parity/ORIGINAL_TO_NEXT_FEATURE_MATRIX.md`，本轮验证索引见 `R7_FINAL_ACCEPTANCE.md`。
