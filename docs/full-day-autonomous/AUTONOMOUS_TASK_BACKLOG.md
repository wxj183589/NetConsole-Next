# 自动任务池

更新：2026-11-06。总体 **PARTIAL**，详细任务 ID 与状态见 `NEXT_TASK_BACKLOG.md`。最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`；文档以所属提交为准。

已完成本机验证：任务并发/恢复、多命令Raw/Facts、诊断/LLDP、模块治理、R3 provenance/manifest、设备原子保存与密码自动读取、Netmiko网络连接和交互终端、ZTE凭据UI、Linux服务器终端与Site Jump Host。已提交增量为AP Group来源身份 `fda2a68`、候选缓存收敛 `2696390`、F10 conservative更换隔离 `764d764` 和配置备份入口/有界差异 `e12d062`。备份6/6、Chrome mock备份6场景、实际HTTP→Netmiko备份19 checks PASS。

本轮新增AUTO/legacy策略6组、worker24/24、Service实例共享worker上限16的预算6组、专用批量连接定向8/8、完整Web12 files/82 tests/typecheck、Chrome mock4组、实际HTTP→Netmiko→合成回环SSH14 checks均PASS。专用批量入口缺失已关闭，限显式库存UUID、独立H3C、只读版本命令，复用既有任务计划与统一Netmiko；FIT-AP仍经AC管理。Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。批量8组已纳入默认完整harness，配置备份6组为独立入口；最终Release构建0 warnings / 0 errors。

已完成本机最终整合与候选验收：功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`；候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`。fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖；`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS，STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups。候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。旧a3954a5候选仅作历史；当前候选不是正式发布。

当前模拟器：AC1/AC2/SW 通过 Netmiko 自动登录和只读命令；FIT-AP 由 AC 管理。不存在独立 FIT-AP 凭据或旧 SW Exec 前置阻断。

下一批：定义candidate确认/拒绝、identity合并/拆分、replacement权威写入/撤销与revision并发合同，以及可靠终止证据和Domain/group生命周期。F10已验证新旧身份、运行态、离线命令输出与来源隔离；它未实现上述写操作，也未从缺席推断ENDED。VLAN实时CLI采集、完整多厂商结构化业务、双AC HA/切换/恢复与实时归属、剩余工作台、原生Windows125%、跨机Web/TLS、目标机安装/SCM/升级/卸载和实机兼容继续待完成。

保持 `REAL_DEVICE_PENDING`，不写设备配置；保留当前分支，不 Push、Merge、Release，不访问旧 NetConsole 真实数据根。
