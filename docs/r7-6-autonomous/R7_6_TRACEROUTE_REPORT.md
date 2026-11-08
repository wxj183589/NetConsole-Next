# R7.6 Traceroute 实现与启动对照报告

## 2026-11-08 最新集成检查点

本批已完成丢失采样历史上下文、实际Raw完整性复核并部署。产品56d16e3/6e48a0a，夹具与验收d99e103/8cd1e2f/0dd6a14。最新候选build/r76-loss-context-8cd1e2f，ProductVersion1.0.0+8cd1e2f96038acef6c63181ad49cb6d7c4920403，Service18771/PID41856 HEALTHY；Vite5174/PID32812保留。

当前Web36files/391tests、typecheck、Service全量EXIT0、loss37/37、候选发布、loss HTTP18与双视口Chrome18、最新候选广覆盖浏览器PASS。SQLite/140Raw逐项长度SHA256、单DB检查点与19静态资源均通过。本批未重跑完整离线门禁，之前ICMP_PERMISSION_DENIED失败未解除；真实客户端/AP关联为UNKNOWN，HA/设备恢复/SCM/8–24h仍未验。

完整当前制品身份、证据路径、数据影响和续跑步骤见[R7_6_PING_LOSS_EVIDENCE_REPORT.md](R7_6_PING_LOSS_EVIDENCE_REPORT.md)。本节取代下文旧候选/PID/测试数作为当前值；下文为此前批次实际证据，未重跑的检查不得视为本批通过。

## 此前批次证据

日期：2026-11-08。Agent：codex-A。源码基线：`cef8273`。状态：**PARTIAL / FINAL_OFFLINE_TRACEROUTE_FAILED_BOTH_MODES**。

本轮 Windows 生产路径复用普通进程令牌下的系统 ICMP tracert，并修复输出编码、原生错误分类和终点跳提前返回时可能吞掉 stderr 超限的问题。历史窄范围启动对照中显式 dotnet DLL 成功、apphost 返回原生错误码 5；最终集成候选的完整 APPHOST 和 DOTNET_DLL_EXPLICIT 门禁均因必需 Traceroute 的 ICMP_PERMISSION_DENIED 失败。不同证据分别保留，不能将历史 DLL 成功升级为最终完整门禁通过或断言具体系统策略原因。

## 产品实现与本轮修复

`50a0b0b` 在 Windows 生产 ICMP probe 下使用绝对系统路径 `tracert.exe`，`UseShellExecute=false`、无可见窗口，参数通过 ArgumentList 传入。先解析并冻结目标地址，再执行最多 30 跳、每跳等待最多 1000ms、受总预算限制的路径探测。实际来源为 `WINDOWS_SYSTEM_ICMP_TRACERT`；其他适用路径保留 `DOTNET_ICMP`，不伪装为 TCP 路由。

实现保留目标可达、TTL_EXPIRED、TIMED_OUT、UNREACHABLE 等观察；超时不制造路由器地址，unreachable 文本中含目标 IP 也不算到达。`af1b63b` 处理 Windows 原生 OEM/UTF-8 输出，`ce7032d` 将中英文原生 code 5 拒绝保留为 PingException/Win32Exception，而不是有效 hop；诊断服务将权限拒绝显示为 `ICMP_PERMISSION_DENIED`。

执行持续复核授权与目标；取消、截止或撤权时仅终止并回收自己启动的子进程。stdout、单行和 stderr 有明确上限，异常不转换成虚构成功路径。

`50db848` 修复终点跳早返回路径：到达终点、不可达或跳数上限后仍回收子进程，并排空已经产生的 stderr；正常完成路径中 stderr 超限必须向上报告。清理时保留原取消令牌与撤权异常，不由 stderr 错误覆盖。新增确定性门控回归，让子进程在终点进度返回前写出 65537 个 stderr 字符，分别核验 reached/unreachable 的超限传播，以及 cancel/revoke 原异常保留；另有干净终点及时回收和静默 stdout 的超限回归。

## 本轮已有启动对照

原始文件：`D:\study\NetConsole-Next-Workspace\reports\r76-trace-launch-comparison.json`。这是固定回环的部署诊断，发生在 Host/mutex/data-root 初始化前；两结果均 `dataRootAccessed=false`、`hostStarted=false`。不是 Web HTTP 或后台服务完整启动验收。

| 启动方式 | 原始结果 | 已证明范围 |
| --- | --- | --- |
| 显式 dotnet DLL | exitCode=0，state=PASS，target=127.0.0.1，ttl=1，status=Success，errors=[] | executable=`C:\Program Files\dotnet\dotnet.exe`，.NET 10.0.13 / X64；该启动上下文系统 ICMP 生产路径成功 |
| apphost EXE | exitCode=1，state=FAILED，status=null；PingException 内层 Win32Exception nativeErrorCode=5 | executable=`D:\study\NetConsole-Next-Workspace\build\r76-trace-permission-verified\NetConsole.Next.Service.exe`，.NET 10.0.13 / X64；该 apphost 启动上下文被拒绝 |

两结果 probeKind 均为 `WINDOWS_SYSTEM_ICMP_TRACERT`。相同 runtime/architecture 不消除启动上下文差异；错误码 5 不能单独证明 AppLocker、防病毒、用户权限或其他具体根因。本报告不修改系统策略、不尝试提权，也不把 DLL 成功写成 apphost 已修复。上述 apphost 路径不是新的集成候选目录。

## 自动化与最后验收

主任务确认 Service 全量 `50db848` EXIT 0；只读复核 `D:\study\NetConsole-Next-Workspace\reports\r76-service-full-final.log`，包含 `NETWORK_TRACEROUTE_TESTS=15/15 PASS` 和 WindowsSystemTraceroute 的 parser/command、进程取消/截止/撤权/输出回归 PASS。子进程 fixture 验证生命周期和解析保护，不代表远端真实多跳网络；本报告未重复运行测试或启动 Service。

当前集成候选：`D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63`；只读检查 ProductVersion=`1.0.0+7099775c7ded206d2b04d40faf4930e4d59c9297`。源码报告基线、候选目录名、制品版本和实际启动路径分别记录，不相互替代。

| 验收范围 | 当前结论 |
| --- | --- |
| 既有显式 DLL 固定回环部署诊断 | PASS，限原始对照报告 |
| 既有 apphost 固定回环部署诊断 | FAILED / nativeErrorCode=5，保持显式记录 |
| 最终集成候选 APPHOST / DLL 两种启动 | 完整离线 APPHOST / DOTNET_DLL_EXPLICIT 均 EXIT 1；两者 OFFLINE_TRACEROUTE=FAIL，state=PARTIAL、outcome=ICMP_PERMISSION_DENIED；必需门禁未被跳过 |
| 最终候选 HTTP 路径和双分辨率浏览器 | 离线 HTTP 门禁记录上述实际失败；共享候选 18771 / PID 49344 HEALTHY，但 Traceroute 实际 UI/hops 专项仍 NOT_TESTED，通用页面或 Long Ping 双视口通过不能代替 |
| 真实远端多跳、跨Site/跳板路径、无线切换 | NOT_TESTED；回环一跳不代表这些网络业务 |
| 现场路径异常→同资源恢复 | NOT_TESTED；权限拒绝不能冒充链路故障或设备离线 |

最终两模式的日志为 `D:\study\NetConsole-Next-Workspace\reports\r76-offline-apphost.log` 和 `r76-offline-dotnet.log`；入口是 `tests/offline_candidate_e2e.py --candidate <上述候选目录>`，DLL 模式额外传 `--dotnet-host "C:\Program Files\dotnet\dotnet.exe"`。两日志在完成备份计划等其余子项后均抛出 `AssertionError: Required Windows loopback Traceroute gate failed: state=PARTIAL; outcome=ICMP_PERMISSION_DENIED`；其余子项 PASS 不改变最终 EXIT 1。独立 Long Ping 回环 ICMP PASS 也不能替代 Traceroute 路径验证。

主集成另完成 18 次独立环境对照：诊断 APPHOST 原生 code 5，而显式 DLL 诊断/直接 tracert PASS；完整 Host 两模式仍失败。这与历史窄范围对照一致，但不是完整候选通过，也不证明具体系统策略根因；独立 ICMP 环境报告已于主线 cdf9e30 集成。共享实例 Vite 5174 / PID 32812 保持不动；本轮不通过改变主机策略、提权或弱化必需门禁处理拒绝。

本报告只新增文档，未启动 Service、重连设备、变更系统策略或复制数据。相关：[Ping](R7_6_PING_REPORT.md)、[Windows 部署](R7_6_WINDOWS_DEPLOYMENT_REPORT.md)、[故障中心](R7_6_FAULT_CENTER_REPORT.md)。
