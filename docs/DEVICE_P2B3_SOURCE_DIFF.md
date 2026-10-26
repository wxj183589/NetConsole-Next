# P2B-3 Source Provenance 与 Network Diff

## Provenance

所有地址、membership、domain、VLAN plan 和 diff fact 都保留 source kind、source reference 和 revision。API 不把 observed 覆盖 planned，也不把 imported/manual 转换成 observed。

地址 effective projection 以同一设备、domain、family、normalized address、prefix、role、interface 的事实组为边界：

- unresolved domain 或 \`REVIEW_REQUIRED\`/其他冲突：\`effectiveState=CONFLICT\` 或 \`UNRESOLVED\`，effective source 为 null；
- 多个事实值相同：状态可为 \`CONSISTENT\`，但不选择某个 source；
- 只有一个无冲突、已解析事实：返回其 effective source；
- 不同事实值：不返回 effective source。

## Diff state

地址 diff 的 planned 侧包含 \`PLANNED\`、\`IMPORTED\`、\`MANUAL\`，observed 侧只包含 \`OBSERVED\`。VLAN assignment 使用同一 source partition。分组内状态如下：

| 状态 | 语义 |
| --- | --- |
| \`MATCH\` | planned 与 observed 有相同值 |
| \`MISMATCH\` | 两侧都存在但值不同 |
| \`PLANNED_ONLY\` | 只有计划/导入/手工事实；不代表设备已应用 |
| \`OBSERVED_ONLY\` | 只有观测事实；不自动创建计划 |
| \`CONFLICT\` | source conflict、claim conflict 或 stale/review-required |
| \`UNRESOLVED\` | domain scope 尚未解析 |

VLAN 计划与 observed 计划可并存；\`vlan-plans\` 对 planned 行报告 \`VLAN_PLANNED_OBSERVED_COEXISTENCE\`，而 observed 行保持自身 \`NONE\`，避免把共存规则误报为两条数据都非法。

Schema v13 当前没有持久化 \`network_reviews\` 表，因此不能声称已执行人工 review。\`REVIEW_REQUIRED\` 行只按 conflict-safe 方式暴露，network diff 的 stale-review E2E 证明它不会产生 effective source；P2B4 才负责 review write model。
