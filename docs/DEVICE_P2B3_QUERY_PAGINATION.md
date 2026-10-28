# P2B-3 查询、分页与边界

## List API

地址、membership、domain 和 VLAN plan API 统一使用：

- 默认 \`limit=20\`；
- 最大 \`limit=100\`，小于 1 或超过上限返回 400 \`INVALID_QUERY\`；
- base64url cursor，cursor 只携带上一条稳定 ID；
- \`ORDER BY id ASC\`，并使用 \`id > $cursor\`；
- 独立 \`COUNT(*)\` 得到 controlled total；
- SQL \`LIMIT=limit+1\` 判断 \`hasMore\`，不会把整个结果集加载到内存。

过滤器均为参数绑定；枚举、VLAN ID、时间戳和 cursor 先做边界校验。 \`search\` 仅作为绑定的 \`LIKE\` 参数，不拼接 SQL。

## Diff API

\`network-diff\` 是单设备 bounded diagnostic read：地址和 VLAN assignment 查询最多读取 101 条事实，返回最多 100 条分组事实并设置 \`truncated=true\`。它按 domain/interface 形成稳定分组，输出 \`MATCH\`、\`MISMATCH\`、\`PLANNED_ONLY\`、\`OBSERVED_ONLY\`、\`CONFLICT\` 或 \`UNRESOLVED\`。

\`asOf\` 使用非负 Unix milliseconds，validity 使用 \`[valid_from_ms, valid_to_ms)\` 语义。未提供 \`asOf\` 时只应用 source/lifecycle/domain 过滤，不伪造当前时间快照。

## 运行时保护

每个请求打开独立 read-only connection，schema availability 失败统一返回 503 \`P2B_SCHEMA_UNAVAILABLE\`。GET 不创建表、不写 schema version、不写 Catalog 设备/VLAN/规划数据；既有 Web Auth 的 session \`last_seen\` 行为属于认证基础设施，不是 P2B 数据写入。
