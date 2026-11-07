# R7.0 故障检测与受控只读恢复

日期：2026-11-07 开发，2026-11-08 最终部署浏览器验收（Asia/Shanghai）

状态：PARTIAL。产品代码与定向测试已落地，已通过本轮部署的真实浏览器→认证 API→授权模拟器连接/补采链。不将单元测试、TCP 可达或任务排队视为业务恢复验证。

## 已实现

- `GET /api/system/operations/health`：先限制当前 Site scope，再读取授权库存、Raw 校验通过的 Current Facts、近期采集任务、诊断结果和既有无线运行态。
- Dashboard 的设备健康面板：每页 25 台设备，显示设备身份、Site、证据状态、接口 DOWN 观测数、LLDP 邻居观测数、Ping 平均时延/丢失率，以及证据捕获时间。
- CPU、内存及缺少有效来源的指标明确为 UNKNOWN。接口和 LLDP 数量是已观测条目的数量，不表示完整设备总量。
- 统一事件：`INTERFACE_DOWN`、`COLLECTION_FAILURE`、`EVIDENCE_STALE`、`PROBE_PACKET_LOSS`、`AP_OFFLINE`、`AP_CONFLICT`、`AP_EFFECTIVE_STATE_CHANGED`。事件提供证据类型和 ID，允许读取对应的授权事实、任务、诊断或无线运行态。
- `POST /api/system/operations/recover`：SUPERADMIN 明确选择设备后执行连接恢复探测，成功后提交现有只读采集计划；不执行设备配置写入。
- 恢复 UI 保留每次探测结果，并通过既有 TaskPlan report 显示后续版本/接口命令的 Run、Raw 和 Parse 状态。可手动重新读取补采结果。
- KeepAlive 页面停用时清除旧响应的生效资格；重新激活时重新读取当前授权数据。

## 证据与资源边界

健康证据窗口为 300000ms。`CollectedRawVerified` 和捕获时间共同决定健康证据资格；兼容 Catalog 中保留的 `STALE` 标签，不把该历史 schema 标签直接当作实时过期判定。FAILED、PARTIAL、EMPTY、UNSUPPORTED、未来时间和离线 fixture 不成为有效实时健康指标。

每次健康查询最多读取本页设备的每设备 1000 条 Current Facts、100 项近期授权任务、20 项授权诊断历史；事件按时间排序最多显示 100 项。无线事件最多检查本页 AC 关联的 10 个 AP，仅在运行态所有来源均有新鲜 LIVE_VERIFIED 证据时产生实时状态/变化事件。页面公开这些范围限制，未实现完整无限历史事件库。

Ping 指标只读取已结束的真实 Quick Ping 样本。设备 ID、Site、当前管理 IP 和 TCP 端口必须匹配；不因旧诊断 ID 相同而把旧端点结果当成当前端点结果。Ping 或连接失败不证明设备离线；无线 OFFLINE 则必须有既有无线运行态接受的设备状态证据。

## 只读恢复边界

- 全局最多同时执行 1 个恢复请求；额外请求返回 busy。
- 单个请求最多 3 次连接探测，整体执行预算 120 秒，重试间隔有界。
- 只对 `CONNECT_TIMEOUT`、`UNREACHABLE`、`CONNECTION_FAILED` 重试。
- `AUTHENTICATION_FAILED`、`CREDENTIAL_UNAVAILABLE`、`HOST_KEY_REJECTED`、协议错误立即停止，不重复撞认证。
- 每次探测前和提交补采前重新检查活动账号、角色、Site scope、库存设备身份、档案绑定、启用状态、主机与端口。
- 取消在探测后及补采提交前重新检查，避免取消后继续排队。
- 连接成功包含 `CONNECTED` / `DEVICE_INFO_UNRECOGNIZED`，后者仅表示认证已建立，不能据此宣称设备信息解析成功。
- 补采严格复用既有 allowlist 和 `H3cBatchTaskService`，只执行 `display version` 与 `display interface brief`，保留原失败与后续采集结果。
- 启动、每次探测状态、结束/取消/失败写入既有设备审计。补采使用持久化 TaskPlan/Run/Command 作为结果 authority，不引入重复的恢复数据库或 JSON 状态库。

结果区分 `CONNECTION_NOT_RECOVERED`、`RECOLLECTION_QUEUED` 和 `RECOLLECTION_NOT_QUEUED`。读取到命令 SUCCEEDED 也只表示本次补采完成；业务恢复仍需相应的业务状态与后续证据核验。

## 定向验证

- C# `--operational-health-tests`：最终完整 Service harness 7/7 PASS。覆盖无证据 UNKNOWN、fixture/未来/部分数据排除、接口证据、过期证据、Raw-verified / legacy STALE 兼容、瞬态恢复、认证失败不重试、三次预算、每次重试权限检查和取消。
- Web `OperationalHealth.test.ts`：5/5 PASS，覆盖 UNKNOWN 与空态、明确选择后恢复、失败探测历史与最终补采证据、只读账号隐藏恢复、读取失败清理旧库存、KeepAlive 隐藏响应失效与重新激活刷新。
- `node --check tests/r70_browser_acceptance.mjs`：PASS。
- 本工作流新增文件 `git diff --check`：PASS。
- 初版 Service 隔离输出构建：0 warnings / 0 errors。最终集成构建、全量测试和配置工作流的客户端符号集成由主 Agent Gate 统一执行。

## 模拟器与浏览器验收

已执行 `tests/r70_browser_acceptance.mjs`，使用本轮部署的 `r70-service` 和当前 Vite 源码。真实 Chrome 登录后，通过 Vue 页面→认证 API→`r68-ac1` / `192.168.56.2:22` 验证：

- Quick Ping 任务 `7e1b3457927e4b2a9f0a65ac738560a5`，TCP 样本 3/3 成功、丢失率 0%，平均时延 0.67ms；这些是服务主机 TCP 探测，不标称为 ICMP。
- 健康工作台读取 6 台授权库存，缺少 CPU/内存证据时全部保持 UNKNOWN；当前 AC 证据已过期时显示 UNKNOWN / EVIDENCE_STALE。新 Quick Ping 结果正确投影为 OBSERVED。
- 明确选择 AC1 执行只读恢复，操作 `recovery-b702e4c77b2849caa6690e2b36bceee6`。第一次连接为 CONNECTED，随后既有 TaskPlan 最终 SUCCEEDED。
- `display version`、`display interface brief` 两条命令均 SUCCEEDED，Raw=COMPLETE，Parse=PARSED。页面“读取补采结果”显示上述最终状态，同时保留连接探测结果与“不代表业务恢复完成”的边界。
- Quick Ping、健康和只读恢复页面均记录 1920×1080 / 2560×1440 截图，document 宽度分别等于 viewport，没有横向溢出。

初轮证据：`D:\study\NetConsole-Next-Workspace\reports\r70-browser-20261107\result.json` 及同目录 `quick-ping-*`、`operational-health-*`、`read-only-recovery-*` 截图。初轮总报告为 PARTIAL，B 项均 PASS。

最终部署（Service PID 44424）再次完整运行于本地时间 2026-11-08 00:00:55–00:01:12：Quick Ping `875c6f3890324b39bafca04cc2798fee` 再次 3/3 TCP 成功，平均 0.67ms、丢失 0%。恢复 `recovery-c5f8d551a33b47b3903db20baaeb7121` 一次 CONNECTED，补采 Run `845c17ba17db467ab1aafcd8f7696df8` 及两条命令最终 SUCCEEDED / COMPLETE / PARSED。证据位于 `D:\study\NetConsole-Next-Workspace\reports\r70-browser-final-20261108\result.json` 与同目录截图；API / 页面 / 控制台错误均为 0。设备详情同时正确显示该 Run 的身份、接口证据为 VERIFIED / RECENT。

最终全产品 UI 验收状态及创建选项的独立复验另见 `R7_UI_ACCEPTANCE_REPORT.md`。

当前没有主动制造设备宕机，也没有把正常设备上的成功探测表述为真实宕机恢复。配置差异故障事件自动聚合、通知投递、无用户选择的定时自动修复不在当前交付的已验证能力内。

## 数据影响

只使用 NetConsole-Next 授权数据根。未访问旧 NetConsole Production / Dev 数据根，未复制 Site 或数据根，未向设备写入配置。允许的本地写入仅为既有审计、采集任务、原始采集证据和运行态投影。
