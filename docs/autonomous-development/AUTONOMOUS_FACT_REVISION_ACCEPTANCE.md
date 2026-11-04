# 事实修订与采集归属验收（Checkpoint I）

日期：2026-11-05；A24/A25实现，八项最小SQLite测试及完整Service harness通过。无schema修改、无真实数据写入。

迟到修订按captured_ms保存为历史，不再回退Current。完全相同的同源修订不执行facts INSERT/UPDATE；不同来源的相同时间、相同内容保留各自来源而不更换Current。同一主体/时间出现不同内容或状态时返回409 H3C_FACT_REVISION_CONFLICT，整批回滚，包括冲突前已处理的新事实。历史修订同样不可覆盖。返回的Current/历史数量使用真实聚合，不再受1000条读取上限影响。

新事实在现有payload_json内记录服务端推导的capturedScope（稳定device/profile/source ID、Site、host、port及版本）；用户不能从请求指定此归属。读取先将当前授权与采集时归属、当前设备/源绑定取交集，再分页。设备迁站、profile重绑、host/port变更不会把旧事实归给新的当前目标。无线Current、地址关联、版本上下文及runtime历史各自执行同样的证据门禁。

无采集归属的旧事实继续可供全局管理员通过通用facts接口诊断；Site读取及无线投影不按现有库存追填归属。重复旧修订也不能自动补写capturedScope，须用新的明确采集时间。此收敛会使旧证据对应的无线列表暂时为空，重新采集/明确的新离线回放后恢复；没有修改旧数据，没有恢复Legacy HistoryStore。

测试覆盖Current单调与重启、零写入幂等、整批冲突回滚、跨source同时间矛盾、授权过滤先于limit、迁站后的历史与物理AP隔离、端点/设备重绑、旧数据全局诊断边界。原Wireless 14方法及Runtime 13方法仍通过；HTTP错误映射待Service维护窗口实测。
