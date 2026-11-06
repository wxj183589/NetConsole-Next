# 当前检查点 M：Netmiko / Web / Linux / Jump

2026-11-06 当前批次以 `docs/WEB_SSH_LINUX_JUMP_ACCEPTANCE_20261106.md` 和 `docs/NETMIKO_CONNECTION_AUDIT_20261106.md` 为准：完整 Service harness、前端 23 项、worker 20 项、离线工具 7 项通过；网络终端重连、Linux loopback SSH/Edge 浏览器、静默撤权、Jump 真实回环转发及 Settings Chrome mock 门禁通过。HCL AC1/AC2/SW 已使用当前 Netmiko 完成只读查询；FIT-AP 按 AC 管理，配置写入 0。真实设备、HA、实时 AP 归属、原生 125% 缩放、跨机 Web 和目标机安装仍待验收。下方 L 是历史检查点，其中旧 harness 竞态及 SW/FIT-AP 阻断已由当前证据替代。

CURRENT_BATCH=M_NETMIKO_WEB_LINUX_JUMP
FUNCTIONAL_COMMIT=a3954a5e95590782a576272d4e4b3c526eab248b
BRANCH=codex-A/netconsole-next-r3-dual-ac
TESTS=Service full harness PASS; Vue 23 PASS; Worker 20 PASS; offline tools 7 PASS; network/Linux/Jump E2E PASS
OFFLINE_CANDIDATE=build/service-web-linux-jump-a3954a5-20261106
OFFLINE_PRODUCT_VERSION=1.0.0+a3954a5e95590782a576272d4e4b3c526eab248b
OFFLINE_RUNTIME=PASS; PATH only System32; isolated data; no residual Service/worker processes
NEXT_LOCAL_TASK=AP Group source identity contract repair; then formal AP candidate/replacement and Domain/group lifecycle
FINAL_STATUS=PARTIAL; REAL_DEVICE_PENDING
PUSH=NO
MERGE=NO
RELEASE=NO

## 历史检查点 L（保留当时状态，以下失败与阻断不是当前结论）

CURRENT_BATCH=L_NETMIKO_CONNECTION_AND_SIMULATOR_GATES
TASKS=31
COMPLETED_TASKS=23
IN_PROGRESS_TASKS=1
FAILED_TASKS=1_CURRENT_HARNESS_RERUN
BLOCKED_TASKS=3
PARTIAL_BLOCKED_TASKS=3
NEW_DEFECTS=A21-A26及Netmiko/JumpHost连接边界已进入动态任务池
FIXED_DEFECTS=以任务池逐项EVIDENCE和验收文档为准
TESTS_RUN=Netmiko worker Python回归、Release build、既有TerminalCore/Telnet边界、完整Service suite复跑及既有浏览器/API矩阵
TESTS_PASS=Netmiko worker 2/2；Release build 0 warnings/0 errors；既有TerminalCore/Telnet、Vitest、Workspace、浏览器/API与Service harness证据保持有效
TESTS_FAIL=当前完整Service harness在ConcurrencyP1Tests.FailedTaskCanBeRetriedAsANewRun失败，待并行worker改动收敛后重跑
LATEST_CODE_COMMIT=5c60016
LATEST_COMMIT=本文件所属报告commit，以git log为准
NEXT_RECOMMENDED_TASK=为FIT-AP取得正确的本机安全凭据后重测；为SW处理旧HCL Exec兼容性；随后再执行双AC HA和实时AP归属Gate
FINAL_STATUS=PARTIAL_PENDING_EXTERNAL_GATES

2026-11-05 自动登录补充：隔离测试根已有 DPAPI 引用完成 AC1/AC2 SSH 自动认证并识别 7.1.064；SW SSH 认证成功但 Exec 设备信息识别失败；FIT-AP Telnet 登录凭据被拒。没有配置写入、切换或恢复。连接测试新增设备信息 15 秒边界和 Telnet 登录失败脱敏映射。

F-J额外交付只读Recent10/时效、单目标Task scope、当前交互身份/ZIP撤权、事实修订/归属及单命令Raw发布。旧Checkpoint E的“Recent10/task snapshot均被schema阻断”结论已被实现证据修正。自动登录授权持续有效；没有Merge/Push/Release，没有终止预览PID60784。未宣称全产品完成。

## Checkpoint L 连接与凭据边界

- 当前 branch：`codex-A/netconsole-next-r3-dual-ac`；当前 HEAD：`5c600162a46932dc7064d0e2e34d9ed0dac01396`。本 checkpoint 只更新文档，未 push、merge 或 release。
- Netmiko CPython worker 与显式 driver mapping 已完成离线回归；profile-scoped credential resolution、SSH HostKey 自动首次信任/轮换、Site Jump Host probe 与显式 fingerprint refresh 已完成隔离服务边界；历史任务按 ID 查询/恢复已完成。
- 四台白名单模拟器状态：AC1 `192.168.56.2:22` SSH **PASS**；AC2 `192.168.56.5:22` SSH **PASS**；SW `192.168.56.3:22` SSH 认证成功但 Exec/设备识别 **PARTIAL**；FIT-AP `192.168.56.4:23` Telnet 认证 **BLOCKED**。双 AC HA、切换、恢复和实时 AP 归属 **PENDING**。
- 凭据只从隔离测试根的 DPAPI Credential Reference 读取；密码未进入命令行、浏览器、URL、Raw、日志、Git 或报告。未修改模拟器配置，配置写入次数为 0。

