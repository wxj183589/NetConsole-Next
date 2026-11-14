# R3.1 H3C 凭据迁移决策

更新时间：2026-11-14（Asia/Shanghai）

## 决策

采用“显式逐设备重录、写入 DPAPI、清理该设备旧 Catalog 字段”的迁移方式，不执行完整数据根复制，不执行后台批量明文导出，也不自动把旧密码写入 vault。

## 原因

1. R3 当前数据库中存在按 profile 保存的旧密码，而 DPAPI vault 以前没有与 profile identity 建立稳定绑定。
2. 共享 `credential_reference` 不能作为 R3 设备密码唯一键；本次选择 `DEVICE_<profile-id>`，避免两个设备共用 reference 时互相覆盖。
3. DPAPI 与 SQLite 不是同一事务资源，因此写入顺序固定为“vault 写入 → Catalog 元数据清理”，失败时恢复原 vault 值。
4. 旧值没有对应的安全审计授权和用户确认时，自动搬运会扩大真实数据影响面，故不自动迁移。

## 运行行为

| 情况 | 结果 |
| --- | --- |
| 新 profile + 新密码 | 写 DPAPI；Catalog 只写非秘密元数据 |
| 已迁移 profile + 更新密码 | 替换 DPAPI；Catalog 秘密字段保持为空 |
| 已迁移 profile + 留空更新 | 保留 DPAPI，不改变密码 |
| 已迁移 profile + 显式清除 | 撤销 DPAPI，并清理 Catalog 字段 |
| 旧 Catalog 有密码、DPAPI 无值、请求留空 | `503 CREDENTIAL_MIGRATION_REQUIRED`，不丢数据 |
| DPAPI 解密/权限/平台失败 | `503` 脱敏错误，记录失败审计 |

## 未授权动作

- 不读取 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`。
- 不创建完整数据库副本。
- 不批量迁移真实设备密码。
- 不删除旧凭据字段的历史数据，除非用户对对应设备明确设置新密码或清除密码。
