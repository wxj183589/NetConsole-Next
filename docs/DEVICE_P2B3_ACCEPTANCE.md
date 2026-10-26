# P2B-3 只读 API 验收记录

## 本地验证

已执行：

- \`dotnet restore src/NetConsole.Next.Service/NetConsole.Next.Service.csproj --locked-mode\`
- \`dotnet restore tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj --locked-mode\`
- \`dotnet build src/NetConsole.Next.Service/NetConsole.Next.Service.csproj -c Release --no-restore\`
- \`dotnet build tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release --no-restore\`
- \`python tests/p2b3_read_api_e2e.py\`

真实 HTTP E2E 使用临时 v12 fixture、显式隔离 v13 migration、10,000+ 地址 facts、domain scopes、planned/observed VLAN 和 \`REVIEW_REQUIRED\` diff fixture。没有读取或写入 \`D:\\NetConsoleData\`、\`D:\\NetConsoleData-dev\`，没有连接真实设备、协议或 VLAN。

E2E 已通过：

- \`FEATURE_GATE_OFF\`
- \`FEATURE_GATE_ON_V12\`
- \`SITE_SCOPE_AUTH\`
- \`CROSS_SITE_DOMAIN_AUTH\`
- \`P2B3_DEVICE_ADDRESS_API\`
- \`P2B3_NETWORK_MEMBERSHIP_API\`
- \`P2B3_NETWORK_DOMAIN_API\`
- \`P2B3_VLAN_PLAN_API\`
- \`P2B3_NETWORK_DIFF_API\`
- \`SERVER_SIDE_PAGINATION\`
- \`STABLE_SORTING\`
- \`QUERY_BOUNDS\`
- \`SQL_PARAMETERIZATION\`
- \`PLANNED_OBSERVED_COEXISTENCE\`
- \`SOURCE_PROVENANCE\`
- \`EFFECTIVE_PROJECTION\`
- \`DIFF_CONFLICT_STATE\`
- \`NETWORK_DIFF_STALE_REVIEW\`
- \`GET_NO_SCHEMA_MUTATION\`
- \`GET_NO_CATALOG_MUTATION\`
- \`P2B3_10K_QUERY_TEST\`
- \`P2B3_HTTP_E2E\`

本地构建保留仓库既有 \`SQLitePCLRaw.lib.e_sqlite3\` \`NU1903\` advisory；无编译错误。

## CI 与审批

P2B3 分支需要在最终 push 后记录 Ubuntu、Windows 和 PowerShell CI run URL。PR23–PR25 当前没有 formal approval，因此即使所有自动化通过，最终状态仍为：

\`P2B3_READ_API_VERIFIED_APPROVAL_PENDING\`

P2B4 和 P2B5 明确未实现；生产 v13 migration 和真实设备连接未运行。
