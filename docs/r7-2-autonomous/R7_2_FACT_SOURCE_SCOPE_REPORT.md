# R7.2 事实来源与捕获范围隔离

2026-11-08；codex-A；branch `codex-A/netconsole-next-r3-dual-ac`。来源/范围分区实现和定向回归 **PASS**；真实 SW 同来源同范围更新 **SIMULATOR_PASS**。现场没有新增另一独立采集 authority，跨 authority 并存由隔离测试证明，不冒充现场互操作。

## 持久化语义

schema27 在事务中保留每条历史事实的 ID、source、payload 及捕获时间，迁移原九个可写字段。新生成列 `authority_key` 与 `scope_key` 从封存 source/payload 派生，不随当前库存变化，也不会由普通写入漂移。

`H3C_TASK_RAW:<run>` 的 run 是稳定采集来源 `H3C_TASK_RAW` 的一次 revision；其他 source 标签按完整原文精确区分。范围键是有序 JSON 数组：version、deviceId、profileId、siteId、sourceId、host、port。缺少对象类型 capturedScope 的旧记录保持 UNSCOPED，不能借当前库存补造历史 Site。

Current、revision 冲突、重放、晚到数据、完整 LLDP 表集合退役都按 `(profile, kind, identity, authority, capturedScope)` 隔离。同一分区仅允许一个 Current；完整表不会清除其他来源、Site、sourceId 或端点分区。迁移按分区恢复被旧全局 Current 逻辑压住的最新记录，同时以同分区最新 lldp.table 防止已退役邻居复活。重复初始化幂等。

无线地址/版本与历史回退的关联同时校验封存 scope，不能拿另一个范围的成功快照补齐当前缺失证据。LLDP relationKey 增加精确 authority、scope 和 Site，避免不同来源混成同一变化历史。

API 增加 `sourceAuthority`、`capturedScopeKey`；拓扑支持精确来源筛选，CSV 保留两字段。设备详情复用现有 LLDP claims API，按 inventory UUID、profile、捕获 Site 精确展示邻居接口、原始来源、authority、revision 和范围详情，提供拓扑入口；不推断上下游方向或实时可达。

## 实际迁移与产品证据

主集成服务受控更新后，隔离长期模拟器 catalog 实际完成 schema27 迁移。运行 `tests/r72_product_lldp_scope_probe.py`，run `54f54ae7ebca476eb13df38674a6b6b8`：两个命令 COMPLETE/PARSED，两个 Raw hash/bytes/profile/run 验证，3 条精确 LLDP 当前事实与关系均来源本次 run，authority 为 H3C_TASK_RAW，捕获范围固定到 `.3:22`、Site local。

证据：Workspace `reports\r72-product-lldp-scope-20261108-034026\summary.json`。采集前的同分区三条 Current 均保留原 ID 转入历史；新三条为 Current。`unrelatedPartitionCount=0`，现场无其他分区可供交叉干扰实证，不把“0 条未变”宣称为现场跨来源测试。

## 隔离回归

- Scope 10/10：相同 identity/source/time 在不同 Site/端点独立；同分区冲突原子回滚；scope 冻结；旧 v26 表迁移/隐藏来源恢复/UNSCOPED/LLDP 不复活/唯一 Current/幂等初始化。
- Publication 18/18：相同邻居 identity 的 offline A/B 与产品 Site A/B 并存；空表只退役自己的采集分区，重放不复活，另一 Site 更新只换自己的 Current，三个 relationKey 独立。
- LLDP/Radio 6/6；Wireless runtime 14/14 及组/身份/投影定向组 PASS。
- 来源筛选、CSV、Radio 页面 44/44 Web PASS；设备邻居与详情 18/18 PASS；typecheck PASS。

未向真实模拟器 catalog 注入合成事实；迁移测试只用独立最小数据库。未访问旧 NetConsole 真实数据根。进程重启持续性及最终真实浏览器验收由主集成报告补证。
