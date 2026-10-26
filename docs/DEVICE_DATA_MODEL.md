# 设备管理 Phase 1 数据模型

## 设备库存

`device_inventory` 是设备管理的唯一业务库存表：

| 字段 | 语义 | 约束 |
|---|---|---|
| `id` | 稳定 `deviceId` | 主键；不由 IP 派生 |
| `name` | 展示名称 | 非空，最长 120 |
| `management_ip` | 管理 IP | 新写入必须是可解析 IP；同一 site 不重复 |
| `vendor` / `model` | 厂商和型号 | 展示元数据，不参与身份模糊匹配 |
| `device_type` | 设备类型 | token；默认 `NETWORK_DEVICE` |
| `site_id` | 局点 | 外键 `sites.id` |
| `group_id` | 可选分组 | 外键 `device_groups.id`，必须属于同一 site |
| `description` | 描述 | 非敏感说明 |
| `enabled` | 库存启用状态 | 0/1；关联终端档案时同步其可用状态 |
| `created_ms` / `updated_ms` | 生命周期时间 | 服务端 UTC epoch ms |
| `terminal_profile_id` | 可选终端连接档案 | 唯一外键 `h3c_device_profiles.id` |

API 保留历史 UI 字段 `id`、`managementAddress` 等，同时补充 `deviceId`、`managementIp`、`siteId`、`groupId`、`description`、`createdAt`、`updatedAt` 和 `terminalProfileId`。

## 终端档案

`h3c_device_profiles` 继续保存 host、port、username、credential reference、Host Key 策略、终端编码/协议等连接边界。它不是设备库存，不承载厂商、型号、分组等业务元数据。`terminal_sessions.target_id` 仍引用 profile ID，因此库存 IP 编辑不会破坏会话历史。

## 状态口径

设备“在线/离线/未知”来自关联 profile 的最近持久化 `terminal_sessions` 状态：`CONNECTED`、`DISCONNECTED/FAILED/CLOSED` 或无可用会话；它不是实时网络探测。库存 `enabled=0` 优先显示 `DISABLED`。

## 审计

`audit_events` 记录 `DEVICE_CREATE`、`DEVICE_UPDATE`、`DEVICE_DISABLE`、`DEVICE_IMPORT_CREATE/UPDATE/COMMIT` 和分组创建事件。detail JSON 不包含密码、凭据内容、私钥或 credential reference。
