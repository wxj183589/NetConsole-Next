# R2 设备详情验收

## 已实现

详情弹窗按真实可用字段分成：

1. 设备名称、稳定设备 ID、当前状态、Site/group scope；
2. 管理 IP、厂商/型号、类型、credential reference、terminal profile ID、创建/更新时间；
3. 终端连接、接口/LLDP、任务/事实三个能力卡，其中未接入 API 的能力明确显示“未接入/独立模块”，不填充假数据；
4. 最近终端会话；
5. 服务端审计记录。

详情仍通过已有 `/api/system/devices/{id}` 读取，未改变身份算法、历史关联或数据模型。

## 视觉证据

- `D:\study\NetConsole-Next-Workspace\build\ui-r2-device-management\final\device-detail-dialog-1920x1080.png`

## 未覆盖

接口、LLDP、AC/FIT-AP、无线事实和任务详情没有在本轮伪造为设备详情字段；这些能力按 Next roadmap 保持独立模块边界。

