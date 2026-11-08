# R7.3 最终集成验收

2026-11-08，Agent `codex-A`，branch `codex-A/netconsole-next-r3-dual-ac`。整体结论 **PARTIAL**。本轮完成本地产品与验收闭环；模拟器运行、管理员SCM、候选apphost ICMP及24小时稳定性仍有明确外部或环境门槛。

## 产品增量

- 首批产品提交：`021c760`，包括配置容量与批次历史安全、LLDP邻居消失/恢复、无线Observation CSV、ICMP诊断入口和UI依赖按需加载。
- 故障增量提交：`5638c2f`。采集失败事件按完整有序命令计划及冻结目标身份划分资源键；计划修改后不重新解释旧故障。历史device聚合事件保持原ID、状态、来源和历史，不自动合并或消解。恢复只能由相同计划的新鲜完整成功采集证明。
- R7.3 专项报告记录配置恢复、双AC、故障中心、Windows SCM、设备互操作、功能迁移、UI与剩余问题；本文件和运行交接补足最终状态。具体文件见同目录各专项报告。

## 自动化验收

- Web全量34 files/351 tests、Vue typecheck通过。
- 当前源码Release Service.Tests完整harness退出码0。故障中心23/23、OperationalHealth15/15、H3C Fact Publication20/20通过；包括计划分区、旧事件兼容、同计划恢复隔离及重复Sync幂等回归。
- Worker49/49、HTTP 40项、Browser既有10场景和新增4场景、Release Web构建、SQLite integrity与Raw完整性沿用本轮已有证据。新代码变更后的本次复验重点为完整C# harness和Web测试；没有把既有浏览器证据冒充为当前新启动Service上的回归。
- 独立无人值守运行实际300秒，受控Service重启1次，回环UDP 30/30条完成Raw完整性核对，SQLite integrity为ok。该窗口不覆盖真实设备计划，不代表24小时长稳或无内存泄漏。

## 设备及部署验收

- 设备互操作：**BLOCKED**。授权四端口各一次有界连接均为TCP_TIMEOUT；Host-Only网卡和路由存在，但未发现运行中模拟器虚拟机。没有本轮新认证或设备Raw。没有扫描网段、猜FIT凭据或写设备。
- 双AC HA：**NOT_TESTED**。未执行主备切换或SW GE1/0/1断链。历史无线观察不等于本轮实时归属证据。
- 整机配置恢复：**PARTIAL**。原件、预检、容量与历史可用；实际设备文件传输、原生替换、回读hash和故意失败回滚未执行。
- Windows SCM：**BLOCKED**。本机无管理员权限，创建服务返回Win32 5。PlanOnly通过；真实安装、重启、异常恢复、升级/回滚及卸载保留未执行。
- ICMP候选Gate：**BLOCKED**。相同DLL由dotnet.exe运行成功，候选apphost返回native error 5；门禁未跳过，具体主机过滤层未定位。
- 功能迁移及UI：**PARTIAL**。25项迁移矩阵与工作台验收保留未完成的长期Ping、Mesh图形和专业报表等范围。Browser在1920x1080及2560x1440已有验收；原生125%不在本轮范围。

## Git、数据与运行

- 产品代码本地提交：`5638c2f`；R7.3首批提交：`021c760`。本验收文档及交接文档将在后续本地文档提交。本地分支 `codex-A/netconsole-next-r3-dual-ac`。
- 旧静态资源：开始时记录的19个untracked资源逐项哈希核验后仍保留；没有将其stage或删除。首轮构建生成的18个被替代文件依据日志与SHA256独立记录后退役。
- DevStatus仓库开始时为`main`、HEAD `0b05fce`且干净；仅新增本轮独立交接文件，不改写其他状态文档。
- Service `http://127.0.0.1:18771/` 当前健康PID `33036`；Vite `http://127.0.0.1:5174/` 当前PID `32812`。完成检查后两者均保持运行。设备端配置写入0；未访问旧NetConsole数据根。
- Push、Remote Merge、Release Publish均为0。完整测试范围和限制见 `R7_3_TEST_EVIDENCE.md`。

## 剩余问题

模拟器上下文恢复后重跑只读采集；获得独立管理与恢复路径后再设计双AC切换；在具备适用原生检查点的设备验证完整恢复与回滚；管理员隔离Windows环境执行SCM生命周期；继续实现配置异常基线、长周期运维和原版功能差距。未经新证据，不提升上述状态。
