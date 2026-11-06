# Netmiko Windows 离线运行包

## 当前准确版本候选（2026-11-06）

当前候选为 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，EXE ProductVersion 精确为 `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`。复用 Worker 未变化的 `build/netmiko-reconnect-final-20261106`，内置 CPython3.13、Netmiko4.7.0、Paramiko4.0.0及18个固定版本/hash wheel。构建session2553 EXIT0，当前Vue源码构建与复制前后driver/Telnet/JSON smoke PASS。

`py -3.13 -B tests/offline_candidate_e2e.py --candidate <当前目录>`，session16200 EXIT0。Production、独立cwd/数据根、PATH仅System32且清除Python/Worker覆盖，7项均PASS：`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE`。候选使用自身运行时，覆盖VLAN/任务诊断入口、TXT/ZIP封存hash及不重采集、只读命令断线恢复且不重放已完成命令。

准确证据见 [统一架构验收](NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md) 和 Workspace `reports/netmiko-final-9fc499f-20261106.json`。这是本机候选运行验收，framework-dependent Service仍需目标机.NET10 ASP.NET Core Runtime；SCM安装、升级卸载及其他Windows机器继续待验收。未Push、Merge或Release。

## 构建与部署方法

目标为 Windows x64 和 CPython 3.13。构建机需要本机安装 CPython 3.13 x64（含 pip）、仓库源码和 `tools/netmiko_requirements.txt` 全部固定版本对应的本地 wheel。构建不会下载依赖，缺少兼容 wheel 时直接失败。wheel 可以预先放在离线 wheelhouse，也可以读取本机 pip 缓存；wheel 来源和获取方式由部署人员核验。

```powershell
py -3.13 tools/package_netmiko_offline.py build --name netmiko-offline-20261106 --wheel-source C:\path\to\wheelhouse
py -3.13 tools/package_netmiko_offline.py verify D:\study\NetConsole-Next-Workspace\build\netmiko-offline-20261106
```

构建目录必须不存在。它包含隔离 CPython、安装后的依赖、worker、wheelhouse、带 SHA256 的 requirements.lock 和整个包的 manifest.json。构建使用 `--no-index --require-hashes`，并启动隔离运行时检查全部 worker 驱动映射、JSON 管道及 Telnet 回环读写。Netmiko 4.7 使用自带的 Telnet 实现，无需 Python 标准库的 `telnetlib`。目标机器可以整体复制该目录，复制后再次执行 verify；verify 需要独立的构建工具 Python 3.13。manifest 用于发现内容变化，不提供签名或来源认证，跨机器交付时应通过可信渠道保护 manifest。

将服务环境变量指向完整路径：

```powershell
$env:NETCONSOLE_NEXT_NETMIKO_PYTHON = 'D:\path\to\bundle\runtime\python.exe'
$env:NETCONSOLE_NEXT_NETMIKO_WORKER = 'D:\path\to\bundle\netmiko_worker.py'
```

也可将运行包放在服务程序目录的 `tools/netmiko/` 下，服务会自动发现其中的 `runtime/python.exe` 和 `netmiko_worker.py`。显式环境变量优先。通过以下入口将已验证的运行包与 Windows Service 组合为新的构建候选：

```powershell
py -3.13 tools/publish_windows_offline.py --name service-offline-candidate --bundle D:\study\NetConsole-Next-Workspace\build\netmiko-offline-20261106
```

该命令要求 `build/` 下候选目录不存在，校验清单、当前 worker 和依赖版本，并在复制前后执行隔离运行检查。它使用 `dotnet publish --no-restore`，构建机须事先准备 .NET 10 SDK、Windows x64 的 NuGet 恢复资产及依赖；不会自动获取这些资产。结果位于 `build/service-offline-candidate`，不是正式发布。此候选采用 framework-dependent 发布，目标机仍需事先安装匹配的 Windows x64 .NET 10 ASP.NET Core Runtime，不能把它称作完全自包含的 .NET 离线安装包。

需要隔离中间构建目录时，先用 `dotnet restore ... -r win-x64 --artifacts-path <workspace-build-directory>` 恢复该目录，再向组合命令传入其 `build/` 下的目录名 `--artifacts-name <name>`。离线恢复应配置本地 NuGet 源和缓存；它与 Netmiko wheelhouse 是不同的依赖来源。

组合工具还要求构建机已有 Node.js 及准备好的 `web/node_modules`。它直接调用本地 Vite，在 Workspace `build/` 临时目录从当前 Vue 源码构建页面，再覆盖候选的 Webroot；不会下载 npm 依赖，也不修改源码 `wwwroot`。缺少入口文件或入口引用的资源时构建失败，避免新服务意外附带旧 Linux/终端/中转页面。目标机运行候选无需 Node.js 或系统 Python。

历史连接批次候选为 Workspace `build/service-web-linux-jump-a3954a5-20261106`，程序集 ProductVersion 为 `1.0.0+a3954a5e95590782a576272d4e4b3c526eab248b`，内含 `tools/netmiko/`；构建时前后隔离 driver/Telnet smoke PASS。此包早于 Group/cache/F10/配置备份及最新连接/批量测试增量，不作为当前候选。最新准确功能提交的候选及运行证据见 [本批验收](WEB_SSH_LINUX_JUMP_ACCEPTANCE_20261106.md)。这不替代目标机安装、Windows SCM 生命周期或升级卸载验收。

前序本机候选为 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，EXE ProductVersion 为 `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；配套 bundle 为 `build/netmiko-8320e2f-20261106`。该检查点的 Vue 源码构建、复制前后 driver/Telnet smoke，以及 Production/独立 cwd-data/PATH 仅 System32 的候选运行五项门禁均 PASS，覆盖内置依赖、HTTP 资源 hash、设备批量与备份页面可达、回环 SSH Netmiko 登录和 Linux WebSocket。Service/Python 及该轮临时目录残留为 0；历史证据见 Workspace `reports/netmiko-final-8320e2f-20261106.json`。此结果仅为该历史版本的本机候选运行验收，不代替本文顶部当前版本，未安装 SCM，未发布。

服务实际运行账户也必须能读取这些路径。重新构建以更新 worker 或依赖；包内 worker 必须与服务版本一起交付。构建与导入检查不连接设备，不代表设备兼容性、模拟器验收或生产验收。当前脚本只验证 Windows x64 CPython 3.13，其他平台和版本需要分别验证。
