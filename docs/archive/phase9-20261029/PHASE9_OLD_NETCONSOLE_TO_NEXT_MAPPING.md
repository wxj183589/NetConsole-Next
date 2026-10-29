# Phase 9：原 NetConsole 到 Next 能力映射

## 证据范围

原项目只读参考为 `src/netconsole/adapters/h3c/`、`src/netconsole/parsers/h3c/`、`src/netconsole/parsers/zte/`、`src/netconsole/services/netmiko_connection.py`、`device_detail_query_service.py`、`online_mr_collector.py`、`ac_management_router.py`、`resources/device_command_profiles.json` 和命令语义矩阵。Next 证据为本分支 `35631e6` 及其父提交、C# harness、`tests/NetConsole.Next.Service.Tests/fixtures/`。

| 能力链 | 原 NetConsole | Next 当前状态 | 结论 |
| --- | --- | --- | --- |
| H3C SSH 连接、Host Key、凭据、取消、释放 | Netmiko/旧连接封装 | SSH.NET、SHA-256 pin、DPAPI 引用、取消/超时/释放 | Next 已实现；真实 V7/V9 互操作仍缺证据 |
| H3C 命令 profile | `device_command_profiles.json`，设备详情/轨旁/AC 分域 | H3C 只读 allowlist，Phase 9 parser policy | 部分实现；Next 尚未迁移完整 profile 选择器 |
| H3C 版本/接口 | `H3CParser`、interface parser | `display version`、`display interface brief` | 已实现并有 V5/V7/V9 fixture；采集任务仍为窄范围 |
| H3C AC/FIT-AP、Radio/WLAN | AC parser/collector | AP、地址、Radio 离线 parser 已实现 | 部分实现；尚无在线 AC collector、持久化和 GUI 链路 |
| H3C LLDP/Optical | 独立 parser、事实库和 AC/轨旁适配 | LLDP list/verbose、transceiver diagnosis 离线 parser | 部分实现；无对应 Next inventory schema/API/GUI |
| ZTE 版本/接口/光模块/LLDP | ZXR10 parser、C89E 现场链 | C# ZXR10 parser 和五条只读命令 policy | 离线实现；未接入 Next SSH/runtime/API/GUI |
| Raw 输出和任务状态 | 原项目 raw log/解析状态 | H3C Raw NDJSON、capture/parse/task 三态 | H3C 已实现；ZTE 尚未进入 Next Raw sink |
| 数据库/API/GUI | 旧 Repository、FastAPI、Vue | Next Catalog/HTTP/GUI 目前仅 H3C 基础任务和通用设备页 | 部分实现；厂商字段跨层一致性未完成 |

## 必须保持的领域边界

`device.inventory.collect` 负责设备版本、接口、光模块和通用 LLDP；`trackside.*` 负责站点交换机/AP 拓扑和光功率；`wireless_controller.*` 负责 AC、FIT-AP、Radio 和 AP 侧关系。Next 的 Phase 9 parser 可共享稳定字段，但不能直接把三类任务合成一个 DTO、任务或持久化表。

## 旧实现可直接复用的业务规则

- 接口 `DOWN` 与光模块 `NO_MODULE/NO_LIGHT/ABNORMAL` 分开表达。
- 缺失 RX/TX 保持 `null`，不得写成 0；光功率保留 dBm 语义。
- 空表、条数下降、解析失败不能覆盖此前有效当前值；部分成功必须带结构化 warning。
- LLDP 同时保留本地端口、远端端口、邻居身份和管理地址。
- AP/AC 关联只使用稳定身份；歧义时保留未匹配或冲突。
- ZTE C89E-4 现场结论不得外推到全部 ZXR10 型号。

## 必须重新实现的部分

Next 仍需要独立的厂商 runtime session、领域 collector、schema/repository、HTTP DTO 和 GUI projection；不能复制旧 SQLite 结构，也不能把真实旧目录作为 Next 数据根。
