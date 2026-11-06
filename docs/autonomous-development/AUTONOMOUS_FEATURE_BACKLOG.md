# 自主开发任务池

日期：2026-11-05；当前 HEAD：5c60016；分支：codex-A/netconsole-next-r3-dual-ac；冻结基线：NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1。

最新附件已授权产品编码/无线UI/四台模拟器测试；旧文档的 authorization pending 为历史，不再作为本轮审批阻断。Schema 未授权应用到真实数据。

## A01 规范化完整 MAC，拒绝丢弃非法字符

TASK_ID=A01
MODULE=Wireless
FEATURE=规范化完整 MAC，拒绝丢弃非法字符
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=NormalizeMac 非十六进制过滤
IMPLEMENTATION_SCOPE=WirelessReadModel.cs
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=COMPLETED

## A28 Netmiko worker 与显式 driver mapping

TASK_ID=A28
MODULE=Connection/Netmiko
FEATURE=CPython Netmiko worker、显式已安装 driver mapping、分页准备与有界输出
CURRENT_STATUS=Checkpoint L 已完成离线实现和 worker contract 回归
USER_VALUE=复用统一连接边界，避免设备厂商/协议静默错配，减少人工登录操作
SEVERITY=P1
DEPENDENCIES=离线 CPython、netmiko==4.7.0
EVIDENCE=tools/netmiko_worker.py；tests/netmiko_worker_test.py；`python -m unittest tests/netmiko_worker_test.py` 2/2 PASS
IMPLEMENTATION_SCOPE=worker process only; .NET retains task/site/credential/HostKey/Raw authority
SECURITY_RISK=密码仅通过本机 stdin/管道；未知 driver 拒绝，不静默回退
DATABASE_IMPACT=无 schema 或真实数据修改
TESTABILITY=Python unit tests；真实 H3C/ZTE/Cisco/Huawei 互操作另行验收
ESTIMATED_EFFORT=已完成
PRIORITY=P1
STATUS=COMPLETED

## A29 Profile-scoped credential 与 SSH HostKey 自动信任/轮换

TASK_ID=A29
MODULE=Connection/Security
FEATURE=profile ID 凭据解析、HostKey 首次自动信任和轮换审计
CURRENT_STATUS=Checkpoint L 已完成本地/隔离服务边界，并取得 AC1/AC2 自动登录证据
USER_VALUE=设备添加后直接连接，免重复输入和确认弹窗
SEVERITY=P1
DEPENDENCIES=DPAPI Credential Reference、HostKey history
EVIDENCE=profile-scoped provider、HostKey Catalog；AC1/AC2 SSH 自动认证和 AUTO_UPDATE PASS
IMPLEMENTATION_SCOPE=profile credential/HostKey boundary; no password in browser/URL/Raw/log/Git
SECURITY_RISK=不得猜测密码；HostKey rotation requires service audit and atomic commit
DATABASE_IMPACT=不修改真实数据；隔离测试根仅读现有 DPAPI 引用
TESTABILITY=Credential/HostKey tests；白名单模拟器连接测试
ESTIMATED_EFFORT=已完成
PRIORITY=P1
STATUS=COMPLETED

## A30 Site Jump Host 探测与显式指纹刷新

TASK_ID=A30
MODULE=Connection/Security
FEATURE=Site 级 Jump Host 持久化、Netmiko probe、指纹变化 fail-closed 与显式 refresh
CURRENT_STATUS=Checkpoint L 已完成隔离服务/API闭环
USER_VALUE=支持可信内网跳板连接，同时保留双 HostKey 审计边界
SEVERITY=P1
DEPENDENCIES=Netmiko worker、Site claims、Jump Host credential boundary
EVIDENCE=POST `/api/system/sites/{siteId}/ssh-jump-host/test`；POST `/refresh-fingerprint`；Telnet 不套用 Jump Host
IMPLEMENTATION_SCOPE=Jump Host only; target device identity remains separate
SECURITY_RISK=密码不入 API view/日志；已保存指纹变化 fail-closed，只有显式 refresh 才更新
DATABASE_IMPACT=增量 schema 已提交；真实数据未修改
TESTABILITY=CredentialTests/worker probe；live bastion evidence pending
ESTIMATED_EFFORT=已完成
PRIORITY=P1
STATUS=COMPLETED

## A31 历史任务按 ID 查询与恢复

TASK_ID=A31
MODULE=Task Center
FEATURE=历史任务直接按 ID 查询，支持详情/重试/恢复不受最近 100 条窗口限制
CURRENT_STATUS=Checkpoint L 已完成实现和定向回归
USER_VALUE=长期任务记录可追溯，失败设备可单独恢复
SEVERITY=P1
DEPENDENCIES=task_runs persistence
EVIDENCE=Catalog direct-ID query；`CONCURRENCY_P1_TESTS=5/5 PASS`（既有隔离证据）
IMPLEMENTATION_SCOPE=task detail/retry/resume lookup
SECURITY_RISK=继续复核当前 Site claims；不扩大原始目标集合
DATABASE_IMPACT=增量查询修复，无真实数据重写
TESTABILITY=超过 100 条历史任务回归；完整 harness 需在并行变更收敛后复跑
ESTIMATED_EFFORT=已完成
PRIORITY=P1
STATUS=COMPLETED

## A02 校验 Observation Site 和来源 AC 一致

TASK_ID=A02
MODULE=Wireless/Security
FEATURE=校验 Observation Site 和来源 AC 一致
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=Project 仅校验非空 Site
IMPLEMENTATION_SCOPE=WirelessReadModel.cs
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=COMPLETED

## A03 连接失败不等于离线

TASK_ID=A03
MODULE=Wireless
FEATURE=连接失败不等于离线
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=RuntimeFor 把 FAILED/CLOSED 投影 OFFLINE
IMPLEMENTATION_SCOPE=WirelessReadModel.cs
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=COMPLETED

## A04 重复 Observation 幂等，缺名称保留身份

TASK_ID=A04
MODULE=Wireless
FEATURE=重复 Observation 幂等，缺名称保留身份
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=BuildGroups ToDictionary 重复键异常；Project 按名称丢行
IMPLEMENTATION_SCOPE=WirelessReadModel.cs
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=COMPLETED

## A05 读取超过 100 台 AC

TASK_ID=A05
MODULE=Collection
FEATURE=读取超过 100 台 AC
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=QueryWirelessCatalog 只取第一页
IMPLEMENTATION_SCOPE=WirelessReadModel.cs
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=COMPLETED

## A06 部分授权标记和离线证据标记

TASK_ID=A06
MODULE=Wireless/Security
FEATURE=部分授权标记和离线证据标记
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=GetSnapshot AUTHORIZED 未表达 partial；fixture 无标记
IMPLEMENTATION_SCOPE=WirelessReadModel.cs
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=COMPLETED

## A07 筛选后详情/AP组不残留，异步请求竞态

TASK_ID=A07
MODULE=Web UI
FEATURE=筛选后详情/AP组不残留，异步请求竞态
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=WirelessView selectedResource 搜索全量，loadGroups 静默吞错
IMPLEMENTATION_SCOPE=WirelessView.vue
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=COMPLETED

## A08 无线分页搜索和状态筛选

TASK_ID=A08
MODULE=Web UI
FEATURE=无线分页搜索和状态筛选
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P2
DEPENDENCIES=NONE
EVIDENCE=WirelessView 仅请求首 100 行
IMPLEMENTATION_SCOPE=WirelessView.vue
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P2
STATUS=COMPLETED

## A09 拓扑证据冲突 fail closed

TASK_ID=A09
MODULE=Wireless
FEATURE=拓扑证据冲突 fail closed
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=BuildDomains 重复域覆盖，重叠成员字典排序取首
IMPLEMENTATION_SCOPE=WirelessReadModel.cs
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=COMPLETED

## A10 状态时效/冲突/Recent10

TASK_ID=A10
MODULE=Wireless
FEATURE=状态时效/冲突/Recent10
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=事实历史与阈值证据
EVIDENCE=R3 G05/G10；只读 Current 投影无生命周期
IMPLEMENTATION_SCOPE=现有 H3c facts 复用评估
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=PARTIAL_BLOCKED

## A11 Scope Snapshot 与当前 claims 交集

TASK_ID=A11
MODULE=Tasks
FEATURE=Scope Snapshot 与当前 claims 交集
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=现有 task 持久化合同评估
EVIDENCE=R3 D06 / G09
IMPLEMENTATION_SCOPE=审计 Scheduler 和 API，复用已有授权
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=PARTIAL_BLOCKED

## A12 双 AC HA、身份、切换与恢复实测

TASK_ID=A12
MODULE=Simulator
FEATURE=双 AC HA、身份、切换与恢复实测
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=安全凭据
EVIDENCE=AC1/AC2 已取得 DPAPI 自动 SSH 登录与版本识别；SW Exec 兼容性和 FIT-AP 凭据仍阻断 HA/切换实测
IMPLEMENTATION_SCOPE=仅四台白名单
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=PARTIAL_BLOCKED

## A13 HTTP/API 与 Service harness 回归

TASK_ID=A13
MODULE=Acceptance
FEATURE=HTTP/API 与 Service harness 回归
CURRENT_STATUS=Checkpoint K 已重新执行隔离 R3 HTTP API、Service harness 与真实 Service 浏览器 smoke
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=NONE（隔离数据根和临时 loopback 端口）
EVIDENCE=R3 API fixture 11 项 PASS；完整 Service harness PASS；Service-backed Browser smoke 1920x1080 PASS
IMPLEMENTATION_SCOPE=只验证隔离服务和现有 API/UI，不接触设备凭据或真实数据
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=Release build、Service harness、R3 API E2E、Service-backed Browser smoke
ESTIMATED_EFFORT=已完成
PRIORITY=P1
STATUS=COMPLETED

## A14 浏览器离线夹具 1920/2560

TASK_ID=A14
MODULE=Acceptance
FEATURE=浏览器离线夹具 1920/2560
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=R3 既有页面未浏览器验收
IMPLEMENTATION_SCOPE=独立 Vite 与拦截 API，标记 Fixture
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=COMPLETED

## A15 Windows 原生125%

TASK_ID=A15
MODULE=Acceptance
FEATURE=Windows 原生125%
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P2
DEPENDENCIES=有效原生125%环境
EVIDENCE=R2 Gate 仍 pending
IMPLEMENTATION_SCOPE=只读检查，不更改缩放
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P2
STATUS=BLOCKED

## A16 LLDP 与关联及终端回归

TASK_ID=A16
MODULE=Diagnostics
FEATURE=LLDP 与关联及终端回归
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P2
DEPENDENCIES=NONE
EVIDENCE=现有 H3C parser/terminal tests
IMPLEMENTATION_SCOPE=定向现有测试，避免架构重写
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P2
STATUS=COMPLETED


## A17 AP地址事实贯通与失败后有效证据保留

TASK_ID=A17
MODULE=Wireless/Collection
FEATURE=AP地址事实贯通与失败后有效证据保留
CURRENT_STATUS=审计发现
USER_VALUE=避免错误身份、状态和来源声明
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=H3cFacts 未保存 address 命令；失败 Current 没有回看有效事实
IMPLEMENTATION_SCOPE=复用现有事实；无证据不得伪造
SECURITY_RISK=不扩展Site或设备范围
DATABASE_IMPACT=真实数据零修改；持久模型单独评审
TESTABILITY=定向单元及fixture；实测单列
ESTIMATED_EFFORT=按证据和schema依赖分批
PRIORITY=P1
STATUS=COMPLETED

## A18 缺失证据与矛盾证据分离，状态冲突保留

TASK_ID=A18
MODULE=Wireless/Collection
FEATURE=缺失证据与矛盾证据分离，状态冲突保留
CURRENT_STATUS=审计发现
USER_VALUE=避免错误身份、状态和来源声明
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=bySerial/byMac 把空证据算矛盾；同revision矛盾输入被去重
IMPLEMENTATION_SCOPE=复用现有事实；无证据不得伪造
SECURITY_RISK=不扩展Site或设备范围
DATABASE_IMPACT=真实数据零修改；持久模型单独评审
TESTABILITY=定向单元及fixture；实测单列
ESTIMATED_EFFORT=按证据和schema依赖分批
PRIORITY=P1
STATUS=COMPLETED

## A19 Domain-owned AP Group 与稳定 source group key

TASK_ID=A19
MODULE=Wireless/Collection
FEATURE=Domain-owned AP Group 与稳定 source group key
CURRENT_STATUS=LOCAL_ACCEPTANCE_PASS；实时 source-key/共享组证据仍待验收
USER_VALUE=避免错误身份、状态和来源声明
SEVERITY=P1
DEPENDENCIES=设备认证及可验证 source group evidence
EVIDENCE=AP_GROUP_SOURCE_IDENTITY_ACCEPTANCE_20261106；Group合同10场景、schema24、HTTP16、浏览器15状态PASS；parser仅有组名时保持CANDIDATE
IMPLEMENTATION_SCOPE=独立来源证据、AC/Domain身份、来源范围详情与成员版本；无证据不得伪造
SECURITY_RISK=不扩展Site或设备范围
DATABASE_IMPACT=schema24仅迁移可重建AP Group缓存；真实数据零修改
TESTABILITY=完整Service harness、定向合同、真实HTTP加离线fixture、浏览器mock；实测单列
ESTIMATED_EFFORT=本机实现已完成；正式生命周期与实测另项推进
PRIORITY=P1
STATUS=COMPLETED_LOCAL_REAL_EVIDENCE_PENDING

## A20 持久身份/历史与可验证 Raw manifest/hash

TASK_ID=A20
MODULE=Wireless/Collection
FEATURE=持久身份/历史与可验证 Raw manifest/hash
CURRENT_STATUS=审计发现
USER_VALUE=避免错误身份、状态和来源声明
SEVERITY=P1
DEPENDENCIES=独立持久化方案评审，新增schema专项授权
EVIDENCE=当前投影引用 fact:id，无正式 manifest/hash外键和生命周期
IMPLEMENTATION_SCOPE=复用现有事实；无证据不得伪造
SECURITY_RISK=不扩展Site或设备范围
DATABASE_IMPACT=真实数据零修改；持久模型单独评审
TESTABILITY=定向单元及fixture；实测单列
ESTIMATED_EFFORT=按证据和schema依赖分批
PRIORITY=P1
STATUS=BLOCKED

## A21 交互会话当前身份复核

TASK_ID=A21
MODULE=Terminal/Security
FEATURE=交互会话当前身份复核
CURRENT_STATUS=Checkpoints H-J实际审计与定向验证
USER_VALUE=原始证据、任务边界和Site隔离可信
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=握手缓存SUPERADMIN角色；会话退出/降权后可继续stream
IMPLEMENTATION_SCOPE=InteractiveScopeAuthorizer/Stream
SECURITY_RISK=不扩展目标；不以显示名拼接独立采集
DATABASE_IMPACT=既有JSON/表；真实数据与schema零修改
TESTABILITY=最小SQLite、调度器、Raw sink及浏览器fixture
ESTIMATED_EFFORT=已按证据分批实施；未完成部分独立评审
PRIORITY=P1
STATUS=COMPLETED

## A22 机器级文件及ZIP当前全局权限

TASK_ID=A22
MODULE=Files/Security
FEATURE=机器级文件及ZIP当前全局权限
CURRENT_STATUS=Checkpoints H-J实际审计与定向验证
USER_VALUE=原始证据、任务边界和Site隔离可信
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=Raw/录屏/备份无Site归属；原通用文件路由可绕过业务授权
IMPLEMENTATION_SCOPE=FilesView/文件路由/Archive
SECURITY_RISK=不扩展目标；不以显示名拼接独立采集
DATABASE_IMPACT=既有JSON/表；真实数据与schema零修改
TESTABILITY=最小SQLite、调度器、Raw sink及浏览器fixture
ESTIMATED_EFFORT=已按证据分批实施；未完成部分独立评审
PRIORITY=P1
STATUS=COMPLETED

## A23 真实采集Raw到facts发布

TASK_ID=A23
MODULE=Collection/Raw
FEATURE=真实采集Raw到facts发布
CURRENT_STATUS=Checkpoints H-J实际审计与定向验证
USER_VALUE=原始证据、任务边界和Site隔离可信
SEVERITY=P1
DEPENDENCIES=单命令发布已完成；协调多命令快照需冻结的持久Plan/Raw grouping合同及后续schema专项
EVIDENCE=原h3c_facts只有offline importer写入；真实任务结果未进入业务事实
IMPLEMENTATION_SCOPE=H3cTaskFactPublication/调度器/显式coherent collection方案
SECURITY_RISK=不扩展目标；不以显示名拼接独立采集
DATABASE_IMPACT=既有JSON/表；真实数据与schema零修改
TESTABILITY=最小SQLite、调度器、Raw sink及浏览器fixture
ESTIMATED_EFFORT=已按证据分批实施；未完成部分独立评审
PRIORITY=P1
STATUS=PARTIAL_BLOCKED

## A24 Current时间单调与不可变revision

TASK_ID=A24
MODULE=Facts/Persistence
FEATURE=Current时间单调与不可变revision
CURRENT_STATUS=Checkpoints H-J实际审计与定向验证
USER_VALUE=原始证据、任务边界和Site隔离可信
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=迟到回放回退Current；同revision silently upsert
IMPLEMENTATION_SCOPE=H3cFacts/409 conflict
SECURITY_RISK=不扩展目标；不以显示名拼接独立采集
DATABASE_IMPACT=既有JSON/表；真实数据与schema零修改
TESTABILITY=最小SQLite、调度器、Raw sink及浏览器fixture
ESTIMATED_EFFORT=已按证据分批实施；未完成部分独立评审
PRIORITY=P1
STATUS=COMPLETED

## A25 冻结采集时设备、Site和端点归属

TASK_ID=A25
MODULE=Facts/Site
FEATURE=冻结采集时设备、Site和端点归属
CURRENT_STATUS=Checkpoints H-J实际审计与定向验证
USER_VALUE=原始证据、任务边界和Site隔离可信
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=旧事实按当前inventory Site重新归属
IMPLEMENTATION_SCOPE=capturedScope/SQL predicates/runtime history
SECURITY_RISK=不扩展目标；不以显示名拼接独立采集
DATABASE_IMPACT=既有JSON/表；真实数据与schema零修改
TESTABILITY=最小SQLite、调度器、Raw sink及浏览器fixture
ESTIMATED_EFFORT=已按证据分批实施；未完成部分独立评审
PRIORITY=P1
STATUS=COMPLETED

## A26 冻结端点和已引用命令防止Raw重标

TASK_ID=A26
MODULE=Tasks/Raw
FEATURE=冻结端点和已引用命令防止Raw重标
CURRENT_STATUS=Checkpoints H-J实际审计与定向验证
USER_VALUE=原始证据、任务边界和Site隔离可信
SEVERITY=P1
DEPENDENCIES=NONE
EVIDENCE=Raw sink重新读当前host；运行中命令编辑破坏manifest验证
IMPLEMENTATION_SCOPE=TaskScopeEndpoint/Raw sink/definition command freeze
SECURITY_RISK=不扩展目标；不以显示名拼接独立采集
DATABASE_IMPACT=既有JSON/表；真实数据与schema零修改
TESTABILITY=最小SQLite、调度器、Raw sink及浏览器fixture
ESTIMATED_EFFORT=已按证据分批实施；未完成部分独立评审
PRIORITY=P1
STATUS=COMPLETED

## Checkpoint J状态说明

A10已实现只读Current/PreviousEffective/Recent10、显式deadline及partial-history；A11已实现单目标冻结scope与执行/读取撤权收敛。两项保留PARTIAL_BLOCKED仅指完整无线持久主体生命周期/多目标Plan与Report，不再把这些已实现能力归为schema阻断。A20已有单命令Raw引用核验，正式持久身份/生命周期及coherent多命令关系仍待方案评审。A23不会把独立AP表/地址任务伪装同一次采集；当前候选/STALE标记保留。任务池共27项；Checkpoint K 新增 A27，并将 A13 从部分阻断更新为完成，状态以下方实际统计为准。
## A27 跨 AC collection manifest 查询与采集记录 UI

TASK_ID=A27
MODULE=Wireless/Facts/UI
FEATURE=按 AC Domain 和当前 Site claims 查询 collection manifest 列表/详情，呈现来源 AC 与 Raw provenance
CURRENT_STATUS=Checkpoint K 已完成实现并通过隔离 API/Catalog/浏览器验收
USER_VALUE=让运维查看跨 AC 采集记录和逐条来源，不把离线记录误认为实时设备状态
SEVERITY=P1
DEPENDENCIES=既有 schema v19 manifest、P2B5 Site claims
EVIDENCE=新只读 routes、Catalog 过滤/计数测试、R3 fixture API、wireless browser fixture
IMPLEMENTATION_SCOPE=Program.cs、WirelessCollectionManifest.cs、WirelessView.vue 和现有测试
SECURITY_RISK=每次读取重检当前 Site claims；source AC 过滤后从可见 Observation 重新聚合
DATABASE_IMPACT=不新增 schema/migration；只读既有 manifest 表，不访问真实数据
TESTABILITY=Release harness、R3 API E2E、browser fixture、Service smoke
ESTIMATED_EFFORT=已完成
PRIORITY=P1
STATUS=COMPLETED
