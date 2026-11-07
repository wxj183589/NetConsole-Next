# R7.1 Windows安装与实例配置

2026-11-08；本轮实际修改产品安装脚本及随构建分发的部署入口。SCM安装、自启与崩溃恢复仍 **NOT_TESTED**。

## 产品代码

`install-service.ps1`支持显式数据根、Web/Syslog端口、H3C/Terminal及离线Python/Worker路径，支持不改系统的 `-PlanOnly`。启用设备功能时先验证离线依赖。数据目录标记最大128bytes，拒绝旧真实数据根、Windows尾随点/空格及namespace别名、reparse路径、二进制与数据目录重叠和非空未标记根。拒绝发生在旧根IO前。

实际安装使用服务专属Registry `Environment` REG_MULTI_SZ，避免SCM拿不到交互用户环境；不设置机器级环境变量。创建EventLog source，虚拟服务账号仅二进制RX/数据M，自动启动和5/15/60秒失败重启配置。任何PowerShell错误终止；sc.exe/icacls退出码检查，失败时仅回收本次创建的SCM条目，保留数据。

Service项目将安装/卸载/计划脚本与说明复制到build/publish的deployment目录。Service数据根拒绝旧项目根；LocalManager路径与Worker生命周期的额外修复见 `R7_1_DEPLOYMENT_WORKER_LIFECYCLE_REPORT.md`。

## 测试

安装计划15/15 PASS，包括真实PlanOnly入口、有效离线Worker参数、未标记根、标记超长/无效、旧根及别名拒绝、无目录/SCM/Registry写入。7个PowerShell脚本语法PASS。测试只创建并退役本次拥有的最小fixture，未访问旧真实数据根。

本机当前令牌非管理员，未安装服务、未执行SCM自启/异常恢复或卸载。手工交互服务HEALTHY/doctor通过不能替代虚拟账户ACL、系统重启、升级/回滚和安装验收。离线候选结果及最终运行环境以最终验收和测试证据报告为准。

## 安装失败与候选门禁限制

安装脚本失败时只尝试回收本次创建的SCM条目，尚未检查该cleanup的退出码；已修改的ACL及新建EventSource不作完整补偿。因此不宣称“安装失败完整回滚”。本轮未对实际SCM、ACL或事件源执行修改；后续须在独立管理员部署目标验证每阶段失败和cleanup报告，并保留数据目录。

离线候选构建、驱动/协议smoke及随包PlanOnly入口通过；完整候选E2E仍EXIT1：独立EXE的回环Traceroute返回 `ICMP_PERMISSION_DENIED`。同一最小探测DLL、同一.NET10.0.13、同一非管理员身份下，apphost.exe拒绝而dotnet.exe成功，不能直接归因为“非管理员”。具体主机策略未定位，未修改防火墙或Gate。证据：Workspace `reports/r71-offline-candidate-e2e.log`、`reports/r71-icmp-permission-investigation.md`。该候选早于后续分类/容量修复，不能作为最终源码的完整离线部署通过证据。
