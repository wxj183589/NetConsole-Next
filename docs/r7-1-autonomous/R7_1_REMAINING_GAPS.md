# R7.1 剩余问题与下一门禁

2026-11-08；全产品 **PARTIAL**。此表取代 R7.0 的当前差距判断，历史报告仍保留。尚未完成的设备能力不因 UI、fixture 或 TCP 成功而升级为 PASS。

| 优先级 / 能力 | 当前结论与确切原因 | 完成条件 |
| --- | --- | --- |
| P0 任意历史整机恢复 | BLOCKED。真实 SW 的 commit 未形成可枚举检查点；真实 transport 未验证完整传输/staging/native replace 确认交互。描述 Writer 的独立重连/失败恢复已经增强，但不是整机恢复 | 在支持的授权设备上验证原生检查点、完整历史文件传输、替换、独立重连完整回读和业务探针，再注入失败验证恢复；分别记录写入/回读/业务/回滚 |
| P0 双 AC 1+1 实际切换 | NOT_TESTED。实际 AP 有 Master/Backup 与 Ready 观察及双向 backup-ac；不能说未配置备份。未找到经验证可精确恢复的手动切换路径；SW GE1/0/1 同承载主机管理，不能用其关闭制造故障 | 独立管理回退路径、明确切换/回切命令或隔离故障方式；记录双 AC 角色、AP 关联和恢复时长。Radio Down 且无已验证客户端，空口业务恢复另验 |
| P0 FIT 原生 Telnet | AUTH_CREDENTIAL_UNAVAILABLE。仅无凭据协商确认 Password-only 提示；既有已拒绝凭据未重复提交。经双 AC 的身份、型号和关联事实可用 | 获得来源明确且适用于 FIT 接入口的凭据后一次有界登录，采集身份/型号/版本；不猜密码，不把 AC 管理观察当直接 CLI |
| P1 计划长期留存 | 完整私有原件批量、Site/Domain 固定集合、持久计划、立即执行和最近批次已实现。自然到期AC1版本6、私有/脱敏hash验证已PASS并暂停；追加复查发现旧最近批次重启不可读；现已实现新终态批次持久元数据、旧批次可用性提示。新真实SW批次跨重启API/浏览器PASS，旧丢失明细不可恢复；元数据最多128条/4MiB。最终容量结果见最终验收；不自动删除历史 | 明确容量、保留/导出与长期运行合同；不得将列表展示上限当磁盘保留策略 |
| P1 故障业务恢复 | 持久生命周期、聚合、精确证据核验、显式有限补采/重试、历史/导出可用。未人为制造真实宕机/AP业务故障；连接成功和补采成功不等于业务恢复 | 授权实际故障下验证新鲜同资源恢复证据及恢复时长；外部通知、全部配置/LLDP变化事件和长期策略仍未全迁移 |
| P1 Windows SCM 部署 | PlanOnly、随包入口、路径边界、LocalManager、Worker归属回收已验证；虚拟账户 ACL、开机自启、异常重启、升级/回滚/卸载保留数据未在实际 SCM 执行 | 独立管理员部署目标执行安装/lifecycle Gate；不能以交互进程 HEALTHY 替代安装验收 |
| P1 离线候选门禁 | FAIL/PARTIAL：候选独立 EXE 的回环 Traceroute 为 ICMP_PERMISSION_DENIED。同 DLL 经 dotnet.exe 探测成功；具体主机策略未定位 | 在部署目标核查精确候选 EXE 的 ICMP 权限后重跑原 Gate。保留失败；不改 Gate 为跳过，不用 dotnet probe 成功替代 candidate PASS |
| P1 设备/拓扑证据 | 型号/版本/控制器角色已进产品 Raw 事实；LLDP 精确筛选及 CSV 已实现。真实SW产品重采正确3关系，精确Site/接口/来源浏览器与820bytes CSV已验；旧解析错列/集合退役已修复。继承的Current键不含source/scope，同identity跨来源范围仍可能覆盖 | 继续验证全站身份消歧和同identity的独立Current来源/范围语义；不跨站模糊归并，不将过滤退役测试扩大为独立authority |
| P2 Syslog 完整迁移 | 接收→封存 Raw→授权查询→CSV 以及五类确切 H3C 标识分类已交付。真实验收为本机合成 UDP；无远端设备 Syslog/长稳证据 | 外部设备发日志、更多厂商事件、持续负载和保留策略分别验；UNKNOWN/多标识不推导设备身份、HA或业务恢复 |
| P2 智能/批量写配置 | 模板变量/接口/VLAN校验与 DRY_RUN_ONLY 保持。没有已验证恢复能力不开放真实模板 Writer | 逐型号支持、保存基线、可恢复、逐设备回读及失败恢复全部验证后再开放 |
| P2 无线权威/图形 Mesh | Domain/AP/Observation、Current/PreviousEffective/Recent10、严格来源与能力面板可用；完整权威生命周期与图形分析未全部闭环 | 按原版/R3合同继续实现来源权威、冲突及有效变化，避免用组名或模糊身份替代权威 |
| P2 长期诊断/现场兼容 | 有界 Quick Ping/Traceroute、任务/Raw/导出可用；原版长期 fping、物理设备、多厂商/Linux/跳板、跨机 Web/TLS 及长期运行未全部验收 | 按具体业务和设备逐项完成，不从模拟器 SSH 成功推定全部现场兼容 |

原版 25 项迁移详表见 [功能矩阵](../r6-feature-parity/ORIGINAL_TO_NEXT_FEATURE_MATRIX.md)。本轮不考虑 Windows 125% 验收；未访问旧真实数据根，未 Push/Merge/Release。
