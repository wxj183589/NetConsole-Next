# R3-B1 API 实施计划（设计阶段）

基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`  
状态：`DESIGNED_NOT_IMPLEMENTED`  
本轮实现数：`0`

## 1. Route 计划

| API | 方法/资源 | 请求 | 响应重点 | 授权与错误 |
| --- | --- | --- | --- | --- |
| `API-W01` | `GET /api/r3/ac-domains`、`GET /api/r3/ac-domains/{id}` | page、pageSize、可选 source AC filter | domain ID、kind、visible members、evidence、revision | 当前 Site predicate；无权不得泄露 member/count |
| `API-W02` | `GET /api/r3/ap-resources`、`GET /api/r3/ap-resources/{id}` | page、pageSize、exact serial、normalized MAC、domain filter | typed identity、status、visible observation summary、provenance | 过滤后计数；ID lookup 仍过 predicate；conflict 不自动合并 |
| `API-W03` | `GET /api/r3/ap-resources/{id}/observations` | page、pageSize、source AC/domain、captured range | source AC/Site、identity basis、raw manifest reference、runtime observation | observation-level claims；隐藏 unauthorized source 与 aggregate |
| `API-W04/W05/W06` | pagination/count/direct ID/exact filters | 见上 | 安全分页与错误 envelope | 无 unauthorized count leak；不做 fuzzy/suffix/cross-Site match |
| `API-W09` | internal predicate only | server context | boolean/typed scope decision | `INTERNAL_P2B5_SCOPE_PREDICATE_NO_NEW_ROUTE` |

## 2. 十个 Gap 追踪

| Gap | B1 处理 | 当前状态 |
| --- | --- | --- |
| G01 AC domain/member | W01 设计和 source mapping | 未实现 |
| G02 typed AP resource/observation | W02/W03 设计 | 未实现 |
| G03 AC↔AP association | observation contract 设计 | 未实现 |
| G04 serial/MAC normalization/conflict | exact filter 与 identity status 设计 | 未实现 |
| G05 normalized state/freshness/confidence | response fields 设计，依赖 state projection | 未实现 |
| G06 multi-AC topology | domain/observation relation 设计 | 未实现 |
| G07 AP group | D04 contract 保留给后续只读 route | 未实现 |
| G08 wireless Site predicate | W01-W03 强制 claims gate；W09 内部复用 | 未实现 |
| G09 wireless plan/task/audit subject | D06/D07 contract，后续批次 | 未实现 |
| G10 AP history/provenance projection | provenance 字段设计，持久 history 未决 | 未实现 |

结论：`API_GAPS_TOTAL=10`、`API_GAPS_ACCOUNTED_FOR=10`、`API_GAPS_IMPLEMENTED_THIS_ROUND=0`、`API_GAPS_REMAINING=10`。

## 3. 通用合同

- 所有 list/detail/observation 先解析当前 claims，再按可见 observation 过滤，最后计算 summary/count。
- `serial` 和 normalized MAC 仅作 exact business filter；不得用模糊匹配、MAC suffix 或名称唯一性替代身份证据。
- direct resource ID 不构成授权；不存在、冲突、无权访问使用统一安全错误语义，不返回其他 Site 的存在性信息。
- response 必须带 `source_ac_id`、`source_site_id/claim_revision`（在当前 authority 允许暴露的范围内）、`captured_at`、`projection_revision` 和 identity/state basis。
- API design 不授权新数据库表、新 migration、缓存写入或 UI 实现。

## 4. 实现顺序与阻断条件

未来获授权后顺序为：claims adapter contract test → W01 domain read projection → W02 AP identity/read projection → W03 observation projection → pagination/filter/count denial tests → R2 regression。若 projection 缺少稳定 provenance、当前 claims 无法在服务端复用、或 schema impact 需要 B 路径，则停止在设计评审，不自行扩大范围。
