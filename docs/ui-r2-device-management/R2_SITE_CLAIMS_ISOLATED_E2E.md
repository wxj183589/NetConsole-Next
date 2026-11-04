# R2 Site Claims Isolated E2E

## 运行边界

- 脚本：`tests/r2_site_claims_e2e.py`
- Service：Release `NetConsole.Next.Service.dll`，仅回环地址。
- 数据：一次性系统临时目录；先初始化，再执行既有隔离 P2B2/P2B5 migration，测试结束精确删除。
- Fixture：Site A/B/C、设备 A/B/C、Group A/B；测试账号 USER_A、USER_B、USER_AB、USER_NONE 复用隔离 admin 的 PBKDF2 fixture hash，不输出凭据；SUPERADMIN 使用既有隔离默认账号。
- 未访问 `D:\NetConsoleData`、`D:\NetConsoleData-dev` 或 SmartHub，未连接真实设备。

## 实际结果

`R2_SITE_CLAIMS_TESTS=31/31 PASS`

覆盖了授权 Site 聚合、单 Site 列表、跨 Site 参数、搜索/计数过滤、详情 404、分组、无 claims fail-closed、角色写入拒绝、SUPERADMIN 全局、CSRF、创建/编辑/审计、稳定 ID、凭据不暴露、FIT-AP、CSV preview/commit 和 batch preview/confirm。

既有 `tests/device_management_browser_e2e.mjs` 也重新执行通过：

`PASS: browser CRUD, stable IP edit, detail/audit, group assignment, CSV preview/commit/conflict and ADMIN write denial`
