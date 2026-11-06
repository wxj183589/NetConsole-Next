# 自动任务池

更新：2026-11-06。总体 **PARTIAL**，详细任务 ID 与状态见 `NEXT_TASK_BACKLOG.md`。

已完成本机验证：任务并发/恢复、多命令 Raw/Facts、诊断/配置备份/LLDP、模块治理、R3 只读 provenance 与 manifest、设备快速添加原子保存、普通密码自动读取、Netmiko 网络连接和交互终端、ZTE 凭据 UI、Linux 服务器终端、Site Jump Host 及最终离线候选运行。

当前模拟器：AC1/AC2/SW 通过 Netmiko 自动登录和只读命令；FIT-AP 由 AC 管理。不存在独立 FIT-AP 凭据或旧 SW Exec 前置阻断。

下一批：正式 AP candidate/replacement 和 Domain/group 生命周期，双 AC HA/切换/恢复与实时 AP 归属，完整工作台，Windows 原生 125%，跨机 Web/TLS，目标机安装/SCM/升级/卸载与实机兼容。保持 `REAL_DEVICE_PENDING`，不写真实设备配置。

当前分支保留；不 Push、Merge、Release；不得触碰旧 NetConsole 真实数据根。
