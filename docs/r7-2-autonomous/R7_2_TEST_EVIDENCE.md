# R7.2 测试证据

2026-11-08，codex-A。接续基线 `065a749`，同分支未提交R7.2工作一并集成。

| 类别 | 结果 | 证据与边界 |
| --- | --- | --- |
| Web UNIT_PASS | 32文件339项通过 | Workspace `reports/r72-web-tests-continuation.log`，typecheck通过 |
| Web构建 | PASS，含大chunk警告 | Vite及终端bundle完成，最终Service静态页面已真实浏览器访问 |
| Worker UNIT_PASS | 49/49 | `python tests/netmiko_worker_test.py`，涵盖恢复能力探测和关闭 |
| Service完整harness | Rebuild后完整PASS，退出0 | 原始失败保留于 `reports/r72-service-harness-continuation.log` 和 `reports/r72-service-harness-final.log`；成功复验日志 `reports/r72-service-harness-rebuilt-pass.log` |
| 恢复定向 | readiness16、checkpoint14、Simulator19通过 | `reports/r72-restore-closure-readiness.log`，fixture不升级为设备恢复通过 |
| 旧配置恢复组 | 默认仓库bin复验PASS | 独立ArtifactsPath曾失败，原因是fixture源码根解析，默认bin的 `--config-restore-tests` 已通过 |
| 拓扑/无线定向 | scope10、publication18、runtime14、LLDP6、AC资源4通过 | Current兼容迁移、独立分区/退役、旧五列与真实八列Radio |
| Windows定向 | doctor13、portable9、升级PlanOnly5通过 | `reports/r72-operations/scm-permission-continuation.json` 明确当前Win32 5 |
| HTTP INTEGRATION_PASS | 35检查通过 | `reports/r72-feature-http-20261108-164750-0a0c39.json`，含撤销claims、CSRF、禁用功能、故障处理幂等/重启、损坏批次保留 |
| BROWSER_PASS | 10场景通过 | `reports/r72-browser-final-complete/result.json`，真实Service静态Web，1920与2560宽，0 API错误/页面异常/控制台错误 |
| 持久Raw/SQLite | 90/90段通过，integrity ok，schema27 | `reports/r72-runtime-final.json`，逐段SHA256、输出SHA256/字节数、run/命令核对；无新设备成功假设 |
| Service重启 | PASS，最终HEALTHY | 旧PID4012受控退出，新PID32828，保留同一隔离数据根，前端可读历史 |
| 本次模拟器采集 | PARTIAL，TIMED_OUT | `reports/r72-product-dual-ac-20261108-164308/summary.json` 与 `reports/r72-product-lldp-scope-20261108-164341/summary.json` |
| DEPLOYMENT_PASS | 未达到 | 当前SCM创建权限拒绝，交互生命周期历史结果另见Windows报告 |
| REAL_DEVICE_PENDING | 保留 | 物理设备、整机恢复/回滚、HA业务恢复、FIT和长稳未通过 |

## 失败及修复轨迹

1. QuickPing测试以对象构造只接受string的ApiRequestError，Vitest通过而typecheck失败。修正测试参数后完整typecheck通过。
2. Traceroute健康fixture缺ManagementIp，不能通过精确资源核验。补齐fixture规范字段，保留原断言，OperationalHealth13/13通过。
3. 完整Service首次走到AC资源组时，旧总数遗漏新增ap.radio事实。总数明确包含controller.role与ap.radio，保留逐命令Raw断言，新增旧格式Radio精确字段与provenance断言。后续增量构建曾运行旧二进制，已安排完整Rebuild后回归。
4. 新浏览器批量原件检查加载了另一张表，旧测试按最后一张表寻找计划明细超时。改按“固定设备范围”表头精确定位，原明细断言保留，10场景最终通过。失败证据仍在 `reports/r72-browser-continuation-dev/result.json`。
5. 恢复预检运行中查询历史会误标中断，现有活跃操作登记防止假中断与误清理。新增回归并接入完整Service入口。
6. 故障恢复原先接受同设备任意后续成功任务，现要求相同完整命令计划、类型、冻结身份及端点。

浏览器检查含本地完整配置原件和多台原件加载、恢复历史、脱敏配置与比较下载、故障筛选/CSV、Quick Ping历史/CSV、设备LLDP详情、来源拓扑、Syslog和快捷入口。浏览器未点击发起设备采集、配置写入或恢复操作。

产品提交 `2157206ac54b99ec55617f51a57c8f80cf819fe9`。完整harness包含Fault21/21、Operational13/13、readiness16/16、AC资源4/4、publication18/18、配置计划19/19、Traceroute15/15、并发6/6、retry172/172及迁移崩溃注入。FailFast输出属于预期子进程故障注入，父级验证通过。

`git diff --check` 对真实Radio回放fixture的三行尾随空格报告告警，保留该采集文本字节；源码和文档未有其他空白错误。构建产物只提交当前index可达的依赖图，未纳入先前无引用的untracked静态资源。
