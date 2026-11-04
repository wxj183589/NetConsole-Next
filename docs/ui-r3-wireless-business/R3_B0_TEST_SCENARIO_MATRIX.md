# R3-B0 测试场景矩阵

本矩阵是实现前测试设计，不表示本轮已执行。共 35 个场景、12 个最小 fixture；真实 AC/AP、网络扫描和真实 task execution 均明确排除。

## 1. 场景清单

| ID | 场景 | 预期 | Fixture | Gate |
| --- | --- | --- | --- | --- |
| SC-01 | 单 AC standalone domain | confirmed domain，引用既有 AC device ID | F01 | B |
| SC-02 | stack 多 member | domain/member 全量可追溯 | F02 | B |
| SC-03 | cluster evidence 不完整 | candidate/unknown，不猜 topology | F03 | B |
| SC-04 | one-plus-one HA | topology kind 与成员 evidence 一致 | F04 | B |
| SC-05 | member/site revision 冲突 | conflict，禁止静默合并 | F05 | B/D |
| SC-06 | serial+完整 MAC 唯一 | AP identity confirmed | F06 | B |
| SC-07 | serial 缺失、MAC 唯一 | candidate/confirmed 依批准规则，保留 evidence | F07 | B |
| SC-08 | MAC 缺失、serial 唯一 | candidate，不使用名称补齐 | F07 | B |
| SC-09 | serial 与 MAC 互相冲突 | conflict，返回 reason/evidence refs | F08 | B/C |
| SC-10 | 多接口 MAC | 保留完整集合和角色，不选第一条 | F09 | B |
| SC-11 | AP 更换后 serial/MAC 变化 | replaced/new candidate，不自动 merge | F10 | B |
| SC-12 | 跨 AC duplicate identity | relation/provenance 分开，需 review | F05/F08 | B/D |
| SC-13 | AP 仅被 AC-A 观察 | 单一 active association | F06 | B |
| SC-14 | AP 被 AC-A、AC-B 同时观察 | 两条 observation，聚合带 provenance | F11 | B/D |
| SC-15 | association ended | Current 结束，Recent10 记录有效变化 | F10 | B |
| SC-16 | raw `R/M` | normalized `RUNNING_MASTER`（profile confirmed） | F02 | B |
| SC-17 | raw `R/B` | normalized `RUNNING_BACKUP`（profile confirmed） | F02 | B |
| SC-18 | raw `I` 且 profile 明确 inactive | `OFFLINE`，保留 raw/reason | F07 | B |
| SC-19 | raw `I` 且 profile 未确认 | `UNKNOWN`，不猜 offline | F12 | B |
| SC-20 | collection timeout | outcome failure，不改 AP offline，Current 保留 | F12 | C/E |
| SC-21 | stale observation | `STALE`，不覆盖有效 Current | F12 | B |
| SC-22 | 多来源互斥 state | `CONFLICT`，列出可见来源 | F11 | B/D |
| SC-23 | 无变化刷新 | 更新时间可变，Recent10 不新增样本 | F06 | B |
| SC-24 | claims 仅允许 AC-A | 只投影 AC-A observation/state/group | F11 | D |
| SC-25 | claims 允许 AC-A、AC-B | 返回 full scope + 两来源 provenance | F11 | D |
| SC-26 | AP 跨 Site partial visibility | `PARTIAL_SCOPE`，不泄漏未授权来源字段/count | F11 | D |
| SC-27 | no Site authorization | 403/404 按合同，零资源 side channel | F12 | D |
| SC-28 | 同名 group 跨 AC | 两个 AC-scoped group，不全局 join | F11 | B/D |
| SC-29 | group membership 变化/空组 | revision/change event，空组可见 | F10 | B |
| SC-30 | SUPERADMIN wireless read | 全 authority 可见但仍保留 source/provenance | F11 | D |
| SC-31 | task create scope snapshot | 保存 claims revision/source subjects | F01/F11 | C |
| SC-32 | task read after claims revoke | 结果不可读，返回 current revoked | F11 | D |
| SC-33 | task partial current claims | 只读可见结果并标 partial，不扩大 scope | F11 | D |
| SC-34 | task failed/timeout/cancel | outcome 与 collection failure 分离，不伪造 AP state | F12 | C/E |
| SC-35 | audit manifest/hash/no-secret | 可追溯 reference，raw/credential 不出现 | F12 | C/D |

## 2. 最小 fixture

| ID | 内容 | 用途 |
| --- | --- | --- |
| F01 | 单 AC inventory + Site claim | standalone/domain/task create |
| F02 | AC stack/HA 成员和 R/M/R/B facts | topology/state |
| F03 | incomplete cluster/unknown topology facts | topology uncertainty |
| F04 | one-plus-one member evidence | topology kind |
| F05 | cross-Site/member/identity conflict | conflict/no-leak |
| F06 | confirmed AP serial + multi-MAC evidence | identity/association |
| F07 | missing serial/MAC and raw `I` variants | candidate/unknown mapping |
| F08 | serial-MAC contradiction | identity conflict |
| F09 | management/radio/other MAC roles | normalization |
| F10 | AP replacement/group membership history | lifecycle/Recent10 |
| F11 | same AP observed by AC-A and AC-B | multi-AC/partial/SUPERADMIN |
| F12 | timeout/stale/failed collection + bounded audit ref | failure/audit |

## 3. 执行门槛

- B0 只完成设计审查和 fixture contract，不运行真实设备。
- B1 实现前必须先将 7 个 decision 从 `NEEDS_USER_DECISION` 改为用户明确选择，并更新 JSON/Markdown 一致性。
- 实现后需按场景逐项记录测试结果、source revision、scope revision 和 evidence ref；“接口返回 200”不能单独作为通过依据。
- Windows native 125% R2 实验不在本轮重跑，仍维持 `PENDING`。
