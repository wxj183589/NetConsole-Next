# R2 Security and Identity Regression

## 结论

`DEVICE_IDENTITY_REGRESSION=PASS`、`CREDENTIAL_SECURITY_REGRESSION=PASS`、`FIT_AP_CREATION_RESTRICTION=PASS`。Site claims 的多 Site 覆盖范围另见 `R2_SITE_CLAIMS_API_CONTRACT_AUDIT.md`，不因本页 PASS 而扩大解释。

## 设备身份与写入

- `device_inventory.id` 是列表、详情、编辑、审计和终端关联使用的稳定设备身份；编辑使用路径 `id`，不会因管理 IP 变化生成新设备。
- 同一 Site 内管理 IP 由服务端唯一性检查保护；端到端流程验证了 `192.0.2.90 → 192.0.2.91` 的稳定 ID 编辑。
- Terminal profile 与设备是一对一链接，服务端拒绝重复绑定；更新使用 `updated_ms` 版本语义，CSV preview/commit 具备原子和版本校验。
- 写入、批量操作和 CSV 结果写入 `audit_events`；详情可读到 `DEVICE_CREATE` 等审计动作。
- 当前业务使用停用/批量禁用，不为测试新增永久删除接口。

## 凭据安全

- 设备库存和前端 DTO 只显示 `credentialConfigured`、用户名/终端档案引用等必要状态，不把密码放入设备表单、列表、详情、URL 或 CSV。
- UI 表单明确“仅引用服务端档案”；编辑不会回显密码。
- Windows 环境继续使用既有 DPAPI credential vault；既有 C# harness 覆盖 DPAPI round-trip、明文不落库和引用解析时才取 secret。本轮没有改变 provider、vault 或 transport。
- 审计 payload 使用设备元数据和引用 ID，不记录密码；浏览器回归没有写入真实凭据。
- 登录使用 HttpOnly session cookie + CSRF token；本轮 E2E 通过既有 `/api/auth/csrf` 合同取 token，没有绕过 CSRF。

## FIT-AP 边界

- UI 新建表单不提供 FIT_AP 独立创建入口；已有 FIT_AP 仅作为 AC 管理的既有记录边界。
- 直接 `POST /api/system/devices` 新建 `FIT_AP` 返回 HTTP 400，服务端消息明确要求由 AC 管理。
- CSV 新建 `FIT_AP` 预览返回 `FIT_AP_MUST_BE_MANAGED_BY_AC`，不可原子提交。
- 普通设备创建、编辑和 CSV 流程仍通过。

## 证据

- `tests/device_management_browser_e2e.mjs`
- `web/src/api/client.test.ts`
- `tests/NetConsole.Next.Service.Tests/Program.cs` 的 Catalog、inventory、audit、credential 和 security assertions
- `src/NetConsole.Next.Credentials/WindowsDpapiCredentialVault.cs` 及既有安全测试
