# Phase 9 parser 字段对照

| 字段 | 原 NetConsole 语义 | Next 离线字段 | 结果 |
| --- | --- | --- | --- |
| 接口名 | 厂商名归一化后保存 | H3C/ZTE 原始名 + parser 稳定 token | 已覆盖；跨领域 DTO 尚未统一 |
| 链路状态 | UP/DOWN/ADM 分离 | H3C Link；ZTE Admin/Physical/Protocol | 已覆盖 |
| 光模块 | no module/no light/abnormal/normal | H3C nullable DOM；ZTE 明确状态 | 已覆盖；Next 无持久化 |
| RX/TX | 数值和单位，缺失不补零 | `decimal?` dBm 语义 | 已覆盖 |
| LLDP 本端/远端口 | 端口与邻居身份同时保留 | H3C/ZTE local/neighbor fields | 已覆盖 |
| AP 身份 | MAC/稳定身份，不模糊匹配 | H3C AP name/id/MAC fixture | parser 覆盖，关联缺失 |
| 失败状态 | 空/部分/不支持/权限可区分 | transcript + parser status | 已覆盖 |
| 当前/历史 | 当前失败不能用历史冒充 | Next H3C Raw/task status 基础存在 | 领域事实历史缺失 |

Next 的 parser 结果仍是内部 C# record；只有 H3C 基础任务进入 Raw/SQLite manifest。ZTE 和 AC/轨旁/FIT-AP 结果尚未贯通到 Next schema、HTTP API 和 GUI，因此不能声明“采集到 GUI 字段一致”。
