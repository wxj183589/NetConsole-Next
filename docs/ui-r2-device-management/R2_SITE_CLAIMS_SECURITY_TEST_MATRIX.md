# R2 Site Claims Security Test Matrix

测试均使用一次性隔离 SQLite、回环 .NET Service、RFC 5737 地址和虚构账号；不读取真实数据、不连接设备、不扫描网络。

| 测试身份/场景 | 期望 | 结果 |
| --- | --- | --- |
| USER_A / Site A 列表、Site 列表、分组 | 仅 A | PASS |
| USER_A / B 查询参数、B 详情、B 搜索 | 403 或 404，无存在性泄露 | PASS |
| USER_B / Site B 列表 | 仅 B | PASS |
| USER_AB / 聚合列表和分组 | A+B，不含 C；summary/分页同样过滤 | PASS |
| USER_AB / Site C 参数 | 403 | PASS |
| USER_NONE / 设备 Site、列表、详情 | fail-closed 403 | PASS |
| USER_A/USER_NONE / 写入、CSV | 保留 ADMIN 角色 403 | PASS |
| SUPERADMIN / 全局 Site、库存、详情 | 既有 GLOBAL 合同 | PASS |
| 缺 Origin/CSRF 的写入 | 403 | PASS |
| 稳定 ID + IP 编辑 | ID 不变，IP 更新，审计存在 | PASS |
| credential/password/secret 返回和审计 | 不进入响应或浏览器存储 | PASS |
| FIT_AP 直接创建与 CSV 新建 | 独立创建拒绝 | PASS（既有 Browser E2E + harness） |
| CSV / batch 原子和过量目标 | 预览/版本/目标 scope 校验，不静默跳过 | PASS |

专项脚本：`tests/r2_site_claims_e2e.py`，实际输出 `R2_SITE_CLAIMS_TESTS=31/31 PASS`。
