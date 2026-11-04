# 测试矩阵

测试计数按不同粒度分开：14个WirelessTests方法、5个Vitest用例、9个Browser状态截图。Service harness是独立suite，内部未提供精确断言总数，不能与这三者相加冒充统一test case数。HTTP test因全局Mutex阻断，不计产品FAIL。

| Gate | 本轮结果 | 证据 |
| --- | --- | --- |
| Service + Tests Release build | PASS，0 warnings/errors | dotnet build |
| LocalManager Release build | PASS | dotnet build |
| Service harness | PASS | Workspace/build/autonomous-20261105/service-harness-final.log |
| Wireless定向 | 14/14 PASS | WirelessTests.Run；包含最小隔离catalog、101台AC及跨Site负向断言 |
| 现有设备/claims/凭据/调度/解析/Raw回归 | PASS（核心suite） | Service harness；不虚报HTTP 31/31 |
| Vitest | 5/5 PASS | npm test |
| Vue typecheck | PASS | npm run typecheck |
| Vite/terminal build | PASS，现有chunk-size warning | web-build.log |
| Browser离线fixture | PASS，9状态 | gui/evidence.json；错误态预期503/403，不是非预期页面异常 |
| HTTP API隔离E2E | BLOCKED B02 | 实际启动被Mutex拒绝 |
| Service-backed Browser E2E | BLOCKED B02 | 未拿旧进程替代新代码 |
| Windows原生125% | BLOCKED B04 | DPI96/DPR1 |
| git diff --check | PASS | 本地检查 |

原有57场景下列PASS仅表示所描述的不变量在离线服务投影/最小catalog中验证，不表示端到端Gate均通过。保守保留尚未覆盖的场景，未删除或弱化原断言。17种设计fixture未全部落地；已有1份双AC JSON和代码内最小fixture。

| ID | 原场景 | 状态 | 证据/缺口 |
| --- | --- | --- | --- |
| SC-01 | 单 AC standalone domain | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-02 | stack 多 member | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-03 | cluster evidence 不完整 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-04 | one-plus-one HA | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-05 | member/site revision 冲突 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-06 | serial+完整 MAC 唯一 | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-07 | serial 缺失、MAC 唯一 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-08 | MAC 缺失、serial 唯一 | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-09 | serial 与 MAC 互相冲突 | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-10 | 多接口 MAC | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-11 | AP 更换后 serial/MAC 变化 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-12 | 跨 AC duplicate identity | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-13 | AP 仅被 AC-A 观察 | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-14 | AP 被 AC-A、AC-B 同时观察 | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-15 | association ended | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-16 | raw `R/M` | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-17 | raw `R/B` | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-18 | raw `I` 且 profile 明确 inactive | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-19 | raw `I` 且 profile 未确认 | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-20 | collection timeout | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-21 | stale observation | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-22 | 多来源互斥 state | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-23 | 无变化刷新 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-24 | claims 仅允许 AC-A | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-25 | claims 允许 AC-A、AC-B | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-26 | AP 跨 Site partial visibility | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-27 | no Site authorization | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-28 | 同名 group 跨 AC | PASS | WirelessTests（离线/最小catalog，不计HTTP） |
| SC-29 | group membership 变化/空组 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-30 | SUPERADMIN wireless read | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-31 | task create scope snapshot | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-32 | task read after claims revoke | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-33 | task partial current claims | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-34 | task failed/timeout/cancel | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-35 | audit manifest/hash/no-secret | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-36 | 同一 Domain 下不同 AC 观察到同名组 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-37 | source evidence 明确 Domain-shared group | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-38 | group 缺 source AC ID 或 source key | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-39 | 同 scope stable source key、display name 变化 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-40 | 相同组名对应不同 source key | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-41 | Domain member 变化且无新 domain-shared evidence | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-42 | 创建前无 Site 权限 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-43 | 创建后、执行前撤销 Site | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-44 | 执行部分目标后撤销 Site | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-45 | 执行期间在下一个 protected boundary 撤销 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-46 | 不可安全中断的 bounded operation 中撤销 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-47 | 执行期间新增 Site 权限 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-48 | 执行完成后撤销 Site | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-49 | 部分 Site 权限撤销 | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-50 | task cancel 与 claims revoke 同时发生 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-51 | claims revision 变化但有效 Site 集合不变 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-52 | resume 时原授权已失效 | NOT_IMPLEMENTED | 持久生命周期、group source key或task snapshot合同尚未实现 |
| SC-53 | Generic Task/Report/Audit API 越权读取 | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-54 | 未授权 AC Domain list/detail | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-55 | AP Resource ID 猜测 | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-56 | Serial/MAC 越权搜索 | BLOCKED | B01/B02/B04或deadline来源未取得 |
| SC-57 | 分页/count 与 claims 缺失或失效 | BLOCKED | B01/B02/B04或deadline来源未取得 |
