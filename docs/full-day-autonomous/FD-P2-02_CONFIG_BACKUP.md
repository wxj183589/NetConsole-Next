# FD-P2-02 配置备份、版本历史与差异

## 2026-11-07 Traceroute 与定时备份检查点：253b814

Branch：`codex-A/netconsole-next-r3-dual-ac`。功能提交 `2927b35dfb6c9d5ae7e82f83548133f13b1668e6`；进度读取并发修复及当前功能检查点 `253b8144226091337227f29dca93c9378ebe99af`。全产品 **PARTIAL / REAL_DEVICE_PENDING**；保留当前分支，仅本地提交，不 Push、Merge、Release。

FD-P2-09新增定时计划CRUD，SUPERADMIN管理自己的精确Profile计划；最多64计划/32目标/4在途计划，间隔60–43200分钟。到期复用既有Netmiko备份/任务，不重叠、不追赶循环；每轮重读当前账号、设备、端点和密码。计划JSON只存引用，原子有界1MiB；损坏文件隔离503且Service继续工作。完成批次元数据最多128条，未保存计划终态前保留pin，不清除Raw或配置版本。

定时15/15、原备份9/9、完整harness、实际HTTP计划15/原备份28、Chrome计划11/原备份9均PASS。RUNNING/SAVED/STARTUP_INFO保持原三类只读语义；startup正文和restore仍未实现。

本批只用 Next Workspace 隔离fixture/回环，设备配置写入0，旧NetConsole真实数据根未访问或复制；用户Vite90348/127.0.0.1:18792保留。现有离线候选仍为ef57213，不含本批功能；本批未重新执行离线十门禁、远端CI、终端/Linux/Jump浏览器或真实设备验收，历史证据保留所属提交范围。

详细功能、限制、命令和证据路径见[本批专项验收](../TRACEROUTE_AND_BACKUP_SCHEDULE_ACCEPTANCE_20261107.md)。

## 以下为既有检查点（历史范围）

下文原有“当前/最终”、测试数量与候选仅对各自提交有效；本批以本节和专项验收为准。


## 2026-11-07 FD-P2-06三类只读备份：ef57213

日期：2026-11-07。Branch：`codex-A/netconsole-next-r3-dual-ac`。功能提交 `e93b38280328beb96bbbb1b172b3d0a01179cfef`；备份下载 no-store 修复及当前产品 HEAD `ef5721354fc56004f018a2b851b1aee9fd8b0bdd`。整体 **PARTIAL / REAL_DEVICE_PENDING**。保留当前分支，不 Push、Merge、Release。

证据提交范围：完整后端 session23456、Worker40/40、前端14files/140tests及typecheck通过于 `e93b382`；`ef57213` no-store修复后重跑Service Release构建、配置HTTP28与完整离线十项，未重复完整harness/Worker/前端单测。

RUNNING/display current-configuration、SAVED/display saved-configuration、STARTUP_INFO/display startup按Profile和类别独立编号，仅同类diff；旧manifest无类别按RUNNING读，不改写文件。STARTUP_INFO是指向，不是startup正文或restore。

继续精确UUID/Profile、Site授权、任务/Raw/Netmiko/自动凭据；三类Raw前及下载脱敏含完整/截断PEM，ef57213统一no-store。历史/差异/下载/切类不采集；不发送save/system-view/restore。

配置backend9/9、HTTP28、Chrome9 PASS，37条ConfigBackupsView在完整Web14files/140tests中PASS；typecheck PASS；完整harness23456 EXIT0。ef57213离线CONFIGURATION_KINDS含独立版本、脱敏/no-store与saved缺失PASS。此前6/6/HTTP19/Chrome6仅早期证据。

专项：[AC 资源](../H3C_AC_RESOURCES_ACCEPTANCE_20261106.md)、[三类备份](../CONFIG_BACKUP_KINDS_ACCEPTANCE_20261106.md)、[全机诊断](../H3C_FULL_DIAGNOSTIC_ACCEPTANCE_20261106.md)。最新结构化报告位置：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-ef57213-20261107.json`。当前总入口为 [运行状态](AUTONOMOUS_RUN_STATE.json)、[测试矩阵](COMPLETE_TEST_MATRIX.md)、[最终交接](FINAL_HANDOVER.md)。


## 2026-11-06 及更早记录（历史）

以下旧检查点、任务状态、测试数量、候选路径及“当前/最终”均保留当时证据范围；最新结果和未完成项以上方 2026-11-07 章节为准。

## 2026-11-06 当前状态补充

配置备份增量已本地提交 `e12d062`，完整验收见 `../CONFIG_BACKUP_DEVICE_DIFF_ACCEPTANCE_20261106.md`。最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`，文档以所属提交为准。整体PARTIAL，不Push、Merge、Release。继续复用任务/Raw/claims、统一Netmiko连接和Profile密码自动读取；普通密码为受ACL保护的SQLite字段，无普通设备DPAPI中心。

设备详情按库存UUID打开明确Profile的备份；进入只加载历史，明确点击才采集，不按名称/IP/首条记录猜关联。当前仅支持独立H3C，FIT-AP经AC管理；隐藏目标先404，不支持厂商/FIT-AP返回409，整批预检拒绝时不创建部分任务。ADMIN只读历史/差异/下载，SUPERADMIN负责写入；设备切换、撤权与迟到响应不能回灌旧设备状态。

差异对既有256KiB上限内完整行序列比较，保留顺序与重复次数，不再限前16384行。先去共同前后缀，中段LCS预算1048576单元；超预算保守替换整个中段，不保证最短编辑。AddedLines/RemovedLines为完整计数，ChangedLines=min(AddedLines, RemovedLines)为配对替换数而非总数；每侧预览最多256行并提示截断。

备份批次Release Service/Tests 0 warnings/errors、默认完整Service harness PASS；备份使用独立入口`CONFIG_BACKUP_TESTS=6/6 PASS`。该批次Web11 files/65 tests/typecheck PASS；Chrome源码UI+API mock6场景PASS，1920×1080 DPR1。`CONFIG_BACKUP_HTTP_NETMIKO_E2E=PASS (19 checks)`：实际HTTP Service→CPython/Netmiko→合成回环SSH，覆盖双目标显式采集、相同/重排/重复备份、版本/脱敏下载/hash、ADMIN授权、跨Site隔离与不支持目标零任务/零命令。HTTP SUPERADMIN为全局；隐藏且不支持目标先404的非全局边界由直接Service scope测试验证。

当前整体Web结果12 files/82 tests/typecheck PASS；AUTO/legacy策略6组、共享worker16预算6组、批量8/8及guard后HTTP14/14 PASS。最终Release构建0 warnings / 0 errors；Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。当前 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106` 候选的`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；本节备份证据保持原业务范围。

旧a3954a5候选仅作历史记录；当前8320e2f候选已在隔离环境通过本机验收。这些均不是现场H3C或其他型号兼容证据。仅Next Workspace隔离fixture，未访问旧数据根，未写设备配置，真实设备保持REAL_DEVICE_PENDING。

## 以下为 2026-11-05 原批次历史记录

原批次的 DPAPI、凭据缺失与测试数量只作历史记录，当前连接模型以本节为准。

日期：2026-11-05

## 交付

- 配置备份复用现有 H3C BoundedCollectionScheduler、设备互斥、Raw seal、DPAPI credential reference、HostKey 和 Task Site scope。
- 支持批量选择 H3C 设备，最多 32 台，批内最多 4 路受控并行。
- 新增 API：
  - POST /api/system/config-backups
  - GET /api/system/config-backups/batches/{id}
  - GET /api/system/config-backups?profileId=...
  - GET /api/system/config-backups/{id}/download
  - GET /api/system/config-backups/{baseId}/diff/{targetId}
- 每个设备的备份保存为版本化文件和 JSON manifest，记录 Site、设备、采集时间、任务 run、大小和 SHA-256。
- 下载、历史和差异均重新执行当前 Site claims 校验；操作写入审计事件。
- 配置命令输出在进入 Raw 和备份文件前对 password、passphrase、pre-shared-key、community、private-key、secret 等敏感行脱敏，配置内容不发布到 H3C Facts。
- 新增“配置备份”页面，支持批量备份、历史查看、下载和版本差异。

## 验证

- CONFIG_BACKUP_TESTS=1/1 PASS
- H3C_TASK_FACT_PUBLICATION_TESTS=11/11 PASS
- NETWORK_DIAGNOSTICS_TESTS=4/4 PASS
- Service/Test Release build：PASS，0 warning / 0 error
- vue-tsc --noEmit：PASS
- Vitest：4 files / 7 tests PASS
- Vite build：PASS
- git diff --check：PASS
- 配置备份 manifest 路径穿越拒绝：PASS

## 边界

- 当前配置备份依赖设备已有安全凭据；四台模拟器凭据缺失时不宣称真实采集成功。
- 未执行真实模拟器登录、配置写入、切换、恢复或生产验收。
- 未修改正式 schema、真实数据根或旧 NetConsole 数据。
