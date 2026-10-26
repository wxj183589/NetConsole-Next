# P2B5 Export Boundary and Acceptance Plan

日期：2026-10-27  
Branch：`codex-A/next-device-management-export-boundary-20261027`  
基线：H3C V5/V7 mock boundary `5b1d814`  
状态：Draft；仅冻结导出边界，未接入运行时导出服务

## 本阶段结论

当前没有足够可信的授权用户、站点范围或生产数据源来实现可发布的导出功能。因此本阶段只固化导出契约和验收门槛，不新增 API、UI、数据库写入、真实数据读取或 WPS/云端同步。

## 允许的导出输入

- 只允许来自固定的 network-planning revision/snapshot；请求必须携带 revision、scope、format 和 operation key。
- 快照必须同时记录 source、source revision、capturedAt、schema version、filter、actor scope 和 content hash。
- 计划地址、Network Domain、VLAN、observed facts、diff、review/conflict 和 operation 状态必须区分来源与状态；不得把 observed 数据静默写回 planned 数据。
- 导出失败、快照过期、scope 不可验证、内容 hash 不一致或数据不完整时，返回结构化拒绝，不生成“看起来成功”的文件。

## 格式边界

- JSON/CSV/HTML/TXT 只能从同一固定快照生成，并在 manifest 中声明格式、编码、字段版本、行数、hash 和生成状态。
- CSV 必须使用严格列集合、受限字段长度和公式注入防护；HTML 必须转义文本并禁止任意脚本、外链和路径穿越。
- 导出操作需要可查询的 operation history；预览、确认、取消、失败和重试状态不得依赖浏览器内存。
- 本阶段不承诺 Excel/WPS 云 API、KDocs、在线文档或自动上传；本地 `.xlsx`/CSV 适配若未来需要，必须另行定义格式契约和隔离测试根。

## 安全与数据边界

- ADMIN 在没有可信 site claim 时继续 denied；SUPERADMIN 也只能在明确授权的 isolated scope 内导出。
- 不读取 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`，不复制完整 Site/数据库，不连接现场设备。
- 不把凭据、Host Key、原始命令、认证 token 或未脱敏 error detail 写入导出文件。
- 导出只读，不触发 migration、设备采集、配置写入或 management_ip 更新。

## 后续实现门槛

实现导出服务前必须先具备：可信用户/站点 claims、固定 revision API、字段版本兼容策略、脱敏规则、HTML/CSV 安全测试、operation/audit 持久化和真实浏览器验收。未满足时功能状态保持 `EXPORT_PENDING`，不得添加无效按钮或 fake payload。

## 验收声明

本 Draft 的完成标准是边界可审查且不产生副作用。它不代表导出服务已实现，也不代表 WPS、真实站点、生产数据或现场设备验收通过。
