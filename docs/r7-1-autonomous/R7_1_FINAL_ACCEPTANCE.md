# R7.1 最终验收

日期：2026-11-08；Agent：codex-A；线程 `01a1b2ee-40f8-7263-aba6-9f1c534c1d00`。
基线 `bdcd4f9658c3f2279e0c160c86c6b0ac8a3303d4`，分支 `codex-A/netconsole-next-r3-dual-ac`。

**全产品 PARTIAL。** 本轮在 R7.0 既有实现上交付真实产品增量。已完成范围与尚未闭环的设备、部署能力分开验收，不宣称 RELEASE_READY。

## 七工作流交付

| 工作流 | 实际代码及操作结果 | 验收边界 |
| --- | --- | --- |
| 配置管理 | 完整私有原件与脱敏版本分离；批量/固定 Site/Domain 目标解析；持久计划、立即执行、最近批次和历史；完整上下文差异 JSON 下载；独立重连回读与失败恢复状态机；版本单调性及非破坏性容量准入 | AC1/AC2/SW 实际只读备份和两类文件哈希验证通过。任意历史整机传输/staging/replace 未闭环；真实 native restore 仍关闭 |
| 双 AC | 通过既有 Netmiko 产品任务及只读探查核实型号、控制器角色、同 AP 的 Master/Backup 与 reciprocal backup-ac | 两台 COMMON 与 AP R/M、R/B/Ready 不等于已做切换；无安全、可恢复的已验证手动路径，未切换/回切 |
| FIT-AP | 经 AC 核实 AP 身份、型号、版本和关联；无凭据 Telnet 协商确认 Password-only 提示 | 不重复提交已拒绝凭据；直接认证/CLI 采集仍 AUTH_CREDENTIAL_UNAVAILABLE |
| 故障中心 | 持久事件聚合、OPEN/ACKNOWLEDGED/RESOLVED、历史、精确资源恢复证据；可选有限补采/重试；精确设备筛选、只读来源/配置关联；当前页及保留历史 CSV | 功能及授权/持久化门禁通过。真实宕机、空口业务恢复及无人值守长期闭环未验；默认不启用补采策略 |
| 原版功能迁移 | 只读审查原版源码；实际新增封存 Syslog 查询/筛选/CSV、接收诊断和五类确切 H3C 事件分类；拓扑精确筛选与来源 CSV | 合成本地 UDP 到 Raw/查询/CSV 通过，不冒充远端设备发日志；25项全迁移仍 PARTIAL |
| 运维工作台 | 总览直达设备、无线、配置、任务、诊断、Syslog、拓扑和故障；同设备配置历史及真实证据链接；身份变化/隐藏页清空旧证据 | 真实 Chrome 操作和下载通过；不把 UNKNOWN、STALE 或补采成功呈现为业务恢复 |
| Windows 部署 | LocalManager 路径规范化、逐层拒绝 reparse、doctor；随包安装入口 PlanOnly；Worker 所有权、取消/退出/预算归还；日志/数据根交接 | 实际交互运行通过。SCM 安装、自启、崩溃重启、升级/回滚未验；候选 apphost ICMP 门禁失败保留 |

## 测试和证据

- Web 最终全量：27 文件、306 项 PASS；`reports/r71-web-history-final.log`。Typecheck 与 Vite/终端资源构建 PASS；`r71-typecheck-history-final.log`、`r71-vite-history-final.log`。Vite 仍提示大 chunk，未扩大为无关打包重构。
- 最终完整 Service 集成 harness EXIT0、Release build 0 warnings/errors：`reports/r71-service-harness-history-final.log`、`r71-harness-history-build.log`；运行构建 `r71-service-history-build.log` 同为0/0。包括 schedule19、simulator19、complete batch18、故障14、Syslog8+6、Worker9组及既有回归。
- LocalManager13/13、Worker Python46/46、安装 PlanOnly15/15及7个 PowerShell语法 PASS。HTTP 最终26组 PASS，覆盖当前角色、CSRF、同会话 scope 收缩/撤销、模块禁用及损坏批次存储503；不连接设备。
- `reports/r71-browser-static-closure-20261108/result.json`：直接访问 Service18771 的已构建静态 UI，登录、总览快捷入口、故障CSV/生命周期筛选/导航、完整历史比较下载、Syslog查询/分类全部 PASS，API/page/console错误0；1920/2560截图22张。无125%验收。
- 历史构建的19个新增chunk已核验；最近批次修复构建新增17个chunk均可达，无新增不可达资源，保留既有已跟踪历史资源；`reports/r71-static-assets-history-reachability.json`。
- 最终回归发现并修复诊断进度发布竞争：子项完成而 run 仍运行时，详情和按 scope 重算的摘要最多显示99%；终态与历史发布后才显示100%。确定性回归在旧实现稳定失败，修复后 Diagnostics10/10、QuickPing7/7、Traceroute15/15通过；原失败日志保留，完整集成结果见下文。
- 真实 SW 立即执行计划的浏览器闭环和 AC1/AC2/SW 完整备份分别见配置专项；自然到期和 LLDP 最终结果在本报告最终集成节补录。

完整命令、分批证据、早期失败和适用范围见 [测试证据](R7_1_TEST_EVIDENCE.md)、[首批验收](R7_1_BATCH_1_ACCEPTANCE.md)、[后续批次验收](R7_1_BATCH_2_ACCEPTANCE.md)。失败日志未删除，未通过删除断言或降低 Gate 获得通过。

专项报告：[配置管理](R7_1_CONFIG_MANAGEMENT_REPORT.md)、[双AC](R7_1_DUAL_AC_HA_REPORT.md)、[FIT-AP](R7_1_FIT_AP_INTEROP_REPORT.md)、[故障闭环](R7_1_FAULT_RECOVERY_REPORT.md)、[Syslog迁移](R7_1_SYSLOG_MIGRATION_REPORT.md)、[Syslog分类](R7_1_SYSLOG_EVENT_CLASSIFICATION_REPORT.md)、[拓扑导出](R7_1_TOPOLOGY_EXPORT_REPORT.md)、[LLDP实录修复](R7_1_LLDP_REAL_CAPTURE_REPAIR_REPORT.md)、[Windows部署](R7_1_WINDOWS_DEPLOYMENT_REPORT.md)、[Worker生命周期](R7_1_DEPLOYMENT_WORKER_LIFECYCLE_REPORT.md)。继续运行和定位日志见 [运行交接](R7_1_RUNTIME_HANDOFF.md)。

## 数据与剩余问题

所有运行数据、私有备份、Raw 和临时测试均在 Next Workspace；未访问 `D:\NetConsoleData` / `D:\NetConsoleData-dev`，未完整复制真实数据根。仅只读原版源码，原版仓库未修改。R7.1 设备配置写入、接口关闭、HA 切换、FIT密码提交均为0。故障可选策略未启用；验收计划在完成观察后暂停，证据备份保留。

关键剩余问题是历史整机恢复、双AC切换及业务恢复、FIT直接认证、真实 Windows SCM 生命周期和候选 apphost 的 ICMP_PERMISSION_DENIED；其余迁移、长期运行及现场兼容详见 [剩余问题](R7_1_REMAINING_GAPS.md)。不执行 Push、Merge、Release。

## 最终集成记录

- 产品批次本地提交 `9135589`、`d40f971`、`5026584`、`2cb89b2`，基线以后七条工作流增量均已集成。最终文档另做本地提交，不 Push/Merge/Release。
- 真实SW产品任务 `74a3f409205646d5801b23776023469e` 重新采集 list/verbose，两命令 COMPLETE/PARSED，Raw输出/分段hash和精确3邻居语义PASS。旧5条错误Current由产品完整集合发布退役，历史/Raw不删改。证据 `reports/r71-product-lldp-20261108-020254/summary.json`。
- 最终拓扑浏览器 `reports/r71-browser-lldp-closure-20261108/result.json`：3关系；精确profile/GE1/0/2/Site local筛到1行；CSV820bytes含source/factRevision/relationKey且逐项对API。1920/2560两张截图已目视，0 API/page/console错误。通用Current仍以profile+kind+identity为键，本批不宣称同identity跨来源/scope隔离。
- 最终完整harness EXIT0包含上述LLDP和QuickPing修复；fixture仅清理行尾空白后LLDP5/5复验PASS。两次失败日志与诊断原因保留。
- 自然计划与最终运行健康见下方终态记录。

## 自然到期真实备份终态

2026-11-08 02:16:40.761 CST 原始due保持不变；实际准入02:16:42.009，延迟1248ms；02:16:45.533批次完成。计划 `6334c82406a348689bf29dd186137351`，真实AC1 `192.168.56.2`，批次 `d5c37492c99d46cc9e74c211acd08b55` SUCCEEDED（1成功/0失败），快照 `e09a1c6418f94eca8dadf84e41e81d21`，版本6。原件4768bytes SHA256 `17f3c0862e0fe7ee71065e34fd01eb5ea8eb35e88a331a5f247c442ac03ff25a`；脱敏4522bytes SHA256 `82d38a4a7b14f52011a02baa7cc505fcd0b1f382182e5ea467e4a742820c2980`。两文件及API视图均校验通过。manifest的restorable表示备份结构完整，不代表真实整机restore已开放或验收。

观察器EXIT0，`reports/r71-natural-schedule-observation.json` 为 PASS_NATURAL_DUE_REAL_AC1_BACKUP，189次采样，未改时钟/due、未run-now、未写设备。终态后通过API暂停同一计划，enabled=false/nextRunMs=null。观察含受控Service重启和Raw/配置读取，并非24H长稳。

追加浏览器检查 `reports/r71-browser-natural-closure-20261108/result.json` 发现旧SW计划最近批次仅内存、重启后404，原失败保留。后续已修复新批次持久历史及旧记录可用性提示，原失败保留；最终跨重启验证见下节。

## 批次历史修复与最终运行验收

真实SW新增批次跨Service重启后仍可读；旧内存批次明确不可用且按钮禁用，不伪造旧历史。新终态元数据持久化最多128条/4MiB，读取重新检查当前owner/角色/Site，配置版本保留策略独立。专项见 [批次重启验收](R7_1_BATCH_HISTORY_RESTART_REPORT.md)。

- 最终源码完整Service harness `reports/r71-service-harness-history-final.log` EXIT0，complete batch18/18、schedule19/19及既有全回归通过。`r71-harness-history-build.log`、`r71-service-history-build.log`均0 warnings/errors。
- 最终Web27文件306项、typecheck、Vite2115modules PASS；相关日志为 `r71-web-history-final.log`、`r71-typecheck-history-final.log`、`r71-vite-history-final.log`。
- HTTP26组PASS，含损坏批次存储503及保留证据/其它服务继续运行；`reports/r71-feature-http-20261108-022949-aa3f4c.json`。
- `reports/r71-browser-history-final-20261108/result.json`：真实已构建UI登录及7业务步骤全部PASS，0 API/page/console错误，1920/2560共22张截图；跨重启配置页两张截图已目视。旧404失败与新增HTTP测试解析错误日志均保留，未降低Gate。
- `reports/r71-runtime-history-final.json`与`r71-doctor-history-final.json`：HEALTHY / doctor PASS，两个UI均HTTP200；自然AC1及新SW验收计划均成功且暂停，nextRunMs=null；无活动Python子Worker。Service交互运行，仍不是SCM验收。
- 最终产品提交 `2cb89b2`，之前产品提交 `9135589`、`d40f971`、`5026584`；后续只更新报告与交接。不Push/Merge/Release。
