# R7.1 G：Local Manager 部署诊断与 Worker 生命周期

- 日期：2026-11-08；Agent：codex-A；分支：`codex-A/netconsole-next-r3-dual-ac`。
- 本专项：定向验证 **PASS**；Windows 服务安装、SCM 停止/重启与断电恢复未在本专项执行，不能据此宣称现场部署验收完成。
- 不连接任何模拟器或设备，不启动第二服务，不终止其它进程。只读现有 Next 隔离根及已有报告；未读取或修改旧真实数据根。

## 产品缺陷与修复

`LocalDeploymentDoctor.RunAsync` 原先在拒绝旧根之前执行 `Directory.Exists`；Local Manager 的 `ResolveManagedRoot` 和 `open-*` 也缺同等词法边界。

新增 `ManagedDataRootPath.Normalize` 共用函数，先进行字符串路径规范化与拒绝，再进入 Directory/File/marker/SQLite 检查。拒绝 `D:\NetConsoleData`、`D:\NetConsoleData-dev` 及子路径，包含大小写、斜杠、`..`、Windows 尾随点/空格和设备 namespace 形式；独立的 `NetConsoleData-next` 不因相似前缀被误拒绝。doctor、凭据/token 的受管根解析、目录打开入口复用该函数，并返回有界 CLI 错误。

拒绝测试仅向新函数和 CLI 传递路径字符串；旧根的拒绝发生于文件系统检查前。测试没有查询旧根是否存在，也没有打开其文件。

## Worker 释放合同

- Python worker 的 `close` 依次释放设备、Jump channel、Jump client，单个资源关闭异常不会妨碍其它资源释放，重复 close 不重复断开。
- 产品 transport 的正常 `Dispose` 使用 `Abort` 终止其自有进程树，不向进行中的 JSON 流插入未排队的 close。它不是 graceful close handshake。
- 命令取消/异常会废弃流并 Abort，防止迟到响应被下一条命令消费；超时与 EOF 不成为成功输出。
- `NetmikoWorkerBudget` 16 个共享槽仅在观察到实际进程退出后归还；无法确认退出则继续占槽，不提前放行更多活进程。
- `BoundedCollectionScheduler.DisposeAsync` 取消在途 token、等待工作项，finally 释放 session，传递到真实 transport 的子进程清理。

本专项在现有 `NetmikoWorkerBudgetTests` 中增加实际合成子进程验证。测试使用当前测试 DLL 作为 worker，仅 stdin/stdout 合成协议，不创建网络连接；记录 PID 并以 `WaitForExitAsync` 确认退出，同时确认所有预算槽归还。覆盖：idle Dispose；在途命令 CANCEL、ABORT、DISPOSE；worker 返回 COMMAND_TIMEOUT；worker EOF exit；真实 scheduler shutdown → session → transport。最后一种还验证任务进入 CANCELLED。

最终一次结果：CANCEL PID 106980、ABORT 92884、DISPOSE 34664、TIMEOUT 53816、EXIT 96828、scheduler shutdown 104404 均已退出，预算全返。PID 是本次合成 worker 的身份，不用于后续进程控制。未发现必须改动生产 transport 的新缺陷，保留其现有释放策略。

这里的 TIMEOUT 是 worker 明确返回 COMMAND_TIMEOUT；取消测试验证父进程在挂起命令时主动清理。未人为等满产品的 45 秒兜底时间，不能将此称为 45 秒实际超时计时验收。

## 本机 doctor 实测

Release Local Manager 对现有 `D:\study\NetConsole-Next-Workspace\test-data\simulator-live-20261105`、loopback 18771 执行 doctor，EXIT 0，七项 PASS：受管根、只读 Catalog、instance ownership、HEALTHY/running、SQLite startup ok、Raw writer、真实 Python worker import/close。匹配运行服务 PID 101804；doctor 未连接设备。

证据：`D:\study\NetConsole-Next-Workspace\reports\r71-doctor-lifecycle-batch2.json`。该结果是当次观测；PID/运行健康可能随服务重启改变。

Release CLI 还实测旧根字符串提前拒绝：doctor EXIT 2，open-data EXIT 2，api-token EXIT 1；没有输出 token，也没有启动 explorer。

## 验证

所有 .NET 命令设置进程级 TEMP/TMP 为现有 `D:\study\NetConsole-Next-Workspace\build\r71-device-tests`；未改变全局环境变量。

- `dotnet run --project tests/NetConsole.Next.LocalManager.Tests -p:ArtifactsPath=D:/study/NetConsole-Next-Workspace/build/r71-device-tests`：11/11 PASS，包含七种旧根字符串、同前缀独立根、现有十项 doctor 行为和真实 worker close。
- `dotnet run --project tests/NetConsole.Next.Service.Tests -p:ArtifactsPath=D:/study/NetConsole-Next-Workspace/build/r71-device-tests -- --netmiko-worker-budget-tests`：9 组 PASS，EXIT 0；真实自有子进程退出与全部 16 槽归还。
- `python -m unittest discover -s tests -p netmiko_worker_test.py`：46/46 PASS，含重复 close、单资源关闭异常及清理。
- `dotnet build src/NetConsole.Next.LocalManager -c Release -p:ArtifactsPath=D:/study/NetConsole-Next-Workspace/build/r71-device-tests`：0 warnings / 0 errors。
- 本专项文件 `git diff --check`：PASS。

没有执行 MSI/SCM 安装、系统账号权限切换、活设备会话中停止 Windows 服务、系统重启/断电或非 Windows 实机验收。已有真实 doctor/worker 报告只证明其记录的范围。本专项文件已交主集成负责人，commit/push 统一处理。

## Service 数据根别名补充

主集成复查要求同等修复 `RuntimeSettings.ValidateDataRoot`。该入口原先只比较 GetFullPath 后的精确旧根，Windows 中间路径段的尾随点/空格和设备 namespace 可能绕过拒绝而进入 `ValidateNoReparsePoints`。

在 `RuntimeSettings.cs` 内提取无 IO 的 `NormalizeDataRootName`，由原入口首先调用，再进入既有 reparse 检查。算法与 Local Manager 保持一致：拒绝设备 namespace，按 Windows 词法规范化比较名，禁止两旧根及子路径，同时保留相似但独立的目录名。Service 没有引入对 Local Manager 的依赖。

`RuntimeSettingsTests` 以 17 种 filesystem/legacy/alias/namespace 字符串验证：纯词法函数和完整入口必须产生相同的无 InnerException 拒绝，不能进入包装后的祖先检查错误；3 种相同前缀独立根只通过纯词法函数验证，未对工作区外目录做 Exists/属性检查。原有 Workspace 内不存在 candidate 的完整校验仍通过且不创建目录。

`dotnet run --project tests/NetConsole.Next.Service.Tests -p:ArtifactsPath=D:/study/NetConsole-Next-Workspace/build/r71-device-tests -- --runtime-settings-tests`：EXIT 0，Web loopback 与数据根测试均 PASS。本补充只修改 RuntimeSettings.cs、其测试和本报告；没有连接设备、重启服务或访问旧真实根。

## Local Manager 祖先 junction 补充修复

后续只读部署复核发现真实缺陷：`ResolveManagedRoot` 只检查根目录本身的 reparse 属性；当受管根是普通目录但祖先是 junction 时，marker/token/credential/password 操作仍可沿别名访问目标。`open-*` 原先只做词法校验和 Exists，同样缺少祖先保护。这与 doctor 已有的祖先检查不同，前述词法拒绝不能覆盖该情形。

`ManagedDataRootPath.ResolveExistingDirectory` 现在先运行原有无 IO 词法拒绝，再从文件系统根逐级检查属性，遇到 reparse 或非目录立即拒绝，避免先通过 junction 读取其子目录。受管根解析、open-* 根及 raw/logs/published 目标均复用该函数。credential CLI 同时捕获该拒绝产生的 InvalidOperationException，返回有界错误。

定向测试在 Workspace GUID fixture 中创建实际 Windows junction 指向另一个合成目录；leaf 本身明确不是 reparse point，marker 是合成值。测试证明直接 junction、普通 leaf 经 junction 祖先、junction 下不存在的 child 均在访问子项前拒绝，目标 marker 保留，普通路径仍可用。测试只精确移除自己创建的 junction，再回收自身 fixture，没有指向或访问任何旧真实根。

LocalManager 定向测试 **12/12 PASS**（原11组保留，新增祖先 junction 组）；Release build **0 warnings / 0 errors**，diff check PASS。构建后的真实 CLI 使用另一个 Workspace 合成 junction 验证：api-token EXIT1、credential revoke EXIT1、open-data EXIT2，均为有界拒绝且目标 marker 不变，未输出 token、未启动 Explorer、未连接设备；fixture 精确退役。未重复已通过的 Service/Python worker 测试，也未执行设备会话或 SCM 安装。发布构建与最终集成由主代理记录。

### Doctor 同步使用逐级保护

主集成复核指出 doctor 原有 `Directory.Exists(root)` 和 leaf→root 祖先循环仍会先沿 junction 检查子目录。现已移除该循环，doctor 在原有无 IO `Normalize` 后复用 `ResolveExistingDirectory`；缺失、不可访问、非目录和 reparse 均保留 `FAILED` / `DATA_ROOT:FAIL` 的诊断结果，不进入 marker、HTTP 或 worker 探测。原有旧根词法拒绝仍在文件系统调用之前。

复用实际合成 junction 的三种路径直接验证 doctor 返回单项 `DATA_ROOT:FAIL`，HTTP/worker 使用一旦调用就抛错的哨兵，确认不会继续。另验证不存在根不创建、文件根不崩溃。Windows 权限边界使用**仅本次 fixture 的 marker 文件**显式拒绝当前用户 ReadData：doctor 返回有界 `CATALOG:FAIL`，无 HTTP/worker；finally 使用保存的 access SDDL 恢复其 ACL，marker/catalog 内容保持不变，fixture 随测试退役。没有变更真实数据或主机策略。

最终 LocalManager **13/13 PASS**，Release **0 warnings / 0 errors**，diff check PASS；没有运行服务/设备操作，也没有提交代码。

## 安装失败补偿复核边界

只读检查 `install-service.ps1`：新服务创建后的异常会尝试 `sc.exe delete`，但未检查 cleanup 的 LASTEXITCODE；ACL修改和新建 EventSource 也不在补偿中。因此不能将该脚本描述为“安装失败完整回滚”。本轮没有执行 SCM/ACL/事件源变更，也没有扩大到通用 ACL 补偿架构。建议后续通过可注入的定向失败 fixture 验证每阶段失败和 cleanup 错误的明确报告，既有数据目录必须始终保留；该项不伪报已修复。
