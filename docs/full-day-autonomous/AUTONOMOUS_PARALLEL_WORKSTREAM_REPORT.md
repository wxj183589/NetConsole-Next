# 并行工作流报告

## 2026-11-06 当前集成状态

最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`；文档以所属提交为准。分支 `codex-A/netconsole-next-r3-dual-ac` 保留，不Push、Merge、Release。AP Group来源身份、候选缓存、F10 conservative隔离与配置备份已各自本机验收。本轮连接策略、资源预算、批量API/UI、Terminal reason修复及文档工作已在共享分支集成；整体 **PARTIAL**。

AUTO/legacy策略6组、worker24/24、Service实例共享worker16预算6组、专用批量定向8/8、完整Vue12 files/82 tests/typecheck、Chrome mock4组、guard后实际HTTP→Netmiko→合成SSH14 checks均PASS。最终Release构建0 warnings / 0 errors；Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。批量8组已纳入默认完整harness，备份6组为独立入口。

当前候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，EXE ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖。`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS，STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups。候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。候选不是正式发布；HA/生命周期/VLAN与完整多厂商业务、原生显示/跨机/安装和实机边界保留，`REAL_DEVICE_PENDING`。

## a3954a5连接批次历史范围

`a3954a5`是早前连接批次本地提交，其Service harness、Vue8 files/23 tests、worker20、离线工具7、网络终端、Linux/Jump及离线候选的PASS仅属于该时点。旧候选 `build/service-web-linux-jump-a3954a5-20261106` 不包含后续Group/cache/F10/配置备份及本轮策略/预算/批量入口，不能作为当前候选或最终整合PASS。

## 以下为早期工作流历史记录

已集成工作流：调度/并发（`493cc54`）、连接/HostKey/Telnet（`8a6ab3b`、`e75018a`、`b4aadb7`）、设备/UI（`7b667da`）、主线多命令与事实管线（`b5380e0`）。各工作流使用独立 worktree，最后在产品 worktree 集成。未推送或合并远端。
