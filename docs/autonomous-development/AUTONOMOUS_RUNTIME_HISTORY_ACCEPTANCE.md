# 无线状态历史增量验收（Checkpoint F）

日期：2026-11-05。状态：实现与离线/最小SQLite/浏览器PASS；真实设备与HTTP Gate仍pending。

本批重新核对B03后，确认无需新增schema即可只读重建现有AP的Current、PreviousEffective和最近10次有效变化。此前把整个A10归类数据库阻断过于保守。正式AP identity/replacement Authority和Raw manifest/hash外键仍保留为A20独立方案；不将本次投影冒充新持久数据库。

- `GET /api/system/wireless/ap-resources/{id}/runtime-state`：复用当前Site guard；可按sourceAcId/domainId缩小视图；不存在和不可见都为统一404。物理身份未确认不关联历史。
- 返回AP整体与各独立AC来源的Current/PreviousEffective/Recent10。初始基线不是变化；同值更新Current采集时间但不追加事件；变化倒序最多10。来源/时间变化而AP整体有效值未变也不追加AP级事件。
- 同时刻多来源更新按一个边界聚合，避免遍历顺序制造虚假中间状态。多个master、同revision矛盾、失败、空表、未知、未验证I均不成为有效历史；失败保留最近有效证据，呈现STALE/UNKNOWN/CONFLICT。
- 默认时效窗口为300秒，是本应用明确的初始策略，**不是自动识别的设备HA/心跳参数**；可用`NETCONSOLE_NEXT_WIRELESS_FRESHNESS_SECONDS`配置1..86400秒，响应返回实际窗口。达到deadline即STALE；未来时间fail closed；配置非法返回受控source-unavailable错误。
- 在线Current只能来自已验证在线事实。离线回放始终标记历史/STALE，且不能覆盖同来源已验证在线Current。所有测试fixture和GUI均明确OFFLINE_FIXTURE_EVIDENCE。
- 现有事实按完整Serial/MAC集合关联；禁止名称、IP或MAC后缀匹配。只读扫描总量默认4096条命令事实，给各可见来源预留配额；不足10个变化且达到上限、或缺少完整身份证据时显式PARTIAL_SCAN_LIMIT/PARTIAL_EVIDENCE。不声称完整历史。事实引用仍为fact:id，不伪造Raw manifest/hash。
- 服务读取支持取消；界面切换来源/资源时取消旧请求并清空旧历史，拒绝晚到响应覆盖当前选项。错误态不保留旧来源历史。

本批新增13个RuntimeTests方法；原14个无线方法保留并增加SQLite实际读链断言，包括命令失败、乱序导入、无变化采集、扫描限额与撤权。使用现有h3c_facts，不改schema/migration，不访问真实用户数据。

新增`r3_runtime_state_history.json`离线fixture（52个采样、双AC各12次有效变化）；输出每个来源Recent10=10，AP整体因只是来源角色同时切换，Recent10=0。浏览器真实打开1920/2560，11种状态通过，含历史错误和不完整历史；主/辅截图及局部详情已目视检查。截图位于Workspace/build/autonomous-20261105/gui。

定向验证：`dotnet run ... -- --wireless-tests` PASS（14+13方法）；vue-tsc、Vitest5/5、Vite、Service harness PASS；HTTP仍不启动第二Service。结果应与原57场景逐项合并，不能把本批方法数与场景覆盖数重复相加。
