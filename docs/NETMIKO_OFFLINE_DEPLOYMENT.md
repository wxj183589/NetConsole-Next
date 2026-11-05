# Netmiko Windows 离线运行包

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

服务实际运行账户也必须能读取这些路径。重新构建以更新 worker 或依赖；包内 worker 必须与服务版本一起交付。构建与导入检查不连接设备，不代表设备兼容性、模拟器验收或生产验收。当前脚本只验证 Windows x64 CPython 3.13，其他平台和版本需要分别验证。
