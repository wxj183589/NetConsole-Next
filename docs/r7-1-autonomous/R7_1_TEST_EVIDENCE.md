# R7.1 测试证据与适用边界

日期：2026-11-08。所有 `reports/`、`build/`、`test-data/` 均相对 `D:\study\NetConsole-Next-Workspace`，不把私有配置原件和凭据放入 Git。

## 自动化

| Gate | 本轮证据 / 结果 |
| --- | --- |
| 第一批完整 Service | `reports/r71-service-harness-batch1-rerun.log`，EXIT0；早期损坏故障文件异常已修复，原失败保留 |
| 第二批完整 Service | `reports/r71-service-harness-final-workspace.log`，EXIT0；Release build `reports/r71-harness-build.log`，0 warnings/errors。包含 Syslog 8+6、完整批量11、故障14、Worker9组、重连172及既有迁移/Raw/任务/权限/调度/取消/并发门禁 |
| Web | 第二批 `reports/r71-web-final-rerun.log`，27文件297项 PASS；后续新增定向和最终计数见最终验收 |
| Worker | Python 46/46；拥有子进程 cancel/abort/dispose/timeout/exit 及 scheduler shutdown 验证，16个budget槽位全部归还。不是终止无关进程 |
| 安装入口 | PlanOnly 15/15、7个 PowerShell 语法通过；候选随包 `deployment` 实际入口 PlanOnly 通过，`reports/r71-offline-deployment-plan.json`。未改 SCM/Registry |
| HTTP 授权 | `reports/r71-feature-http-20261108-012452-8425e4.json`，23组 PASS；ADMIN只读/匿名/CSRF/同会话scope收缩/模块禁用。使用最小合成故障及一条封存Syslog，0设备任务；run-now仅验证missing-plan准入，成功执行另见浏览器 |

完整 Service 两次失败证据保留：`r71-service-harness-final.log` 的 apphost 被拒访问默认 Temp，随后 `...final-rerun.log` 使用外置 artifacts DLL 导致测试按 AppContext 向上找仓库失败。最终按仓库既定 bin 输出构建，以 dotnet.exe 运行并将 TEMP/TMP 指向 Workspace 最小测试目录，全部通过。没有删除断言或用失败日志冒充通过。`pnpm test:unit` 不存在的一次命令失败保留于 `r71-web-final.log`，正确入口 `pnpm test` 的结果见 rerun。

## 真实产品 HTTP / 模拟器

- `reports/r71-product-identity-20261108-005330/summary.json`：AC1/AC2 通过现有产品任务串行采集身份及控制器角色，均 SUCCEEDED / Raw COMPLETE / PARSED；型号 WX5540H-HCL、Alpha7165、COMMON。该事实随时效变 STALE，不伪造实时。
- `reports/r71-complete-configuration-20261108-005555.json`：AC1、AC2、SW 三台独立 COMPLETE_RUNNING 批次 SUCCEEDED；私有原文、脱敏文件、公开视图哈希和字节数逐项验证。
- `reports/r71-device-20261108-003716`、`r71-ha-admission-20261108-004028`、`r71-ap-help-*`：只读设备/AP/链路/CLI能力证据；未切换/回切/关闭接口。FIT 只有无凭据协商，未提交错误密码。
- 自然 AC1 计划 `6334c82406a348689bf29dd186137351`：02:16:40.761 原始到期。创建 `reports/r71-natural-due-plan-20261108-011640.json`，重启后 due 未漂移 `r71-natural-due-after-restart-20261108-012655.json`。持续观察 `r71-natural-schedule-observation.json`；最终是否执行及暂停以其终态和最终验收为准，不提前称 PASS。

## 浏览器

真实 Chrome → Vue5174 → 认证 API18771；1920×1080、2560×1440，无125%检查。结果 JSON记录实际 API/page/console 错误和宽度；截图实际视觉复核。

| 目录 | 验收 |
| --- | --- |
| `reports/r71-browser-20261108-rerun` | 故障、配置目标预览、已有完整配置及差异下载 |
| `reports/r71-browser-syslog-udp-20261108` | 单条本地 UDP→封存Raw→真实查询→CSV；不是设备发日志 |
| `reports/r71-browser-topology-20261108` | 15个profile claims、0关系；准确空态/禁用导出，无虚构非空验收 |
| `reports/r71-browser-fault-navigation-20261108` | 故障精确设备筛选/重置、真实 TASK_RUN 来源、同设备配置历史入口 |
| `reports/r71-browser-syslog-classification-20261108` | 本地3条合成UDP，MESH_LINKUP/IFNET_PHY_UPDOWN/冲突UNKNOWN，筛选、Raw引用及新增CSV列；0错误，6张截图 |
| `reports/r71-schedule-browser-2026-11-07T17-26-09-781Z` | UI创建精确SW计划→立即执行→真实私有/脱敏hash→后台终态→暂停→最近批次；暂停计划保留便于复查 |
| `reports/r71-browser-fault-export-20261108-final` | 总览直达Syslog/拓扑/故障焦点；真实当前页CSV 2134bytes、1 EVENT+2 HISTORY逐字段对API，额外导出查询0；0错误、4张截图 |

## 最终集成补充

- 配额集成完整 Service：`reports/r71-service-harness-quota-final.log` EXIT0；`r71-harness-quota-build.log` 0 warnings/errors；schedule19/19、simulator19/19、complete batch11/11，含并发容量和运行中删除回收。后续 LLDP 增量另行验证。
- 最终前端：`reports/r71-web-closure-final.log`，27文件305项 PASS；`r71-typecheck-closure-final.log` 与 `r71-vite-closure-final.log` PASS。19个新生成chunk均在当前index引用图内，`r71-static-assets-reachability.json`。
- `reports/r71-browser-fault-review-20261108`：CSV/身份加固后登录/快捷入口/导出/精确导航连续PASS，0错误、8张截图；控制字符和敏感值边界用隔离组件测试验证。
- `reports/r71-browser-static-closure-20261108`：直接访问Service18771已构建UI；登录及6个业务步骤PASS，API/page/console错误0，22张1920/2560截图，真实下载与来源逐项核对；无新设备连接。

## 未通过 / 未执行

LLDP 最终增量首次完整回归 `reports/r71-service-harness-lldp-final.log` 在 Quick Ping 重启读取处失败（`complete samples survive restart`，EXIT -532462766）；对应构建 `r71-harness-lldp-build.log` 为0 warnings/errors。定位为诊断子项完成、run 尚未终态/持久化时已经对外报告100%，调用方立即重建实例可能读取不到完整历史。修复与重新验收结果在最终集成记录补录；保留本次失败日志，早先 quota harness EXIT0 不能替代此后代码的最终回归。

随后 `reports/r71-service-harness-publication-final.log` 已通过 QuickPing7/7，但在原有 Phase11 AP LLDP fixture 断言失败。其带版权banner和命令echo、缺closing prompt；新严格完整集合判定误伤既有表抽取兼容。修复保留普通表抽取对该旧形态的支持，产品带provenance的完整集合路径仍严格，不能由缺结束边界的离线capture建立table authority。原断言未删除；新增同一实录的确定性回归，LLDP5/5、发布17/17、Phase11 facts28/peers3 PASS。最终完整回归另录，两个失败日志均保留。

`reports/r71-offline-candidate-e2e.log`：离线候选 E2E EXIT1，回环 Traceroute 为 ICMP_PERMISSION_DENIED。候选构建/离线驱动smoke/PlanOnly通过不能代替完整 Gate。`reports/r71-icmp-permission-investigation.md` 及两个 probe 日志证明同 DLL/同 .NET/同非管理员身份在独立 apphost 被拒、dotnet.exe 成功；具体主机策略未确认。没有修改 Gate 或防火墙策略。

整机历史恢复、HA切换/回切、FIT直接认证、真实无线客户端恢复、SCM安装/开机/崩溃恢复、跨机/物理设备和24小时长稳均不因以上测试而升级为通过。

## 最终修复后统一验收

- `reports/r71-service-harness-closure-final.log` EXIT0；`r71-harness-closure-build.log`、`r71-service-closure-build.log` 均0 warnings/errors。QuickPing7/7、Diagnostics10/10、Traceroute15/15、LLDP5/5、publication17/17、Phase11 facts28/peers3及既有完整门禁通过。fixture清理行尾空白后另行LLDP5/5 PASS。最终source与运行DLL一致，未删断言。
- `reports/r71-product-lldp-20261108-020254/summary.json`：真实SW产品任务 `74a3f409205646d5801b23776023469e`，两条只读命令 COMPLETE/PARSED，输出及分段hash核验、三关系语义PASS，旧5 Current由产品退役，历史保留。无设备写入或凭据导出。
- `reports/r71-browser-lldp-closure-20261108/result.json`：已构建UI显示3关系；精确profile/GE1/0/2/Site local=1；CSV820bytes/1行，与API来源/revision/key核对。双分辨率截图已目视，0 API/page/console错误。浏览器设备连接0。同identity跨来源/scope Current继承限制未因此消失。

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
