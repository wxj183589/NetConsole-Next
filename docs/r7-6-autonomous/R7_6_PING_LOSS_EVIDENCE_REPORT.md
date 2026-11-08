# R7.6 丢失采样历史证据集成检查点

日期：2026-11-08。状态：**PASS / FIXTURE_E2E**；R7.6 总体 **PARTIAL**。Agent codex-A；Thread 01a1ba51-04f2-71a2-ac64-17bb40048093；Branch codex-A/netconsole-next-r3-dual-ac。仅本地提交，禁止 push、remote merge、release publish。

## 产品行为

Long Ping 历史中选择实际 TIMED_OUT/UNREACHABLE 采样，点击“查看采样前证据”，读取当时以前完成的同目标事实及 Raw 引用。REPLIED 和未发送序号不能作为丢失采样查询。查询不启动设备连接或采集。

精确核对当前 actor/Site claims、历史设备/Site、诊断类型、数值 IP、协议/TCP port、profile revision、capturedScope；先限制 captured_ms 和 task ended_ms 不晚于采样，再排序取每类身份/authority/scope 的最近历史。不用 is_current 排除有效历史，不以未来 Current 回填过去。最多检查21条候选返回20条并展示年龄；接入 AP 缺少权威客户端关联事实时始终 UNKNOWN，不从管理设备身份或邻近时间推断因果。

复用正式 Raw reader 核对路径/reparse、文件长度、封存记录身份、segment SHA256 和 output 解码大小/SHA256。每请求缓存 run 验证，每事实最多32引用。缺失/损坏 Raw 的事实排除，精确恢复文件后重新请求重验。响应 no-store；切换采样/目标、失活和卸载均使迟到响应失效。

## 本地提交与验收

- `56d16e3`：历史上下文 API/UI；`6e48a0a`：实际 Raw 文件完整性和确定排序修复。
- `d99e103`、`8cd1e2f`：产品封存 Raw 夹具、真实 HTTP 和 Chrome 验收；`0dd6a14`：容许正常 STOPPED 的 CORE_SERVICE 夹具运行记录，业务任务计数不含核心服务生命周期。
- Service 定向37/37 PASS：reports/r76-loss-final-unit.log。含当前产品修改的 Service 全量 EXIT0：reports/r76-service-loss-full.log；之后只有测试夹具修改。
- Web36files/391tests与typecheck PASS：reports/r76-web-loss-full.log、reports/r76-web-loss-typecheck.log。Release构建0warnings/errors：reports/r76-loss-fixture-lifecycle-build.log。
- 候选发布/离线 Worker smoke PASS：reports/r76-loss-candidate-publish-restored.log。Vite输出到Workspace构建目录，没有改写源wwwroot。
- HTTP18checks PASS：reports/r76-ping-loss-http-69b9b64b7d40/result.json。匿名/角色/Site/撤权、未来事实排除、年龄和Raw引用、no-store、损坏/丢失/恢复Raw、无效采样与不建任务均通过。
- 实际Chrome1920x1080和2560x1440共18checks PASS：reports/r76-ping-loss-browser-2026-11-08T14-44-52-709Z/result.json。实际响应、来源和两级SHA可见，未来事实缺席，清空/刷新不重放，无pageerror/横向溢出。fixture已停止且业务任务数未变。
- 最新共享候选广覆盖浏览器PASS：reports/r76-loss-broad-browser-20261108/result.json，总览/故障/配置/无线四组、两视口。

HTTP/浏览器输入为产品Catalog、H3cTaskRawResultSink和发布路径生成的两条合成封存Raw/Facts，192.0.2.71不被连接，设备连接/配置写入为0。证据等级是FIXTURE_E2E，不能作为远端实际丢包、无线接入或HA验收。正常Service退出记录导致的首次夹具拒绝已修复，失败证据reports/r76-ping-loss-http-2b0deb9e2b72保留。

## 最新候选和运行

- Candidate：build/r76-loss-context-8cd1e2f。
- ProductVersion：1.0.0+8cd1e2f96038acef6c63181ad49cb6d7c4920403。
- DLL SHA256：940bf1eeea03c3fa21947ab33823553a2a60c8d4e9f2a2e80811bdcac5210ffd。
- Service：http://127.0.0.1:18771/，PID41856，HEALTHY；bundled Worker、H3C enabled、Syslog loopback UDP15514。
- Vite：http://127.0.0.1:5174/，PID32812，保留。
- Data：test-data/simulator-live-20261105。SQLiteok、140FINALIZED Raw逐项长度/SHA256通过、0incomplete/missing、recoveryissues0。
- 本次核验：reports/r76-loss-post-upgrade-integrity.json；19受保护assets不变。原单DB检查点test-data/r76-runtime-checkpoint-20261108/catalog.db保留，SHA256为5fba203ba26a99caa2f9c05229646415f0e9bd1b64b048975f352e180098173f，integrityok。

候选包含至6e48a0a的产品代码；嵌入提交8cd1e2f，之后0dd6a14只改测试。旧候选/PID49344及370tests属于此前批次，不能作为当前值。相对bundle路径和缺RID restore的失败日志、部分候选build/r76-loss-context-candidate均保留；成功打包使用独立已restore artifacts。

## 尚未闭环与续跑

完整离线APPHOST/显式DLL仍采用此前reports/r76-offline-apphost.log、reports/r76-offline-dotnet.log的EXIT1 / ICMP_PERMISSION_DENIED证据。本批未重复完整门禁，打包成功和窄ICMP检查不能宣称完整离线PASS。此前真实回环LongPing HTTP13checks与浏览器保留为较早候选证据，没有写成本批重跑。

AC1/AC2既有12条只读Raw/Facts属于SIMULATOR_READONLY。本批没有新增设备连接；SW有效凭据、FIT原生认证/exec-console读回、客户端到AP关联、双AC HA、设备Writer/readback/business/rollback、elevated SCM、跨机、8–24h长稳仍待验。历史300秒不升级为长期证据。

下一批先核对实际HEAD、PID、AGENTS和状态文件，继续权威客户端/AP时间合同、完整Host ICMP诊断及上述外部验收。全局Service mutex跨数据根，隔离HTTP测试需受控停共享实例后恢复。使用tests/r70_runtime_control.py与精确expected-dll/PID，不强停共享console。不重复开发已完成历史上下文，不用同名/IP/时间邻近补造接入关系。

未访问旧NetConsole数据根、未复制完整Site/数据根、未修改原检查点、未删除19静态资源或其它dirty/未推送worktree。各实现Agent已完成，本检查点无活动实现任务。DevStatus仅新增本批交接文件，本地提交、不推送。
