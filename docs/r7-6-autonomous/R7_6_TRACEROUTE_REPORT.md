# R7.6 Traceroute 实现与启动对照报告

日期：2026-11-08。Agent：codex-A。源码基线：`cef8273`。状态：**PARTIAL / LAUNCH_MODE_DEPENDENT**。

本轮 Windows 生产路径复用普通进程令牌下的系统 ICMP tracert，并修复输出编码、原生错误分类和终点跳提前返回时可能吞掉 stderr 超限的问题。已有启动对照中显式 dotnet DLL 成功，apphost 返回原生错误码 5；两项必须分别保留，不能合并为候选全面通过或断言具体系统策略原因。

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
| 最终集成候选 APPHOST / DLL 两种启动 | PENDING_ROOT_FINAL_ACCEPTANCE：主任务补实际命令、版本、输出与退出码，不能复用旧路径结果 |
| 最终候选 HTTP 路径和双分辨率浏览器 | PENDING_ROOT_FINAL_ACCEPTANCE：补生产API的 probeKind、实际 hops/失败、截图与运行实例 |
| 真实远端多跳、跨Site/跳板路径、无线切换 | NOT_TESTED；回环一跳不代表这些网络业务 |
| 现场路径异常→同资源恢复 | NOT_TESTED；权限拒绝不能冒充链路故障或设备离线 |

本报告只新增文档，未启动 Service、重连设备、变更系统策略或复制数据。相关：[Ping](R7_6_PING_REPORT.md)、[Windows 部署](R7_6_WINDOWS_DEPLOYMENT_REPORT.md)、[故障中心](R7_6_FAULT_CENTER_REPORT.md)。
