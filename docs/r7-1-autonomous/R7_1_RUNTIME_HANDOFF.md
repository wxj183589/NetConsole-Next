# R7.1 运行环境交接

日期：2026-11-08；Agent codex-A；整体 PARTIAL。产品源码分支 `codex-A/netconsole-next-r3-dual-ac`，只做本地提交，不 Push/Merge/Release。

## 直接使用

- Service 与已构建 Web UI：`http://127.0.0.1:18771`。
- Vite 开发前端：`http://127.0.0.1:5174`，API 代理18771。
- 既有 Web 用户未重置；使用现有账号。凭据、设备密码、local-api-token、私有完整配置不复制到报告或 Git。
- 数据根：`D:\study\NetConsole-Next-Workspace\test-data\simulator-live-20261105`。
- Service DLL：`D:\study\NetConsole-Next-Workspace\build\r71-service\NetConsole.Next.Service.dll`。
- UDP Syslog：本机 loopback15514；本轮消息来自本地合成验收，不代表设备上报。普通默认设置仍关闭。

服务在交付时保留运行；本次是交互进程，不能把它描述为已完成 SCM 安装或开机自启。

## 数据、日志和资源

运行根内 `catalog.db`、`raw/`、`operations/fault-center.json`、`backup/config-complete/` 与计划持久文件为本次 Next 独立运行状态。私有 `.cfg` 不可按脱敏导出分享；公开 `.view.cfg` 仍应按实际分享用途复核。

控制台日志：Workspace `reports/r71-service.log`、`r71-service.err.log`；启动健康与安全控制证据 `r71-start-health.json`、`r71-console-control.json`。所有验收 JSON、截图及下载在 Workspace `reports/r71-*`；不迁入 DevStatus。

完整配置新增发布默认全局1GiB、每profile1000份；达到阈值只拒绝新增，不自动删除。环境变量和范围见 [配置专项](R7_1_CONFIG_MANAGEMENT_REPORT.md)。其它 Raw、普通备份和整个 DataRoot 不受该专项配额覆盖，仍须关注磁盘空间。完整批次内存最多128份，计划尚未观察的批次暂时保留；新增 `backup/config-batches.json` 最多128份终态元数据、文件上限4MiB。旧内存批次无法追溯恢复，页面明确提示；它不是配置文件保留策略。

Worker 受 Service 的16个进程预算管理。不要终止其它 Python、Vite、dotnet 或共享控制台；不要并行启动第二个使用同一数据根的 Service。

## 受控重启与诊断

在仓库根执行；先从 `/api/system/health` 取得当前 processId，再以该精确 PID 替换以下占位，确认没有正在执行的用户任务：

```powershell
python tests/r70_runtime_control.py stop --pid <当前processId> --expected-dll D:/study/NetConsole-Next-Workspace/build/r71-service/NetConsole.Next.Service.dll --report-prefix r71
python tests/r70_runtime_control.py start --expected-dll D:/study/NetConsole-Next-Workspace/build/r71-service/NetConsole.Next.Service.dll --report-prefix r71 --syslog-port 15514
```

工具核对 PID/命令行/DLL 及独占控制台后发送受控退出，不做强杀。PID 会在每次重启后变化，以实际 health 为准。先停服再覆盖运行 DLL；持久计划启动后按既有不追赶语义恢复。

LocalManager `doctor <DataRoot> 18771` 检查受管根、SQLite标识、HTTP实例和 Worker 握手；以最终 doctor JSON 为准。安装入口、PlanOnly、权限与实际 SCM 门禁边界见 [Windows部署报告](R7_1_WINDOWS_DEPLOYMENT_REPORT.md) 和 [Worker专项](R7_1_DEPLOYMENT_WORKER_LIFECYCLE_REPORT.md)。

## 继续开发

先读 [最终验收](R7_1_FINAL_ACCEPTANCE.md)、[剩余问题](R7_1_REMAINING_GAPS.md)、[测试证据](R7_1_TEST_EVIDENCE.md) 及真实 Git status。DevStatus 另有并发 dirty 修改，不能覆盖或一并提交。设备操作继续使用精确 Site/profile 和既有 Netmiko；FIT不猜密，SW GE1/0/1共享管理链路不能关闭。

自然计划 `6334c82406a348689bf29dd186137351` 已成功生成AC1版本6并通过API暂停，nextRunMs=null；见自然观察JSON与配置专项。追加批次历史修复与真实SW跨重启浏览器已PASS；新SW计划同样暂停。最终Service PID `85584`，Vite PID `37420`，运行产品提交 `2cb89b2`，DLL SHA256 `6087d3b479cb029a5514474007f9cb71bce8246fb4bfc6ebba7e8a75b9bcfd72`。HEALTHY、doctor PASS、两个UI HTTP200；详细快照 `reports/r71-runtime-history-final.json`。PID为本次采样值，重启后须重新读取。
