# R7.0 Windows 部署与运行诊断

- 日期：2026-11-07；Agent：codex-A。
- 状态：**PARTIAL**。新增诊断命令、定向自动测试和现有运行实例验证 PASS；本轮 Windows SCM 安装/崩溃重启实测 **NOT_TESTED**。

## 实际代码成果

`src/NetConsole.Next.LocalManager/LocalDeploymentDoctor.cs` 和 LocalManager `Program.cs` 新增：

```powershell
NetConsole.Next.LocalManager.exe doctor [data-root] [port]
```

默认复用 `NETCONSOLE_NEXT_DATA_ROOT` / `NETCONSOLE_NEXT_WEB_PORT`；Windows 未配置 root 时使用已有部署默认 ProgramData 位置。doctor 不创建数据根、不初始化数据库、不连接设备。

诊断按次序执行：

1. 验证已有 root、项目 marker，以及路径不经过 Reparse Point。
2. 以 SQLite `Mode=ReadOnly`、`Pooling=false` 读取实例 identity。
3. 只请求 `127.0.0.1` 健康端点；禁用重定向和代理；响应限制 64 KiB，超时 5 秒。
4. 比较健康端点和 root 数据库的 instance ID，拒绝把错误端口上的其他进程当成本实例。
5. 检查运行状态、有效 PID、健康状态、服务报告的启动 SQLite 完整性和 Raw writer readiness。
6. 使用配置的/随包附带的现有 Python Netmiko worker 执行 `close` 握手，验证依赖导入及协议可用。无设备认证、无设备命令。

结构化 JSON 包含每项 PASS/FAIL、可读原因及建议动作，失败退出码非零。数据库可读和启动完整性是不同检查；doctor 不声称执行了新的完整数据库 integrity scan，也不声称 worker close 握手证明设备互操作。

现有 LocalManager `start`/`stop` 增加 StartPending/StopPending 等待，减少重复点击或状态过渡时再次发送同一 SCM 动作的失败。没有引入新守护进程或 PID 猜测式终止逻辑。

## 复用的部署机制

现有 `scripts/install-service.ps1` 已实现 Windows 服务自动启动、受限虚拟服务账户/目录 ACL，以及 5/15/60 秒 SCM 崩溃重启策略。卸载保留数据。`tools/publish_windows_offline.py` 已支持当前前端构建与离线 Netmiko bundle 校验。此轮复用这些机制，未重复实现部署架构。

`tests/windows_service_lifecycle.ps1` 新增重复 start/stop 幂等调用以及安装后 doctor 的 matching-instance/worker 断言。该脚本原有安装、令牌、SCM 启停、强制崩溃恢复和卸载数据保留测试继续保留；本轮没有在主开发机执行该破坏性安装/终止流程。

## 测试结果

```powershell
dotnet run --project tests/NetConsole.Next.LocalManager.Tests/NetConsole.Next.LocalManager.Tests.csproj
```

**10/10 PASS**：健康 matching instance；数据库字节未变；错误端口实例拒绝；健康降级/SQLite 失败；worker 缺失；HTTP 离线；超大响应；错误 JSON 形状；未标记/不存在目录拒绝；实际 Netmiko worker 导入和 close 握手。测试只创建最小临时 SQLite，位于 Next Workspace test-data，完成后回收本次 fixture。

`tests/validate_windows_scripts.ps1`：全部 PowerShell 语法 PASS，含修改后的 Windows lifecycle 脚本。

实际 CLI：

```powershell
dotnet run --project src/NetConsole.Next.LocalManager/NetConsole.Next.LocalManager.csproj -- doctor D:\study\NetConsole-Next-Workspace\test-data\simulator-live-20261105 18771
```

返回 EXIT 0、全部检查 PASS，准确识别当时现有 Service PID `68272`，健康 HEALTHY、SQLite startup `ok`、Raw 可写、worker handshake 成功。此命令没有重启或停止已有 Service/Vite。该 PID 是本次诊断时点记录；最终运行 PID/端口/日志和停止命令以 `R7_FINAL_ACCEPTANCE.md` 的最后一轮环境核验为准。

## 未完成与数据影响

- 新的离线发布候选/最终运行验收由集成负责人记录；不能用本项 CLI 验证替代安装包验收。
- 本轮未重新执行 SCM 安装、虚拟账户实机采集、SCM 强制崩溃恢复、开机自启、卸载数据保留：**NOT_TESTED**。
- 未复制完整真实数据，未访问旧 NetConsole 数据根，未改现有运行数据的业务内容；诊断只读打开 Next 的现有 catalog。
- 无 push、merge 或 release publish；分支为 `codex-A/netconsole-next-r3-dual-ac`，提交由集成负责人统一处理。
