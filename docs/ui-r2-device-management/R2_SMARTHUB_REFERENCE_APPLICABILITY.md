# SmartHub 参考适用性

## 参考结论

SmartHub handoff 已通过研究归档和 Next UI reference handoff，但 SmartHub 自身 full visual 仍为 PARTIAL，不能作为 Next 的完成或发布证据。R2 只采用 clean-room 的交互和信息架构观察：

- Workbench 的侧栏、header、tab 和 page header 层级；
- 搜索、筛选、分页、表格密度与操作区；
- Site/group scope 导航的可见性；
- 创建/编辑表单、详情分组、对话框、空/加载/错误/禁用状态；
- 终端、任务、审计状态的边界表达方式。

## 明确禁止

- 不复制 H3C logo、sprite/glyph、AI/tray 资产或未知字体；
- 不复制 SmartHub 业务实现、数据库、API、Java/MariaDB SQL 或第三方 bundle；
- 不把 SmartHub 的真实设备、真实截图或原始业务数据写入 Next；
- 不因 SmartHub 的视觉 pairing 结果宣称 Next 已完成真实设备或 Windows GUI 验收。

## 本轮落地映射

| 参考观察 | Next R2 落地 | 证据 |
| --- | --- | --- |
| 密度可读性 | 普通工作台/设备表格字号、行高和按钮字号局部提升；终端暗色布局不改 | `web/src/styles/theme.css` |
| 层级 scope | 新增当前授权 `siteId` 与真实 device groups 作用域栏 | `web/src/views/DevicesView.vue` |
| 筛选工作台 | 厂商、类型与已有状态/分组筛选均通过服务端 query | `client.ts`、`Program.cs`、`DeviceManagement.cs` |
| 安全表单 | 显示 credential reference 语义，不新增密码输入；FIT-AP 无独立创建入口 | `DevicesView.vue` |
| 详情分组 | 稳定身份、作用域、安全、能力边界、终端历史、审计分组 | `DevicesView.vue` |

