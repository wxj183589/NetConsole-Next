# R2 设备管理 UI Gap Matrix

| Gap | 基线 | R2 处理 | 状态 | 残余边界 |
| --- | --- | --- | --- | --- |
| Workbench 字号过小 | 普通 shell 和 inventory 多处 9–11px | 局部提升 shell、表格、筛选、状态与详情字号 | CLOSED | 终端专用暗色区保持原密度 |
| Site 作用域不可见 | `local` 仅作为隐式参数 | 显示当前 UI 作用域、siteId、分组计数和 scope note | UI CLOSED / API GAP | 后端没有 Site list API；设备 inventory/group/CRUD 尚未统一接入 Site claims，未伪造跨局点树 |
| 分组导航缺失 | 只有下拉框 | 真实 device groups 侧栏可点击切换 | CLOSED | 仍受服务端当前 site scope 限制 |
| 厂商/类型筛选缺失 | 只有 search/status/group | 增加服务端 `vendor`/`deviceType` query 与 UI 筛选 | CLOSED | 选项由已知 canonical 值和当前页真实值组成 |
| 请求竞态 | refresh 可被旧响应覆盖 | list/group/profile 分别使用 sequence guard | CLOSED | 未引入 AbortController，服务端仍可完成旧请求但不会落 UI |
| 创建表单安全语义弱 | 终端档案标签不清晰，类型可自由输入 | credential reference 说明；类型 select；FIT-AP 创建拦截 | CLOSED | 真实 AC/FIT-AP domain UI 不在 R2 |
| 详情层级平面 | 基础信息、会话和审计连续堆叠 | 稳定身份、作用域、安全、能力和历史分段 | CLOSED | 接口/LLDP 仅显示未接入，不伪造字段 |
| 125% 证据缺失 | 既有脚本未覆盖 | 新增独立 R2 visual fixture script并完成本机 DPI 只读核验 | NATIVE PENDING / SIMULATED PASS | 当前有效显示器和 Chrome 窗口为 100%，不能把 CSS 模拟升级为原生 125% |
| 服务型 Browser CRUD 未执行 | 只有 mock visual fixture | 使用隔离 Service、SQLite、Cookie/CSRF 的现有脚本完成 CRUD E2E | CLOSED | 测试数据库已退役，脱敏截图保留在 Workspace build 证据目录 |
| 多 Site claims 未覆盖设备库存 | P2B5 仅保护网络规划等链路 | 完成路由审计并记录独立 API 合同缺口 | OPEN / CONTRACT GAP | 本轮不修改身份模型、schema 或设备路由授权架构 |

