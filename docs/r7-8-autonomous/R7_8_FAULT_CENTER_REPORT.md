# R7.8 活动 Long Ping 故障中心

## 产品修改

- `OperationalHealthService` 接受 RUNNING、尚未结束且有真实有效样本的 Long Ping，按样本时间检查 5 分钟新鲜度。Quick Ping 与 Traceroute 仍使用完成证据。
- 同设备、Site、协议、目标地址、TCP 端口精确绑定。暂停、正在暂停、取消、异常完成、未发送、计数不一致、未来/乱序/过期样本不确认故障或恢复。
- 3 次连续有效丢失形成 `PROBE_PACKET_LOSS`；同资源后续末尾 3 次应答均晚于故障，才允许恢复。累计丢包统计保留，不推断设备/AP 离线。
- `FaultLifecycle` 允许同一诊断运行中更新的有效丢包窗口推进事件；重复窗口保持幂等，恢复之前的旧样本不能复发，恢复之后的确认丢失可重开同一事件。
- 每个精确协议/端口保留自己的最新窗口。兼容字段 `probeWindow` 继续提供最新窗口，新可选字段 `probeWindows` 用于故障恢复逐资源匹配；ICMP 与 TCP 不互相遮蔽。
- 故障中心说明活动 Long Ping、暂停/取消及分协议核验边界。

## 定向验证

隔离输出：`D:\study\NetConsole-Next-Workspace\build\r78-fault-agent`。

```powershell
dotnet build tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj --artifacts-path D:\study\NetConsole-Next-Workspace\build\r78-fault-agent --nologo
dotnet D:\study\NetConsole-Next-Workspace\build\r78-fault-agent\bin\NetConsole.Next.Service.Tests\debug\NetConsole.Next.Service.Tests.dll --operational-health-tests
dotnet D:\study\NetConsole-Next-Workspace\build\r78-fault-agent\bin\NetConsole.Next.Service.Tests\debug\NetConsole.Next.Service.Tests.dll --fault-center-tests
cd web
npm test -- --run src/components/FaultCenter.test.ts
```

结果：编译 0 警告/0 错误；OperationalHealth 25/25、FaultCenter 30/30、FaultCenter Vue 17/17 全通过。

业务测试复用真正的 `NetworkDiagnosticsService → OperationalHealthService → FaultCenterStore`：

1. 注入受控探测器逐阶段产生 3 丢失 → 3 应答 → 3 丢失；在任务仍 RUNNING 时验证 OPEN → RESOLVED → REOPENED，来源 ID 相同，旧窗口重读幂等，恢复后再失联不借旧应答恢复。
2. 同一设备同时运行 ICMP、TCP；形成两个不同资源故障，再给 ICMP 三次应答而 TCP 保持丢失，仅 ICMP 恢复。
3. 继续覆盖取消、暂停/正在暂停、精确目标、无效样本、新鲜度、完成 Long Ping 与 Quick Ping 一致性及既有 Raw/LLDP/AP 生命周期。

这些是隔离 fixture 的产品集成测试，不代表真实设备或模拟器验收；本子任务没有启动/停止共享 Service，没有操作设备。测试自建单库 fixture 并由既有清理逻辑回收，没有复制真实数据根。原有 19 个未跟踪 wwwroot 资产及其它工作流改动未触碰。

## 待主任务整合的边界

- 暂停续跑确认边界已由主集成在 `8103729` 完成：`ConfirmationStartSequence` 从续跑后的有效样本重新确认，累计统计保留；网络诊断与健康投影定向覆盖通过。
- 后台仍每 15 秒轮巡一页、每页 25 设备，读取最近 20 个授权诊断任务；不声称逐样本即时告警或无限任务覆盖。
- 本子任务没有执行完整 Service/Web harness、HTTP、浏览器或真实设备验收，由主任务在受控升级窗口执行。
- 按分工不执行 commit/push，由主任务汇总审查。

## 配置变化自动进入故障中心

- `OperationalHealthService` 注入现有 `SimulatorConfigurationService`，只读复用完整备份漂移检测；不连接设备，也不生成备份或配置写入。原有四台模拟器直连、唯一设备身份和 Site 校验继续生效，非授权配置目标不进入本链路。
- 最新两个连续、完整且通过完整性验证的备份发生配置或型号/软件版本变化时生成 `CONFIGURATION_CHANGED`。最新备份须在 5 分钟新鲜度内；事件以设备、Profile、Site 和完整备份来源分区聚合，以实际最新备份 ID 和捕获时间去重。摘要仅包含版本编号和变化说明，不含配置行或敏感值。
- `UNKNOWN`、无基线、仅一个基线、损坏文件、身份漂移、版本缺口、重复版本、未来或过期捕获均不能形成当前变化或恢复。完整备份检测异常不会中断其它设备的健康投影。
- 后续有效连续备份的配置和元数据均不变化，且其时间和前驱备份晚于或精确衔接故障证据时，现有自动核验可将事件标记为 RESOLVED。转换说明明确表示“配置已稳定”，不表示回滚到变化前配置。旧证据、其它设备/Site/来源分区不能消除事件；更新的真实变化可 REOPEN 同一资源。
- 原有每页 25 台设备、15 秒后台轮巡及事件数量上限继续适用。此链路以已有完整备份为事实来源，不代替定时备份任务。

隔离构建与定向验证输出位于 `D:\study\NetConsole-Next-Workspace\build\r78-config-fault-agent`：

```powershell
dotnet build tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj --artifacts-path D:\study\NetConsole-Next-Workspace\build\r78-config-fault-agent --nologo
dotnet D:\study\NetConsole-Next-Workspace\build\r78-config-fault-agent\bin\NetConsole.Next.Service.Tests\debug\NetConsole.Next.Service.Tests.dll --simulator-config-tests
dotnet D:\study\NetConsole-Next-Workspace\build\r78-config-fault-agent\bin\NetConsole.Next.Service.Tests\debug\NetConsole.Next.Service.Tests.dll --operational-health-tests
dotnet D:\study\NetConsole-Next-Workspace\build\r78-config-fault-agent\bin\NetConsole.Next.Service.Tests\debug\NetConsole.Next.Service.Tests.dll --fault-center-tests
```

结果：构建 0 警告/0 错误；完整配置 28/28、健康投影 26/26、故障中心 30/30 全通过。新增测试使用既有单库 fixture 和受控配置会话，实际运行 `Capture → DetectDrift → OperationalHealthService → FaultCenterStore → VerifyObservedDevices`，验证 OPEN → RESOLVED → REOPENED、重复幂等、损坏最新备份不能恢复及监测不连接/不写设备；同时覆盖精确身份、Site、分区、版本连续性和时间边界。测试退出回收其 fixture，没有复制真实数据根。

本配置子任务未操作共享 Service、模拟器或生产数据；未修改 Program.cs、Web 或已存在的未跟踪静态资产，未 commit/push。HTTP/浏览器和真实模拟器验收由主任务在受控升级后完成；配置事件的 Web 标签/筛选由主任务整合。

## 主集成 HTTP / 浏览器复验

2026-11-09，候选 `build/r78-config-candidate`：完整配置 28/28、健康 26/26、故障中心 30/30；Web 全量 36 files / 401 tests、typecheck、Release publish 及 Worker 60 tests 通过。

- `reports/r78-config-fault-c74dfe4debcc/result.json`：12 项实际 Service HTTP 检查通过，包含故障 OPEN/稳定恢复/复发、损坏备份不恢复、权限、重启持久化、0 设备会话与命令。证据为合成配置，不是模拟器恢复验收。
- `reports/r78-config-fault-browser-2026-11-08T17-16-41-701Z/result.json`：Chrome 1920x1080 和 2560x1440 共 15 项通过；真实 API 过滤、脱敏证据、正确补采说明、CSV、无横向溢出，无设备写入或页面错误。
- 浏览器首轮 2560 宽度失败是脚本对已展开历史重复点击，随后改为确认展开状态再验证，原失败证据保留。

该候选创建于本地配置提交之前，包含明确未提交增量，不宣称 clean-commit 正式发布。
