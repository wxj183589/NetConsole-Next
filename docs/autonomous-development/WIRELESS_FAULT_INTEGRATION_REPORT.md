# 无线故障中心集成报告

更新时间：2026-11-14（Asia/Shanghai）

## 结论

无线 MESH Syslog 已接入现有 OperationalHealth → FaultCenter 链路，使用已有 TaskRun/Raw、AP observation/lifecycle、Long Ping/Quick Ping 和 FaultCenter 持久化边界；本批没有新增故障数据库，也没有把 fixture 当作在线事实。

## 证据链

1. `Catalog.QuerySyslog` 只读取已封装的本地 Raw Syslog segment，并保留 hash、revision、source、scope 和原始行引用。
2. `OperationalHealthProjection.WirelessSyslogEvents` 只接受精确 AC inventory 管理 IP、允许的 MESH 事件类型和 freshness window 内的事件。
3. 事件类型投影为 `WIRELESS_MESH_LINK_DOWN`、`WIRELESS_MESH_ACTIVE_LINK_SWITCH`、`WIRELESS_MESH_LINK_UP`；down/switch 为 WARNING，up 为 INFO。
4. 资源键包含 sender AC、事件类型和严格解析的 peer MAC；详情不回显未经处理的 Syslog payload。
5. 事件带 `EvidenceType=SYSLOG`、`SourceAuthority=syslog-local`、`CapturedScope=local` 和 Raw path/line evidence ref，随后进入已有 OperationalHealth/FaultCenter 扫描。
6. Syslog 读取失败、权限错误、部分结果或 scope 失效只产生 limitation/partial 状态，不凭空生成故障；没有 fresh evidence 时不宣称恢复或在线。

## 保持不变的事实边界

- AP_OFFLINE/AP_CONFLICT 仍要求已有的 live verified observation 规则；连接会话关闭或一次采集失败不会直接等同设备离线。
- Long Ping、Quick Ping、TaskRun/Raw、LLDP 和既有 FaultCenter lifecycle 继续使用原有证据合同。
- 故障状态不是由 UI fixture、AP 名称、模糊 AC 身份或跨站点相似地址推断。

## 验证状态

| 项目 | 状态 | 证据 |
| --- | --- | --- |
| OperationalHealth 无线 Syslog 投影 | PASS | `OPERATIONAL_HEALTH_TESTS=27/27 PASS` |
| FaultCenter 集成与恢复边界 | PASS | `FAULT_CENTER_TESTS=30/30 PASS` |
| Syslog 查询与封装 Raw 证据 | PASS | `SyslogQuery: 8/8 PASS`、`SyslogEvents: 6/6 PASS` |
| 既有无线任务与 observation 发布 | PASS | `WIRELESS_TASK_CONTRACT_TESTS=5/5` 及无线组测试 |
| 真实 AC Syslog、MESH 链路切换、长时运行 | NOT_VERIFIED | 本批未接入真实设备或真实 Syslog 接收端 |
