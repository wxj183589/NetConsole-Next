# R7.3 测试证据

2026-11-08，codex-A。首批产品检查点 `021c760`。所有输出位于Next Workspace。

| 检查 | 本轮数量与结果 | 实际证据范围 |
| --- | --- | --- |
| Web完整 | 34文件351项PASS，typecheck PASS | 真实Vue组件、mock API；`reports/r73-initial/web-tests-first.log` |
| Web Release | PASS，无大JS chunk警告 | 真实Vite构建与终端bundle；`reports/r73-initial/web-build-integrated.log` |
| Worker完整 | 49/49 PASS | CPython worker单元/seam，未连接模拟器 |
| Service完整 | 前两批完整Rebuild后退出0 | `reports/r73-service-full-final.log`，真实SQLite/Raw/Worker子进程与合成transport，不等同设备互操作 |
| 配置批次 | 20/20 PASS | 当前claims/owner、混合Site整批隐藏、损坏store、终态metadata重载 |
| SimulatorConfiguration | 21/21 PASS | 合成transport写入/重读/回滚与存储路径、dangling链接、容量，非设备写入 |
| 恢复readiness | 16/16 PASS | 合成独立读、活跃历史和阻断语义 |
| OperationalHealth | 15/15 PASS | 空完整LLDP表OBSERVED0、严格来源、新鲜度及旧路由回归 |
| FaultCenter | 22/22 PASS | LLDP同分区恢复、生命周期、策略和持久性 |
| H3cTaskFactPublication | 20/20 PASS | scheduler→真实Raw封存/hash→Facts→故障→恢复→重建→复发，含1100revision历史挤占回归 |
| ICMP诊断单元 | 3/3 PASS | 注入生产probe seam，固定回环/错误码/退出码/无Host或数据初始化 |
| HTTP | 40检查PASS | 实际Service/SQLite/auth/CSRF/module gate/claims撤销/重启；无设备连接。`reports/r72-feature-http-20261108-173340-f42fb0.json` |
| Browser既有 | 10场景PASS | 真实Service静态Web/现有隔离存储，API/页面/控制台错误0；`reports/r73-browser-regression/result.json` |
| Browser新增 | 4场景PASS | 图表、LLDP类型、存储与批次、独立Domain无线观察CSV；`reports/r73-browser-product-fixed/result.json` |
| Workspace paths | Python4/4及JS PASS | 本机路径/重解析边界回归 |
| LocalManager | doctor13/13、portable9/9 PASS | 本机最小fixture和真实Worker导入/关闭，不启动主线第二实例 |
| SCM PlanOnly | 新5/5、旧upgrade5/5 PASS | 参数/保留/隔离计划，未安装或启动服务 |
| apphost/dotnet实际probe | PARTIAL | 同DLL、同token、固定回环；EXE native5，dotnet Success，`reports/r73-icmp-host-diagnostics.json` |
| 设备TCP | 4/4 TCP_TIMEOUT | 白名单每目标一次3秒，未认证、未采集；不计互操作PASS |

完整Service为自有harness，各组按自身打印计数，未编造统一xUnit总数。包括retry172/172、Worker预算/退出9组、并发6/6、迁移崩溃注入、Site claims、无线runtime14/14、scope10/10、LLDP6/6、QuickPing7/7、Traceroute15/15与Syslog8+6组。FailFast属于预期子进程注入，父级通过。

## 实际持续运行

`tests/r73_unattended_http.py --duration-seconds 300` 使用全新最小根，未复制任何数据根。实测monotonic 300.0秒、30个采样、受控Service重启一次、30条回环UDP全部封存与SHA256/字节/条数/发送者验证、SQLite integrity ok、PROCESSING故障跨重启保持。没有修改系统时间。

证据为 `reports/r73-unattended-980acd58f83a4cc8972972b9c0031737/result.json`。PID6128的RSS从68481024到98394112bytes，峰105967616；重启PID33328从68833280到100495360bytes，峰107892736。观测包含启动/加载增长，时间不足以判断长期泄漏。首次采样文件714961bytes，退出并checkpoint后703701bytes，不能用最终差值解释持续文件增长趋势。

该窗口H3C/终端关闭，真实Worker与自然设备计划未被该300秒测试覆盖，相关故障注入另由完整Service harness覆盖。没有24小时稳定性结论。测试根保留约704kB必要证据。

## 原始失败保留

- HTTP新增存储fixture最初遗漏terminal_protocol，继承AUTO后被精确SSH目标规则拒绝403。补显式SSH，保留产品规则，40检查复验通过。失败报告 `reports/r72-feature-http-20261108-173300-b937f9.json`。
- 浏览器首轮把单Domain过滤误要求为跨Domain包含两AC，修正为逐Domain真实操作，过滤约束保留。原始失败 `reports/r73-browser-product/result.json`。
- 无线刷新回归最初点中任务历史“刷新”，改点页面“刷新数据”后保留旧数据必须清除断言并通过。

## 第三批集成复验

产品故障修复提交 `5638c2f` 后，在当前源码重新运行完整 Release Service.Tests harness：进程退出码 `0`。输出确认 `FAULT_CENTER_TESTS=23/23`、`OPERATIONAL_HEALTH_TESTS=15/15`、`H3C_TASK_FACT_PUBLICATION_TESTS=20/20`；同一完整 harness 还复验了配置批次20/20、模拟器配置21/21、恢复Readiness16/16、Worker预算9组、Netmiko Retry172/172、并发6/6、Quick Ping7/7、Syslog Query8/8和Events6/6等既有组。

本次 Web 全量复验为34 files/351 tests，`pnpm typecheck`通过。当前 18771 Service（PID 33036）与5174 Vite（PID 32812）均在检查前后保持运行，Service health 为HEALTHY。运行实例是既有集成构建；本次没有为了源码复验覆盖它或中断其隔离数据。HTTP 40项及Browser 14场景沿用此前同轮报告中的结果，没有把本次 C# harness 说成新一次浏览器/API集成。
