# Device Management P2B5 — Network Planning UI

日期：2026-10-27  
Branch：`codex-A/next-device-management-p2b5-ui-20261027`  
基线：PR #27 head `7c32804050d816836404acee93a129056bbfc64f`  
状态：Draft，未合并，等待 CI 与人工审查

## 范围

P2B5 将 P2B3 只读 API、P2B4 v14 隔离写入能力接入现有 Vue 工作台，新增 `/network-planning` 路由。页面不生成前端示例数据，所有列表、差异、审核、历史和操作状态均来自服务端 API。

覆盖：

- 多 IP 地址事实、成员关系和来源/Revision 展示；Observed 行不可覆盖。
- Network Domain、显式 Site/Station/Line/Section scope 和解析状态。
- VLAN 规划、半开有效期、Tagging/PVID/QinQ 表单和 observed 共存展示。
- 规划 vs 实测差异，保留 planned/observed/source identity。
- 人工审核、地址/VLAN 历史、操作历史。
- CSV Preview、Dry-Run、Confirm、Cancel 和幂等键。
- sessionStorage 恢复 tab、设备、搜索、Domain、来源和 scope 筛选。
- P2B3/P2B4 gate 不可用时显示明确只读/不可用边界，不显示写入按钮。

## 服务边界

P2B3 读取校验现在接受 Schema v13 或包含完整 v13 读取表的隔离 Schema v14；v13 迁移自身仍严格要求从 v12 到 v13。P2B4 写入、历史、审核和操作查询仍要求隔离临时数据根、v14 authority 和对应环境 gate。

浏览器验收使用临时目录创建 v12 → v13 → v14 fixture，不读取或写入 `D:\NetConsoleData`、`D:\NetConsoleData-dev`，不连接真实设备。

## 视觉与响应式

复用 `NcLayout`、现有主题变量、Element Plus 控件和主题切换；新增 planning 样式覆盖浅色/深色、1920×1080、1680×943、1440×900、1366×768。截图和 JSON 证据由浏览器脚本生成到本地可重建的 artifact 目录，不作为业务数据提交。

## 后续约束

- P2B5 Draft PR 不得直接合并到 main。
- Admin 无可信 Site Claims 时只能只读；Site Scope/Admin auth 需要后续安全阶段单独设计和审查。
- 不在本阶段引入真实设备、凭据、IP/VLAN 或生产/开发真实数据库。
