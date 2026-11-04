# 自主开发任务池

日期：2026-11-05；起点：27ebfb8；冻结基线：NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1。

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
STATUS=QUEUED

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
STATUS=QUEUED

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
STATUS=QUEUED

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
STATUS=QUEUED

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
STATUS=QUEUED

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
STATUS=QUEUED

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
STATUS=QUEUED

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
STATUS=QUEUED

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
STATUS=QUEUED

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
STATUS=QUEUED

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
STATUS=QUEUED

## A12 双 AC HA、身份、切换与恢复实测

TASK_ID=A12
MODULE=Simulator
FEATURE=双 AC HA、身份、切换与恢复实测
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=安全凭据
EVIDENCE=R3 上轮仅 TCP 可达
IMPLEMENTATION_SCOPE=仅四台白名单
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=QUEUED

## A13 HTTP/API 与 Service harness 回归

TASK_ID=A13
MODULE=Acceptance
FEATURE=HTTP/API 与 Service harness 回归
CURRENT_STATUS=审计发现
USER_VALUE=保证运维结果可信、授权隔离和可用性
SEVERITY=P1
DEPENDENCIES=现有 Service 维护窗口
EVIDENCE=全局 Mutex 已占用
IMPLEMENTATION_SCOPE=保留预览，不绕过 Mutex
SECURITY_RISK=按当前 Site claims 和来源隔离；不增加外部目标
DATABASE_IMPACT=本轮不修改真实数据或正式 schema
TESTABILITY=离线 fixture / 定向现有测试；真实设备单列
ESTIMATED_EFFORT=按证据逐批实施
PRIORITY=P1
STATUS=QUEUED

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
STATUS=QUEUED

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
STATUS=QUEUED

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
STATUS=QUEUED

