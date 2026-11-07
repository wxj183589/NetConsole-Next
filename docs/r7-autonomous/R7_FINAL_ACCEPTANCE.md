# R7.0 最终验收

日期：2026-11-07至2026-11-08。Agent：codex-A。线程：`01a1b2ee-40f8-7263-aba6-9f1c534c1d00`。

**整体 PARTIAL**。本轮完成七工作流的实际产品增量、真实模拟器及浏览器集成和本地提交。整机历史恢复、双AC切换、FIT原生认证及完整部署验收未闭环，不能宣称全产品替换或RELEASE_READY。

## 实际交付

| 工作流 | 本轮代码与可操作结果 | 结论 |
| --- | --- | --- |
| A 配置 | 任意两份完整备份按接口/VLAN/无线/AP组/管理/ACL/路由上下文比较、敏感变化提示、脱敏导出、实时捕获恢复预览、型号版本/完整性校验；原生检查点状态机、独立四阶段结果与失败阻断UI | PARTIAL，真实历史整机恢复BLOCKED |
| B 故障恢复 | 证据健康、统一事件、UNKNOWN/过期/部分事实边界；最多3次/120秒只读连接探测、既有版本/接口补采、Raw/Parse结果与审计 | 已交付范围PASS，后台巡检/业务恢复PARTIAL |
| C 双AC | 拓扑确认、设备观察角色与HA验证分开投影/显示；修正帮助命令清行后取得有效只读能力证据 | HA切换BLOCKED / NOT_TESTED |
| D FIT/H3C | 按型号事实与Raw来源/时效投影能力；设备详情真实API替换占位；FIT经AC管理与直接访问独立，禁止独立创建FIT类型 | 经AC与能力UI PASS，原生Telnet BLOCKED |
| E 原版迁移 | 增加ICMP/TCP Quick Ping采样、逐样本时延/丢失/取消/历史并接入健康；只读审计原版25项，更新矩阵和任务池 | 新增诊断PASS，完整迁移PARTIAL |
| F 工作台 | 首页真实入口/AP计数/失败任务口径，健康/恢复面板、设备能力详情、无线能力与配置比较；加载/空态/错误/旧请求失效保护 | 当前浏览器范围PASS_WITH_LIMITATIONS |
| G 部署 | LocalManager doctor核对受管根、只读SQLite实例、端口健康PID、Raw readiness和Worker握手；SCM过渡等待；精确PID的安全控制台启停验收工具 | 本地运行PASS，安装包/SCM门禁NOT_TESTED |

## 模拟器事实与写入边界

授权范围仅AC1 `192.168.56.2:22`、AC2 `192.168.56.5:22`、SW `192.168.56.3:22`、FIT `192.168.56.4:23`。使用既有服务端凭据，未打印凭据、猜密码或重复已知失败的FIT认证。以下为 **SIMULATOR**，不是物理设备或真实空口业务验收。

- 双AC只读探查：`reports/r70-device-20261107-233336/summary.json`。两台AC型号WX5540H-HCL、Comware7.1.064，`display wlan ac-role`均Common；AP R/M和R/B保留独立观察。没有控制器切换，不从本机slot备份推定跨AC HA。
- FIT：保留经AC的1个AP Resource/2来源观察、身份和组/历史。旧凭据无法建立直接CLI，本轮不再重复提交；直接访问UNKNOWN，原生采集/写入NOT_TESTED。
- SW原生探查：`reports/r70-sw-checkpoint-20261107-234349`。S6850、Comware7.1.070 Alpha7170，执行 `configuration commit` 后无新增commit point。未知确认未回答，未执行replace。
- 新HTTP配置链：`reports/r70-configuration-http-20261108-000030.json`。完整捕获、自比较、实时恢复预览均通过；操作 `4674ba41cc7d4474b6dc5038a9c55f8f` 返回 `NATIVE_CHECKPOINT_NOT_CREATED`，BeforeBackupId可追溯，写入/回读/业务/回滚四flags全部false。未执行描述变更或整机替换。前后完整配置canonical SHA256均 `0c1f167f14300ea6ad2262e23596404b0fa905fc1f88ecc853be2e2f28171161`。
- 真实恢复任务 `recovery-c5f8d551a33b47b3903db20baaeb7121` 首次CONNECTED，版本/接口两命令SUCCEEDED、Raw COMPLETE、Parse PARSED。没有人为宕机；这验证连接与补采，不代表业务故障恢复。
- TCP Quick Ping三次真实探测成功、平均0.67ms/0%丢失。TCP结果不冒称ICMP。

Writer成功、完整回读、服务探针和回滚是四个独立结论。Fixture验证状态机全链，不升级为真实设备恢复成功。

## 最终测试

| Gate | 结果与覆盖 |
| --- | --- |
| Web全量 | 23文件、257测试PASS；配置/设备详情异步隔离、健康恢复、Quick Ping、无线/任务/权限及既有回归 |
| Typecheck / Vite | PASS；2104 modules生产构建，保留大chunk提示，不声称已做性能优化 |
| Worker | 46/46 PASS；既有连接/协议/命令策略与新增checkpoint交互拒绝/无未知确认 |
| Service完整harness | EXIT0，日志 `reports/r70-service-harness-final.log`；包含迁移路径/reparse边界、Schema13崩溃恢复/Schema26、Raw故障注入/恢复/完整性、任务/并发/备份/恢复门禁、无线Observation/历史、Site claims/任务ZIP等 |
| 新Service专项 | 完整比较10/10、检查点14/14、健康7/7、Quick Ping7/7、H3C capability PASS；fixture与真实证据分别记录 |
| HTTP E2E | 30/30 PASS，`reports/r69-http-20261108-000304-5ba7d1.json`；域与Site可见性、角色拒绝、禁用/撤权、双命令事实投影、重试、ZIP manifest/hash |
| Browser | 最终7/7步骤通过、14项双分辨率宽度检查通过；API/page/console错误0。设备专项另确认创建选项实际6项、不含FIT_AP |
| LocalManager | 10/10 PASS，最终doctor EXIT0；现有catalog只读、实例匹配、健康/Raw/worker close握手 |
| Release | Service publish成功；Release build 0warnings/0errors；PowerShell脚本语法及git diff检查PASS |

HTTP先前失败已保留：一次因当前真实实例持有单实例mutex，改为精确安全停止后隔离运行；另一次为任务终态与独立facts发布之间的测试竞态，增加15秒有界投影等待并保留全部原数量/Raw/ZIP/hash断言，超时仍FAIL。没有修改真实事实或削弱门禁。

浏览器证据：`reports/r70-browser-final-20261108`（完整业务）、`reports/r70-browser-device-final-20261108`（选项非空与设备详情复验），各含1920×1080、2560×1440截图。根Agent实际复核设备详情、健康和无线截图；初轮旧占位和脚本locator错误证据保留。未执行Windows125%缩放验收。

## Git与交接

Branch：`codex-A/netconsole-next-r3-dual-ac`，起点 `2551b2683cee04aa48edff765e4ac94750acf8a5`。

- `cf99baf` 完整配置上下文比较与原生检查点门禁。
- `22dfe8d` 健康/只读恢复/Quick Ping/能力及工作台集成和静态资源。
- `f6ca318` 本机诊断与安全启停。
- 本目录共九份验收/专项/差距报告、25项迁移矩阵和任务池独立文档提交；可用 `git log -4 --oneline` 核验最终SHA。

全部中文本地提交带Agent/线程标识；**Push / Remote Merge / Release Publish均未执行**。本地publish目录是开发运行输出，不是正式发布。DevStatus只新增本轮拥有的任务交接文件，保留原current-status/handoff/tasks/doing的dirty内容。

## 保留的最终运行环境

| 项目 | 当前值 |
| --- | --- |
| Web | `http://127.0.0.1:5174/`，Vite PID37420 |
| Service | `http://127.0.0.1:18771/`，PID83920，HEALTHY |
| DLL | `D:\study\NetConsole-Next-Workspace\build\r70-service\NetConsole.Next.Service.dll` |
| Data root | `D:\study\NetConsole-Next-Workspace\test-data\simulator-live-20261105`，原Next独立数据根，SQLite schema26 |
| Worker | 既有Python/Netmiko按需启动；最终doctor导入/close握手PASS，真实SSH连接与补采PASS |
| 日志 | Workspace `reports/r70-service.log`、`reports/r70-service.err.log` |
| 健康 | `/api/system/health`；`reports/r70-start-health.json`、`reports/r70-doctor-final.json` |
| Syslog | 端口0，禁用；不能声称当前UDP接收已启用 |

安全停止（先复核PID与DLL；工具会检查专属控制台且只发送Ctrl-C，拒绝共享控制台，不force kill）：

```powershell
cd D:\study\NetConsole-Next-Workspace\NetConsole-Next
python tests/r70_runtime_control.py stop --pid 83920
python tests/r70_runtime_control.py start
dotnet run --project src/NetConsole.Next.LocalManager -- doctor D:\study\NetConsole-Next-Workspace\test-data\simulator-live-20261105 18771
```

该工具仅用于本轮已知开发DLL/隔离数据根；安装后的Windows服务应使用现有LocalManager/SCM启停机制。原68272及中间实例105908/44424均验证目标身份后正常退出；未终止无关进程，Vite保持运行。

## 数据影响与限制

仅Next独立数据根新增完整配置备份、审计、任务/Raw和诊断历史；证据和构建在Next Workspace。完整原文保留私有目录，不写Git/浏览器报告；对外比较和下载脱敏。仅退休本轮生成且未引用的未跟踪中间静态资源，未删除已跟踪旧资源/历史测试数据/工作树。

未读取或改写 `D:\NetConsoleData`、`D:\NetConsoleData-dev`，未完整复制数据根/Site。原版只读源码审计；旧NetConsole源码无修改。SW执行原生commit探查但未产生checkpoint，完整运行配置前后相同；无AC/FIT配置修改、无HA切换、无整机替换。完整未完成项和下一门禁见 `R7_REMAINING_GAPS.md`。
