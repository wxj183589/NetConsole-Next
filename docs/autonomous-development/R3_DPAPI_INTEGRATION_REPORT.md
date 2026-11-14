# R3.1 DPAPI 集成报告

更新时间：2026-11-14（Asia/Shanghai）

## 已完成

- R3 Service DI 默认绑定 `WindowsDpapiH3cCredentialAuthority`。
- H3C 采集、交互终端和完整配置读取通过 `IH3cCredentialAuthority` 解析设备绑定凭据。
- H3C profile onboard/create/update-credential 统一走凭据服务，生产 Catalog 写路径不再保存明文密码。
- 设备库存的“凭据已配置”状态按 DPAPI vault 判断，而不是按旧 SQLite 字段判断。
- DPAPI 存储异常、解密异常、权限异常和平台不支持统一返回脱敏 `503`，并写入失败审计；不会回显 secret 或异常原文。
- 跨 DPAPI/Catalog 写入失败时执行补偿，补偿失败显式标记为不可确认，不宣称成功。

## 未完成 / 不宣称

- 未对真实数据根执行迁移。
- 未将 ZTE、Linux、Jump Host 的凭据存储改成 DPAPI；它们仍是独立边界。
- 未把 Draft PR #49 整支 cherry-pick 到 R3；仅复用了错误分类、脱敏和主机密钥边界的语义。
- Windows 服务安装、真实设备认证和 8–24 小时运行仍须独立验收。

## 验证门

```text
CODE_COMPILE=PASS (dotnet build Release, 0 errors)
TARGETED_CREDENTIAL_TESTS=PASS (DPAPI authority, credential, connection policy, simulator configuration, onboard)
REAL_DEVICE=NOT_VERIFIED
REAL_DATA=NOT_TOUCHED
R3_PUSH=NO
```
