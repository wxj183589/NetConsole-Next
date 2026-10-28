# NetConsole Next 设备管理 Phase 1

状态：`DEVICE_PHASE1_VERIFIED`（本地服务测试、前端构建、隔离真实浏览器验收和 GitHub Ubuntu/Windows/PowerShell CI 均通过）。

## 范围

Phase 1 建立服务端真实设备库存，覆盖设备新增、编辑、启用/禁用、搜索、状态筛选、局点/分组、详情、审计和 CSV 导入预览/原子提交。设备库存与现有 `h3c_device_profiles` 终端连接档案分离；通过 `terminal_profile_id` 可选关联，不修改既有 terminal session 的 `target_id`。

保留边界：Raw/Catalog、G1/G2.3/G2.4、Web Auth/CSRF、PR2 ZIP、Desktop Helper 和终端只读策略不回退。Phase 1 不创建真实设备连接，不写生产或开发真实数据。

## API

- `GET /api/system/devices`：服务端分页、搜索、状态、site/group 筛选。
- `GET /api/system/devices/{id}`：设备字段、终端档案摘要、最近会话、设备审计。
- `POST/PUT /api/system/devices`：新增/编辑，写操作仅 `SUPERADMIN`。
- `GET/POST /api/system/device-groups`：读取和创建分组。
- `POST /api/system/devices/import/preview`：仅解析与匹配，不写库。
- `POST /api/system/devices/import/commit`：需要 preview token 和显式确认；单 SQLite transaction 全批提交，异常自动回滚。

Cookie 会话写入仍由全局 Origin + CSRF 校验保护；LocalManager bearer 仍使用既有本地超级管理员边界。审计只记录 actor、设备标识和非敏感字段，凭据引用、密码、私钥不进入 CSV 或审计详情。

## 兼容策略

schema v11 新增 `device_inventory`、`device_groups`。初始化会为既有 H3C profile 建立按站点/IP 去重的库存桥接记录；旧 H3C profile API 仍可写入桥接记录，但不会制造同站点/IP 的重复库存。GET 查询不隐式写入桥接。编辑库存管理 IP 不改动终端档案 host，详情会提示 `TERMINAL_PROFILE_ADDRESS_DIFFERS`，终端历史继续按原 profile ID 读取。

库存 `enabled` 只表示资料层状态，不会隐式修改 `h3c_device_profiles.enabled` 或 `collection_sources.enabled`；终端连接、采集源和任务运行状态由各自边界控制。

## 非目标

- 不提供密码/凭据/私钥导入。
- 不把“最近会话 CONNECTED”伪装成实时探测。
- 不删除设备，不清理任何真实数据。
- 不实现真实 SSH/Telnet/Serial 设备连接验收；终端能力沿用已有只读链路。
