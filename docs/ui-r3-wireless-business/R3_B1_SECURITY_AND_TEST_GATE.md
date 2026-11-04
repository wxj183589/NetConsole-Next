# R3-B1 安全与测试门禁

基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`  
状态：`DESIGN_ONLY / NOT_RUN_DOC_ONLY`

## 1. 第一条 route 即授权

W01/W02/W03 及 pagination、count、direct ID、serial/MAC filter 均服务端复用 R2 `P2B5` predicate。授权顺序固定为：解析当前 Site claims → 计算可见 AC/domain/observation → 过滤业务资源 → 聚合 summary/count。客户端不能提交 scope 覆盖；generic Task API 不能旁路；W09 不新增 route。

安全要求包括：

- 未授权 observation 不影响 AP summary、state、group、conflict 或 count；
- 跨 Site direct ID、serial/MAC 查询不得泄露资源存在性、名称或计数；
- 不返回凭据、密码、raw command output 或未授权 raw manifest 内容；
- identity conflict、unknown state、stale 和 collection failure 按合同返回，不降级成 confirmed/online；
- D06 的 revoke 在 execution/resume/read/download/report/audit boundary 重新检查；
- D07 的 audit 只引用安全 provenance，不写敏感原文。

## 2. 场景与 fixture 计划

`R3_B0_TEST_SCENARIO_MATRIX.md` 已定义 `SC-01..SC-57`，覆盖 AC/domain、serial/MAC identity、multi-AC visibility、AP Group amendment、runtime state、Task revoke、audit provenance、count/no-leak 和 R2 regression；`F01..F17` 是最小 fixture 集。

本轮仅审计设计，不执行产品测试；测试状态统一为 `NOT_RUN_DOC_ONLY`。真实设备、真实网络和真实任务不是 B1 离线 gate 的必要前置，也不在本轮调用。

## 3. 未来 Gate 清单

| Gate | 当前状态 |
| --- | --- |
| `AC_INVENTORY_IDENTITY` | `NOT_RUN_DOC_ONLY` |
| `AC_DOMAIN_IDENTITY` | `NOT_RUN_DOC_ONLY` |
| `AC_DOMAIN_MEMBER_RELATION` | `NOT_RUN_DOC_ONLY` |
| `AP_RESOURCE_IDENTITY` | `NOT_RUN_DOC_ONLY` |
| `AP_OBSERVATION_PROVENANCE` | `NOT_RUN_DOC_ONLY` |
| `SITE_AUTHORIZATION` | `NOT_RUN_DOC_ONLY` |
| `CROSS_SITE_DENIAL` | `NOT_RUN_DOC_ONLY` |
| `IDENTITY_CONFLICT` | `NOT_RUN_DOC_ONLY` |
| `UNKNOWN_STATE_HANDLING` | `NOT_RUN_DOC_ONLY` |
| `NO_SECRET_LEAK` | `NOT_RUN_DOC_ONLY` |
| `NO_UNAUTHORIZED_COUNT_LEAK` | `NOT_RUN_DOC_ONLY` |
| `REGRESSION` | `NOT_RUN_DOC_ONLY` |

## 4. 隔离执行要求（未来）

未来实现测试使用单个隔离 SQLite candidate、最小 fixture、RFC 5737 文档地址或 mock transport、独立端口和互斥锁；不复制完整 Site/数据库，不连接 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`，不与用户当前 UI preview service 争用端口。任何需要真实设备或真实写入的测试必须另行授权并单独记录。
