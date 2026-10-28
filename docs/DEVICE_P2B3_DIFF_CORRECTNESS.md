# P2B-3 Diff 正确性与分页边界

## 无序集合

planned/observed 事实按 Domain 与 Interface 分组。比较值集合使用 ordinal HashSet.SetEquals，不会受数据库返回顺序影响；重复同值事实不会制造虚假 mismatch。

比较值包含：

- normalized value；
- lifecycle；
- valid-from / valid-to；
- Source Kind、Source Reference、revision、domain、interface 和 conflict state 作为返回事实保留。

Source Kind 不被排序或去重掩盖：OBSERVED 与其他来源仍分开返回，REVIEW_REQUIRED、未解析 Domain 和跨 Domain/Interface 仍产生独立状态。

## 截断安全

Diff 查询在数据库端按 network_domain_id 与 interface_reference 计算每组 COUNT(*) 和 ROW_NUMBER()，每组最多返回 100 条事实，但保留完整组计数。

如果组事实超过上限，返回：

status=INCOMPLETE
truncated=true

不再对该组生成确定性的 MATCH、MISMATCH、PLANNED_ONLY 或 OBSERVED_ONLY。因此 observed/planned 位于截断边界之后时不会产生错误业务结论。

## Cursor

地址、Membership、Domain、VLAN page Cursor 绑定 endpoint 和全部过滤条件。Cursor 只能在相同过滤条件下继续使用；改变 site/domain/source/lifecycle/as-of 等过滤条件会受控返回 INVALID_QUERY。排序键仍为稳定的 id ASC。

## 自动化覆盖

- DIFF_SET_ORDER_INDEPENDENT
- DIFF_DUPLICATE_SOURCE_FACTS
- DIFF_PLANNED_OBSERVED_MATCH
- DIFF_PLANNED_OBSERVED_MISMATCH
- DIFF_SAME_IP_DIFFERENT_DOMAIN
- DIFF_SAME_DOMAIN_DIFFERENT_INTERFACE
- DIFF_STALE_MANUAL_REVIEW
- DIFF_UNRESOLVED_DOMAIN
- DIFF_100_FACTS
- DIFF_101_FACTS
- DIFF_1000_FACTS
- DIFF_OBSERVED_AFTER_LIMIT
- DIFF_PLANNED_AFTER_LIMIT
- DIFF_GROUP_CROSSES_PAGE_BOUNDARY
- DIFF_INCOMPLETE_NOT_MATCH
- DIFF_INCOMPLETE_NOT_PLANNED_ONLY
- DIFF_STABLE_PAGINATION
- CURSOR_FILTER_BINDING
