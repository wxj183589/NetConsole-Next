# NetConsole Next 设备数据诊断

## 结论摘要

用户提供的整改前页面显示 `782` 台登记设备，但在本次隔离预览数据根 `D:\NetConsoleNext-UIData` 中，SQLite `h3c_device_profiles` 实际记录数为 `0`。此前隔离预览数据根 `D:\NetConsoleNext-PreviewData` 也没有可复现的 782 条设备记录。因此本次不能把 `782` 解释成当前预览数据库的真实数量，也没有删除或覆盖任何 782 条记录。该数字只能作为整改前截图证据保留，必须用实际 Catalog 查询结果替代展示。

当前新接口返回的空库证据为：

```json
{
  "page": 1,
  "pageSize": 20,
  "total": 0,
  "hasMore": false,
  "summary": {
    "totalRecords": 0,
    "validDevices": 0,
    "invalidRecords": 0,
    "enabledDevices": 0,
    "disabledDevices": 0,
    "onlineDevices": 0,
    "offlineDevices": 0,
    "unknownStatus": 0
  },
  "items": []
}
```

## 数据来源与 API

设备登记的权威表是 `h3c_device_profiles`，连接状态只从 `terminal_sessions` 按 `target_id` 的最近会话读取。站点名称通过 `collection_sources.site_id -> sites.id` 关联。现有 Profile 表并不包含可靠的厂商、型号或显示名称字段，因此 DTO 不再虚构这些值，而是使用 `—`，设备名称在缺失时使用 `未命名设备` 并附加质量问题。

新增接口：

```text
GET /api/system/devices?page=1&pageSize=20&search=&status=
```

返回 `DeviceInventoryPage`：

- `summary.totalRecords`：当前查询条件下的 Profile 记录数。
- `validDevices`：`id`、`host`、`terminal_protocol` 均非空的记录。
- `invalidRecords`：至少一个上述核心字段为空的记录。
- `enabledDevices` / `disabledDevices`：来自 `h3c_device_profiles.enabled`。
- `onlineDevices`：最近终端会话为 `CONNECTED`。
- `offlineDevices`：最近终端会话为 `DISCONNECTED`、`FAILED` 或 `CLOSED`。
- `unknownStatus`：启用设备没有可判定的最近会话状态。

禁用是配置状态，不能推导为在线或离线；设备登记也不能推导为在线。

## 原因与修复位置

整改前设备页复用了终端设备投影，页面期望的设备名称、管理地址、厂商/型号和终端能力并不都由该投影保证，导致缺失属性直接进入模板，出现 `undefined`、空名称，以及通过地址片段拼接出的孤立 `:`。另外，终端设备 API 受 `TerminalEnabled` 门控，不适合作为始终可用的登记设备列表来源。

本次修复：

1. `Catalog.QueryDeviceInventory` 以真实 `h3c_device_profiles` 为登记源，按真实会话状态做最近状态投影和分页。
2. `Program.cs` 无论终端采集是否启用，都提供认证后的 `/api/system/devices` 查询入口。
3. `web/src/api/client.ts` 增加与 JSON 字段一致的 `DeviceInventoryPage` 类型和查询方法。
4. `web/src/deviceViewModel.ts` 统一处理 `undefined`、`null`、对象值、空字符串和孤立地址分隔符；显示未知值为 `—`，不把对象转成 `[object Object]`。
5. `DevicesView.vue` 只渲染当前分页，不一次性渲染 782 行，并展示数据质量提示和真实空态。

没有改写 Schema、删除设备、修改 Raw/Catalog 真实数据或伪造在线状态。

## 自动化证据

- `tests/NetConsole.Next.Service.Tests/Program.cs` 新增库存映射契约：完整记录、缺失 ID/地址/协议、启用/禁用、在线/离线/未知、搜索和分页统计。
- `web/src/deviceViewModel.test.ts` 覆盖 `undefined`、`null`、对象值、孤立地址和有效字段映射。
- 空隔离库真实浏览器截图显示 `0` 登记设备，页面显示真实空状态，而不是补造设备。

测试 fixture 只写入系统临时目录并在测试结束清理，不复制生产或开发真实数据。
