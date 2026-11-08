# R7.3 运行交接

日期：2026-11-08
Agent：`codex-A`
产品仓库：`D:\study\NetConsole-Next-Workspace\NetConsole-Next`
分支：`codex-A/netconsole-next-r3-dual-ac`
产品功能提交：`5638c2f`（R7.3首批：`021c760`）

## 可用入口

- 集成Service与内置Web：<http://127.0.0.1:18771/>，最后核验PID `33036`，health `HEALTHY`。
- Vite开发入口：<http://127.0.0.1:5174/>，最后核验PID `32812`。
- 完成任务后未停止上述进程。PID会变化，后续先查健康接口和端口所有者。

本次追加改动经过当前源码的完整Service harness验证，但没有覆盖正在服务用户浏览器的既有集成构建或其数据目录。若需要让浏览器检查最新C#故障资源分区行为，应先按既有受控运行脚本确认PID、命令行、DLL和数据根，再在独立隔离根启动对应构建；不得直接覆盖或并行启动同一数据根。

## 测试和证据

- 完整Service.Tests Release harness：退出0，详见 `R7_3_TEST_EVIDENCE.md`。
- Web：34 files/351 tests及typecheck通过。
- HTTP 40项、Browser 10+4场景、Worker49/49、独立300秒运行和Raw/SQLite检查使用各自专项报告，不与Service harness证据混为一类。
- 网络环境及四个授权设备连接状态见 `R7_3_DEVICE_INTEROP_REPORT.md`；设备当前阻断为TCP_TIMEOUT，认证和本轮采集未发生。

## 数据及操作边界

- 本轮测试使用Next Workspace内的fixture、隔离测试根及回环流量。没有访问、复制或修改旧NetConsole真实数据根，没有设备配置写入。
- 不要猜FIT-AP凭据，不要扫描整个网段，不要关闭管理链路以制造HA故障。
- SCM真实生命周期仍需要单独管理员隔离环境；目前只有离线计划及PlanOnly证据。
- 300秒无人值守结果不等同24小时验收。

## Git接续

产品代码与R7.3验收/专项报告均已本地提交。DevStatus只新增了一个R7.3任务交接文件。所有仓库禁止Push、Remote Merge与Release Publish。本轮未清理19个既有静态资源。
