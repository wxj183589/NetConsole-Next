# 测试矩阵

测试计数按粒度分开：后端61个具名方法/分支（Wireless14、Runtime13、TaskScope11、Interactive6、FactScope8、RawPublication9），Vitest5、Workspace Python4；浏览器15状态另计，不能冒充统一E2E数量。Service harness独立suite不虚报断言总数。HTTP因全局Mutex阻断，不计产品FAIL。

| Gate | 本轮结果 | 证据 |
| --- | --- | --- |
| Service + Tests Release build | PASS，0 warnings/errors | dotnet build |
| LocalManager Release build | PASS | dotnet build |
| Service harness | PASS | Workspace/build/autonomous-20261105/final-service-harness-checkpoint-j.log |
| Wireless定向 | 14/14 PASS | WirelessTests.Run；最小SQLite、101台AC、跨Site |
| Runtime history | 13/13 PASS | WirelessRuntimeTests及SQLite重建 |
| Task scope | 11/11 PASS | 实际SQLite/有界调度 |
| Interactive/files | 6/6 PASS | session/角色/归档边界 |
| Fact revision/scope | 8/8 PASS | 单调Current、回滚、迁站/重绑、legacy |
| Raw fact publication | 9/9 PASS | 真实Raw sink、SHA、投影故障隔离、端点/命令冻结 |
| 现有设备/claims/凭据/调度/解析/Raw回归 | PASS（核心suite） | Service harness；不虚报HTTP 31/31 |
| Vitest | 5/5 PASS | npm test |
| Vue typecheck | PASS | npm run typecheck |
| Vite/terminal build | PASS，现有chunk-size warning | web-build.log |
| Browser离线fixture | PASS，15状态 | gui/evidence.json；错误态预期503/403，不是非预期页面异常 |
| HTTP API隔离E2E | BLOCKED B02 | 实际启动被Mutex拒绝 |
| Service-backed Browser E2E | BLOCKED B02 | 未拿旧进程替代新代码 |
| Windows原生125% | BLOCKED B04 | DPI96/DPR1 |
| git diff --check | PASS | 本地检查 |

原有57场景下列PASS仅表示所描述的不变量在离线服务投影/最小catalog中验证，不表示端到端Gate均通过。保守保留尚未覆盖的场景，未删除或弱化原断言。17种设计fixture未全部落地；已有双AC JSON、52样本runtime JSON及代码内最小SQLite/Raw fixture。

| ID | 原场景 | 状态 | 证据/缺口 |
| --- | --- | --- | --- |
| SC-01 | 单 AC standalone domain | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-02 | stack 多 member | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-03 | cluster evidence 不完整 | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-04 | one-plus-one HA | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-05 | member/site revision 冲突 | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-06 | serial+完整 MAC 唯一 | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-07 | serial 缺失、MAC 唯一 | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-08 | MAC 缺失、serial 唯一 | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-09 | serial 与 MAC 互相冲突 | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-10 | 多接口 MAC | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-11 | AP 更换后 serial/MAC 变化 | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-12 | 跨 AC duplicate identity | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-13 | AP 仅被 AC-A 观察 | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-14 | AP 被 AC-A、AC-B 同时观察 | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-15 | association ended | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-16 | raw `R/M` | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-17 | raw `R/B` | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-18 | raw `I` 且 profile 明确 inactive | PARTIAL_OFFLINE_PASS | Runtime：有显式版本上下文时I归一化，真实device profile仍待实测；HTTP Gate pending |
| SC-19 | raw `I` 且 profile 未确认 | PARTIAL_OFFLINE_PASS | Runtime：无版本上下文保留UNKNOWN；无设备实测；HTTP Gate pending |
| SC-20 | collection timeout | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-21 | stale observation | PARTIAL_OFFLINE_PASS | Runtime：deadline边界、clock skew与STALE；HTTP Gate pending |
| SC-22 | 多来源互斥 state | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-23 | 无变化刷新 | PARTIAL_OFFLINE_PASS | Runtime：不变样本更新Current时间，不增加Recent10；HTTP Gate pending |
| SC-24 | claims 仅允许 AC-A | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-25 | claims 允许 AC-A、AC-B | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-26 | AP 跨 Site partial visibility | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-27 | no Site authorization | PARTIAL_OFFLINE_PASS | Wireless/FactScope：无权返回空/不可见；HTTP Gate pending |
| SC-28 | 同名 group 跨 AC | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-29 | group membership 变化/空组 | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-30 | SUPERADMIN wireless read | PARTIAL_OFFLINE_PASS | Wireless：全局读取与来源谓词；HTTP Gate pending |
| SC-31 | task create scope snapshot | PARTIAL_OFFLINE_PASS | TaskScope：单目标actor/Site/endpoint快照；多目标Plan未实现；HTTP Gate pending |
| SC-32 | task read after claims revoke | PARTIAL_OFFLINE_PASS | TaskScope：通用任务读与创建proof；Report未实现；HTTP Gate pending |
| SC-33 | task partial current claims | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-34 | task failed/timeout/cancel | PARTIAL_OFFLINE_PASS | TaskScope/RawPublication：失败/超时/取消及PARTIAL封存；HTTP Gate pending |
| SC-35 | audit manifest/hash/no-secret | PARTIAL_OFFLINE_PASS | RawPublication：单命令hash引用与无秘密审计；长期业务Audit未完成；HTTP Gate pending |
| SC-36 | 同一 Domain 下不同 AC 观察到同名组 | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-37 | source evidence 明确 Domain-shared group | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-38 | group 缺 source AC ID 或 source key | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-39 | 同 scope stable source key、display name 变化 | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-40 | 相同组名对应不同 source key | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-41 | Domain member 变化且无新 domain-shared evidence | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-42 | 创建前无 Site 权限 | PARTIAL_OFFLINE_PASS | TaskScope：缺Site授权不创建目标；HTTP Gate pending |
| SC-43 | 创建后、执行前撤销 Site | PARTIAL_OFFLINE_PASS | TaskScope：排队撤权不连接；HTTP Gate pending |
| SC-44 | 执行部分目标后撤销 Site | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-45 | 执行期间在下一个 protected boundary 撤销 | PARTIAL_OFFLINE_PASS | TaskScope：connect/command/result边界撤权；HTTP Gate pending |
| SC-46 | 不可安全中断的 bounded operation 中撤销 | PARTIAL_OFFLINE_PASS | TaskScope：有界原子输出PARTIAL保留；HTTP Gate pending |
| SC-47 | 执行期间新增 Site 权限 | PARTIAL_OFFLINE_PASS | TaskScope：新增claims不扩大目标；HTTP Gate pending |
| SC-48 | 执行完成后撤销 Site | PARTIAL_OFFLINE_PASS | TaskScope：完成后读取撤权；Report未实现；HTTP Gate pending |
| SC-49 | 部分 Site 权限撤销 | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-50 | task cancel 与 claims revoke 同时发生 | PARTIAL_OFFLINE_PASS | TaskScope：cancel与revoke双原因；HTTP Gate pending |
| SC-51 | claims revision 变化但有效 Site 集合不变 | PARTIAL_OFFLINE_PASS | TaskScope：revision-only变更指纹/审计；HTTP Gate pending |
| SC-52 | resume 时原授权已失效 | NOT_IMPLEMENTED | 完整场景尚缺identity/domain/group持久生命周期或多目标Plan/Report；子能力见增量验收 |
| SC-53 | Generic Task/Report/Audit API 越权读取 | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-54 | 未授权 AC Domain list/detail | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-55 | AP Resource ID 猜测 | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-56 | Serial/MAC 越权搜索 | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-57 | 分页/count 与 claims 缺失或失效 | BLOCKED | B01/B02/B04或deadline来源未取得 |

增量映射仅更新有代码/SQLite证据的子场景；PARTIAL_OFFLINE_PASS不等于完整原场景E2E通过。SC-33/44/49等多目标/Report与SC-52恢复仍未实现，NEVER restart保持不变。
