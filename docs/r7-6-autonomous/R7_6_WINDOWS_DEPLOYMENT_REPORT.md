# R7.6 Windows 部署事实报告

日期：2026-11-08。Agent：codex-A。报告基线：`39c2827`。状态：**PARTIAL / SCM_NOT_TESTED**。

本轮完成 LocalManager 非法 JSON PID 的就绪诊断，以及 Windows PowerShell 5.1 安装计划路径兼容修复。两个 shell 的离线计划与语法门禁通过；尚不能据此宣称真实 SCM 安装、恢复或最终发布候选通过。

## LocalManager 就绪诊断

主线提交 `89682f8` 修改 `LocalServiceReadiness.cs`，在 `TryGetInt32` 前检查 `JsonValueKind.Number`。健康接口即使返回 `HEALTHY` / `running`，字符串、布尔、null、数组、对象、越界整数、小数、零和负数 PID 均不会提前报告就绪，也不会以 JSON 类型异常中断等待；它们最终进入带 `processId=missing` 的有界 `SERVICE_NOT_READY` 诊断。

既有 loopback 健康探测、超时、响应上限和正常正整数 PID 语义保持。此修复不扩大为对任意健康服务的进程所有权保证；真实 SCM PID、安装路径和实例身份仍须由部署验收独立核对。

| 定向检查 | 本轮已执行结果 | 范围 |
| --- | --- | --- |
| LocalServiceReadiness | 4/4 PASS | 启动重试、降级超时、响应上限、9 种非法 PID |
| Portable runtime | 9/9 PASS | LocalManager 既有独立测试组 |
| Deployment doctor | 13/13 PASS | 合成 HTTP / 最小目录 fixture |
| LocalManager / 测试 Release build | 0 warnings / 0 errors | 对应修复 worktree |

## Windows PowerShell 5.1 与 pwsh 7

主线提交 `39c2827`（隔离工作流原提交 `d5ccef5`）替换 `[IO.Path]::IsPathFullyQualified`。Windows 自带 5.1 使用 .NET Framework，没有该方法；修复前以 `tests/r73_scm_plan_tests.ps1` 复现 `MethodNotFound,Get-InstallationPath`。本机脚本签名策略先阻止了不带执行策略参数的命令；之后仅对测试进程显式使用 `-ExecutionPolicy Bypass`，未修改机器或用户执行策略。

新校验要求盘符及根分隔符，兼容 `D:\...` / `D:/...`；继续拒绝相对路径、`D:relative`、当前盘根路径、UNC、设备/扩展命名空间和尾点/尾空格别名。既有旧 NetConsole 数据根、卷根、二进制/数据目录重叠、重解析祖先、无标记非空目录及无效 marker 检查保持。

| 门禁 | Windows PowerShell 5.1 | pwsh 7 |
| --- | --- | --- |
| `tests/service_installation_plan_tests.ps1` | 27/27 PASS | 27/27 PASS |
| `tests/r73_scm_plan_tests.ps1` | 5/5 PASS | 5/5 PASS |
| `tests/validate_windows_scripts.ps1` | 7/7 PASS | 7/7 PASS |
| `git diff --check` | PASS | 同一补丁 |

命令分别为 `powershell -NoProfile -ExecutionPolicy Bypass -File <上述脚本>` 和 `pwsh -NoProfile -ExecutionPolicy Bypass -File <上述脚本>`，从隔离仓库根运行。测试只生成不执行的 EXE 文本 fixture，并验证计划不会创建数据目录或报告文件。Junction 用例仅在 fixture 内创建并精确移除链接；生成 fixture 完成后退役。

`r73_scm_plan_tests.ps1` 明确输出 `SCM lifecycle NOT_TESTED`。语法组只解析七个既有脚本，不能代替实际安装、升级或卸载执行。

## 候选启动方式与 ICMP 证据边界

`tests/offline_candidate_e2e.py` 默认启动候选 EXE；`--dotnet-host <绝对 dotnet.exe>` 为显式独立 DLL 门禁，并输出 `OFFLINE_LAUNCH_MODE=DOTNET_DLL_EXPLICIT`。它不在 EXE 失败后自动重试 DLL，所有重启/子门禁沿用指定模式。Traceroute 失败即使先完成独立 schedule 检查，仍最终抛错并返回失败，不能因部分 PASS 标志宣称整体通过。

本工作流此前的窄范围部署诊断观察到 EXE/apphost 原生 ICMP transmit code 5 与显式 `dotnet DLL` 成功不同；这是 Host 初始化前的历史对照。最终候选的完整 APPHOST 和 DOTNET_DLL_EXPLICIT 离线门禁均已执行，均因必需回环 Traceroute 的 `state=PARTIAL; outcome=ICMP_PERMISSION_DENIED` 失败并 EXIT 1。历史 DLL 诊断成功不等于最终完整 DLL 门禁通过，也不能推定具体系统策略根因。

原始日志为 `D:\study\NetConsole-Next-Workspace\reports\r76-offline-apphost.log` 与 `r76-offline-dotnet.log`。命令入口为 `tests/offline_candidate_e2e.py --candidate D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63`；DLL 门禁额外显式传入 `--dotnet-host "C:\Program Files\dotnet\dotnet.exe"`。实际 Python/runpy 引导命令保留于两份日志的 traceback；本报告只读复核，没有再次启动服务。除 Traceroute 外，两日志的 bundled runtime、静态 UI 资产哈希、回环 Netmiko 连接/重连、诊断 TXT/ZIP、Linux WebSocket、AC Raw/Facts、配置种类、完整诊断、诊断历史及备份计划子项均 PASS。静态资产 HTTP 校验不能代替浏览器操作，回环合成设备不能代替物理设备。

## 最终验收与未完成项

| 项目 | 结果与待补内容 |
| --- | --- |
| 最终 R7.6 Windows x64 候选路径 / commit / manifest | `D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63`；Service.exe ProductVersion=`1.0.0+7099775c7ded206d2b04d40faf4930e4d59c9297`；目录后缀不替代嵌入 commit，完整 manifest 本报告未独立核验 |
| 最终候选离线 E2E | APPHOST 与 DOTNET_DLL_EXPLICIT 均 EXIT 1；必需 live ICMP Traceroute 为 PARTIAL / ICMP_PERMISSION_DENIED；其他已列子项 PASS，整体 FAILED |
| 最终候选双分辨率浏览器 | NOT_VERIFIED；1920×1080 / 2560×1440 的最终实际操作、截图和错误统计尚无完成证据 |
| 最终候选单 DB checkpoint / SQLite / Raw 完整性 | Long Ping 最小 fixture 的重启后 SQLite integrity PASS；离线任务 TXT/ZIP 与 AC Raw 哈希子项 PASS；最终运行实例的单 DB checkpoint、整体 SQLite/Raw 核对尚未完成，不以 fixture 代替 |
| 真实 SCM 安装 / 启动 / 重启 / 卸载 | NOT_TESTED |
| SCM 自动启动、虚拟账户、崩溃恢复 | NOT_TESTED；未以离线 plan 代替验证 |
| 真实升级失败及 binary fallback | NOT_TESTED；数据库 schema 兼容性仍需独立验证 |
| 独立 Windows 主机、长期运行、跨版本兼容性 | NOT_TESTED |
| 真实设备 Writer / 整机自动 rollback | NOT_TESTED；范围详见配置报告 |

## 数据与交接

本专项未运行 install/update/uninstall 的真实执行分支，未调用真实 SCM 或写注册表，未停止或启动运行实例，未执行设备命令。没有复制完整真实数据根；未访问旧 NetConsole 的 Production / Dev 数据。源码补丁与报告均仅本地提交，未 push、远端 merge 或正式发布。最终候选、浏览器和 DevStatus 由主集成统一收口。

参考：[Windows 安装规范](../WINDOWS_SERVICE_INSTALLATION.md)、[LocalManager 操作规范](../LOCAL_MANAGER_OPERATIONS.md)、[R7.5 部署报告](../r7-5-autonomous/R7_5_DEPLOYMENT_REPORT.md)、[R7.6 配置恢复报告](R7_6_CONFIG_RESTORE_REPORT.md)。
