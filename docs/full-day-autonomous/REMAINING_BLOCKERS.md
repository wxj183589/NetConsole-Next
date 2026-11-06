# 剩余阻断

日期：2026-11-06。整体状态：**PARTIAL**。最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`；文档以所属提交为准。

## 已关闭的本机整合项

- 最终Release Service/Tests构建0 warnings / 0 errors；Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。批量8组已纳入默认完整harness，配置备份6组保留独立入口。
- 当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖。`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS，STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups。候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。旧a3954a5候选只作历史；当前候选通过本机隔离验收，候选不是正式发布。

## 仍需实现或验收

1. 双AC HA、切换/恢复和AP实时归属尚无验收证据；当前AC1/AC2/SW Netmiko只读连接/命令PASS不能代替这些业务门禁。
2. 正式AP candidate/replacement与Domain/group生命周期、完整模块工作台仍需推进；F10隔离和候选缓存收敛不构成正式生命周期。
3. VLAN实时CLI采集及完整解析、原版完整AC命令/稳定组证据/CLI诊断导出、保存/启动配置和其他厂商完整结构化业务仍有缺口。通用断线自动重连/退避/命令重放尚未实现。
4. Windows原生125%显示、跨机器Web/TLS、目标机安装/SCM/升级/卸载尚未验收；当前候选本机隔离运行PASS不替代这些门禁，目标机仍需.NET10 ASP.NET Core Runtime。
5. 真实网络设备、Linux主机和实际堡垒机兼容仍 `REAL_DEVICE_PENDING`，不执行真实设备配置变更。

当前不存在“先取得 DPAPI 引用才能继续”的依赖。普通设备录入用户名/密码后自动关联 Profile 并自动认证，库存和 Profile 原子保存；FIT-AP 通过 AC 管理。历史 SW Exec、FIT-AP 独立 Telnet 和 Mutex 争用保留为历史证据，不再列作当前阻断。

专用批量连接入口缺失已关闭：定向8/8、Vue12 files/82 tests/typecheck、Chrome mock4组、guard后实际HTTP→Netmiko14 checks PASS；策略6组、worker24/24、共享worker16预算6组PASS。网络终端、Linux HTTP/WebSocket/Edge及Jump Settings Chrome等既有证据仍按原范围有效，最终harness、Jump和当前候选均已通过本机门禁。没有Push、Merge或Release，不把候选称作正式发布。
