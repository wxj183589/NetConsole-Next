# P2B-2 acceptance

## 已执行

- `dotnet restore tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj --locked-mode`
- `dotnet build tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj --configuration Release --no-restore`
- `dotnet tests/NetConsole.Next.Service.Tests/bin/Release/net10.0/NetConsole.Next.Service.Tests.dll`

完整 runner 通过，包含既有 P2B-1 合同、v12 read-only preflight、v12→v13 隔离迁移、legacy backfill、幂等重跑、关系/约束/完整性检查、Catalog restart compatibility 和进程级 crash recovery。构建仅保留仓库既有 `SQLitePCLRaw.lib.e_sqlite3` `NU1903` advisory。

追加的迁移路径安全测试全部通过：临时目录边界、相似前缀和 `..` 逃逸、symlink、junction/reparse-point、连接路径 mismatch、生产路径、显式 flag 和 isolated context 缺失均按预期拒绝或允许。详见 `DEVICE_P2B2_FINAL_REVIEW.md` 与 `DEVICE_P2B2_MIGRATION_SECURITY.md`。

## CI 与审批

PR23/PR24 当前仍为 Draft/Open，尚无正式 approval；P2B-2 应以 Draft PR 供 review。P2B1 已有的 Windows syntax 和 G1 CI 证据需保留：G1 首次 Ubuntu archive admission 曾因时序失败，重跑后通过；这不应被改写为“首次全绿”。本分支 PR25=`https://github.com/wxj183589/NetConsole-Next/pull/25`：Windows deployment syntax run `40346448663` SUCCESS；G1 run `40346448548` Ubuntu/Windows SUCCESS。

## 数据安全

本次测试只使用临时 fixture。未读写真实生产或真实开发数据、真实设备、真实协议、真实 VLAN，也未启用正常启动自动迁移。

## 当前状态

`P2B2_SCHEMA_VERIFIED_APPROVAL_PENDING`：candidate schema、迁移和自动化验收已完成；正式审批、CI 结果和生产迁移授权仍待完成。

