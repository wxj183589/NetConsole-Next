# R7.3 动态工作池

2026-11-08，codex-A。实际基线 `220c979050192eafce2e77613041c5bd89a845f9`，分支 `codex-A/netconsole-next-r3-dual-ac`。仅本地开发与提交。

## 初始保护

19个既有untracked静态资源已逐文件记录字节与SHA256，见Workspace `reports/r73-initial/initial-untracked.json`，不删除、不纳入提交。DevStatus实际状态干净，HEAD已经集成其它任务历史，本轮仅新增独立交接文件。Service实际PID32828、18771 HEALTHY，Vite PID32812、5174。

## 并行所有权

| 执行者 | 工作流与文件边界 | 首批状态 |
| --- | --- | --- |
| 主线 | 网络诊断、无线UI/导出、main/Dashboard构建优化、集成/浏览器/短稳/报告 | Host-Only与路由正常，无运行HCL/虚拟机，四授权端口超时。按需组件及ECharts构建已由1034/1158kB降至354/476kB |
| 配置执行者 | 配置存储/恢复/批次Service与组件、专项测试 | 精确profile授权容量/历史占用API与UI，随后批次安全重试 |
| 故障执行者 | FaultCenter/OperationalHealth/新投影、组件/测试 | 同分区LLDP邻居消失/恢复/复发与来源CSV，随后配置变化核验 |
| 部署执行者 | LocalManager/部署scripts/诊断入口与测试 | 隔离SCM生命周期脚本与PlanOnly，精确apphost/dotnet ICMP控制变量探针 |

## 连续批次

1. 网络根因与资源保护，独立产品代码及定向测试。
2. 集成首批，真实Service与静态Web/浏览器验收，本地检查点。
3. 继续第二批高价值配置/故障功能与无线导出，独立根实际持续运行与重启/Raw核验。
4. Web/Worker完整回归、Service完整harness、HTTP与Browser、Release/SQLite/Raw/部署门禁。
5. 十份最终报告、DevStatus独立交接与本地提交，保留主Service和Vite运行。

设备环境受阻时继续本地产品功能。整机恢复、HA切换、FIT、SCM与实际无线业务恢复分别记录，未经执行不提升PASS。UI125%不在范围，旧NetConsole真实数据根不访问。
