# R7.1 Syslog H3C 事件标识分类与筛选

2026-11-08；原版源码只读对照，未启动原版，未读取旧真实数据根，未连接设备。

## 原版支持证据

- `D:/study/NetConsole-Workspace/NetConsole/src/netconsole/services/ground_unattended/syslog_runtime.py:34-40` 定义 WMESH、IFNET、CFGMAN 消息标识；`WmeshRealtimeParser.parse`（659-672）给出相应事件族和类型。
- `D:/study/NetConsole-Workspace/NetConsole/apps/desktop_renderer/src/views/rail-transit/SyslogPage.vue:34` 提供五类事件筛选。
- 原版 `tests/test_ground_unattended_syslog_field_validation.py:1026-1027` 中的 WMESH LINKUP/LINKDOWN 示例复制为合成测试输入。它们是原版测试样本，不是本轮设备采集证据。

## 实际交付

`H3cSyslogEventClassifier` 仅识别完整消息标识：`IFNET/<数字>/PHY_UPDOWN:`、`CFGMAN/<数字>/CFGMAN_CFGCHANGED:`、`WMESH/<数字>/MESH_LINKUP:`、`MESH_LINKDOWN:`、`MESH_ACTIVELINK_SWITCH:`，兼容 `%%10` 等 Comware 版本前缀与大小写。事件族/类型来自该标识，不从消息关键词、发送者、AP 名称或站点推断。标识前有字母数字/下划线、类型后有额外后缀、族与类型错配或缺少标识均 UNKNOWN。

单条消息出现多个完整标识时保守保留 UNKNOWN，包括已知+已知、已知+未知、同标识重复；正则有 25ms 超时，超时也保留 UNKNOWN。该分类不代表设备状态、链路恢复或业务恢复。

现有 `POST /api/system/syslog/query` 的请求增加可选 `eventType`（未提供或 null 继续查全部），只接受五个确切事件值或 `UNKNOWN`。返回增加 `eventFamily`/`eventType`；原始 `message`、Raw 路径/行号不修改。HTTP 现有模型绑定自动使用新增可选字段，无新增旁路接口。

分类在现有封存/路径/reparse/长度/SHA256校验后执行，复用 Site 授权、31天窗口、8分段、100000记录、500返回上限和 partial 状态。没有修改 writer、UDP worker、授权逻辑、Program 或 shared API client。

`SyslogWorkspace` 增加事件类型筛选、事件族/类型列与 CSV 列，未知保留 UNKNOWN，兼容旧响应缺少分类字段。消息继续以 Vue 文本方式呈现；身份变化与停用继续清空证据并阻断迟到响应。

## 已执行验证

- Service `--syslog-query-tests`：原有 SyslogQuery **8/8 PASS**（含真实 loopback UDP 在持续监听时定时封存），新增 SyslogEvents **6/6 PASS**，EXIT 0。
- 新增分类覆盖12种标识样本，包括原版 WMESH 示例、五类事件、关键词误匹配、前后缀、错配族、多个/重复标识；在隔离 SQLite + 实际 RawSegmentWriter 封存文件上验证可选/确切/UNKNOWN筛选、消息字节解码结果/Raw引用/行号保留，以及 severity、容量、授权和非法筛选约束。
- Vue SyslogWorkspace **6/6 PASS**，原有4项语义保留；新增确切过滤值、服务端分类显示、原始消息文本转义、UNKNOWN和身份切换清除证据验证。
- `npm run typecheck` PASS；`git diff --check` PASS。
- 测试数据为 Workspace 内 GUID 隔离根，测试结束已删除；构建输出在 Workspace build 内。未访问或写入旧真实数据根。

本批未单独执行运行服务 HTTP/真实浏览器新字段验收，也未执行真实 H3C 设备发送 Syslog、持续负载、告警关联或业务恢复验证；这些不由以上单元/本地 UDP 合成验证替代。原版完整事件 payload/身份关联等功能未因此全部迁移。
