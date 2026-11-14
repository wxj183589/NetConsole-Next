# R3.1 H3C 设备凭据权威审计

更新时间：2026-11-14（Asia/Shanghai）

## 结论

R3 生产运行时的 H3C 设备凭据权威已收敛到设备绑定的 Windows DPAPI vault：

```text
Authority = <data-root>\credentials\h3c-credentials.v1.json
Key       = DEVICE_<lowercase-profile-guid-as-uppercase>
Reader    = H3cSsh / Terminal / complete-configuration via IH3cCredentialAuthority
Writer    = H3C profile onboard/create/update-credential HTTP API
```

SQLite `h3c_device_profiles.credential_password` 只保留为受控迁移检测字段；生产写路径会清空它，运行时不再从该字段读取密码。凭据状态查询也以 DPAPI vault 为准。

## 审计证据

- R3 原路径是 `DeviceRecordH3cCredentialProvider(Catalog)`，直接读取 `h3c_device_profiles.credential_password`。
- `WindowsDpapiCredentialVault` 和 LocalManager `credential set/revoke` 已存在，但此前未被 R3 服务生产 DI 使用，形成独立写入路径和未消费的 vault。
- R3 的 H3C profile identity（host、port、username、credential reference、已固定 host key）仍由 Catalog 事务维护；本次没有把 host-key 变更错误地移植为旧 DeviceEditor 逻辑。
- 生产 profile 写入先更新 DPAPI，再写 Catalog 元数据；Catalog 失败时执行 DPAPI 补偿，补偿失败返回不可确认状态并记录脱敏审计。
- 不在审计、错误、API 响应或测试输出中记录密码、DPAPI 明文或完整异常消息。

## 兼容与边界

- 现有仅存于 SQLite 的旧凭据不会被自动复制或静默删除。若 vault 没有对应凭据而 Catalog 仍有旧值，更新请求返回 `503 CREDENTIAL_MIGRATION_REQUIRED`，要求显式重新录入该设备密码。
- 只有在用户显式设置新密码或清除密码时，旧 Catalog 字段才随该设备操作被清空。
- ZTE、Linux、Site Jump Host 的现有凭据表不在本次 H3C DPAPI 收敛范围，不能宣称全产品所有厂商凭据已经统一。
- 真实 Production/Dev data 未读取、复制、迁移或覆盖；本次测试只使用临时隔离根。

## 状态

```text
H3C_RUNTIME_AUTHORITY=PASS
H3C_WRITE_COMPENSATION=PASS (targeted code path; test result recorded separately)
LEGACY_SQLITE_AUTO_MIGRATION=NO
REAL_DATA_MIGRATION=NOT_RUN
OTHER_VENDOR_AUTHORITY=NOT_IN_SCOPE
```
