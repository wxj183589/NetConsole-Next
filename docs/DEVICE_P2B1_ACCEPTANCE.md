# P2B-1 验收记录

状态：P2B1_CONTRACTS_VERIFIED_REVIEW_PENDING。

## 验收范围

- 领域枚举、来源 provenance、半开 validity、IPv4/IPv6 规范化。
- 多 IP source fact 与 address claim 冲突边界。
- Network Domain 多 site/station scope、权限可见性和 parent cycle。
- VLAN validity、跨域复用、planned/observed coexistence、PVID/tagging/QinQ。
- planned/observed authority、人工 review、stale revision、effective projection。
- legacy `management_ip` 兼容、pending/conflict/resolved 状态、identity/profile link 保留。
- 错误 DTO、HTTP status、feature gate 和 Schema v13 只读 preflight。

## 测试资产

- 实现：[P2B1Contracts.cs](../src/NetConsole.Next.Service/P2B1Contracts.cs)
- 预检：[P2B1SchemaV13Preflight.cs](../src/NetConsole.Next.Service/P2B1SchemaV13Preflight.cs)
- 合成测试：[P2B1ContractTests.cs](../tests/NetConsole.Next.Service.Tests/P2B1ContractTests.cs)
- fixture：[p2b1_contract_fixture.json](../tests/NetConsole.Next.Service.Tests/fixtures/p2b1/p2b1_contract_fixture.json)

## 验收命令

```powershell
dotnet build src/NetConsole.Next.Service/NetConsole.Next.Service.csproj -c Release --no-restore
dotnet build tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release --no-restore
dotnet run --project tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release --no-build
pnpm run typecheck
pnpm run test
pnpm run build
git diff --check
```

预检必须通过隔离 synthetic v12 fixture；不得把 production 或 `D:\NetConsoleData-dev` 统计写入验收结论。CI、PR review 和 GitHub workflow 结果在 push 后追加；正式批准前状态保持 review pending。

## 明确未做事项

- `SCHEMA_V13_IMPLEMENTED=NO`。
- `SCHEMA_VERSION_UNCHANGED=YES`。
- `MIGRATION_EXECUTED=NO`。
- 未新增 P2B-2…P2B-5 API、route、UI 或真实设备访问。
- 未修改主分支、PR23、P2A worktree 或真实数据目录。

## P0 与 reviewer pending

三项 P0 决策为：同设备多来源共存且同域 claim 判重、legacy 无 domain 不猜测、VLAN 使用半开有效期。替代方案、DB/API/兼容影响和测试矩阵分别记录在地址、scope、VLAN、authority 与 preflight 文档中。待 reviewer 确认后，下一步才可进行 P2B-2 设计或 v13 migration implementation 评审。
