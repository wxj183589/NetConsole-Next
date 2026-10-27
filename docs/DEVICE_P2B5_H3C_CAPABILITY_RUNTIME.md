# P2B5 H3C V5/V7 Capability 运行时实现

日期：2026-10-27  
Agent：codex-A  
Branch：`codex-A/next-device-management-h3c-capability-runtime-20261027`  
状态：`IMPLEMENTED_AND_VERIFIED`（mock-only）

## 实现

- 新增纯 .NET `H3cCapabilityService`，输入仅为 synthetic text/bytes；通过 DI 注册，但不连接 transport、不读取凭据、不写 Catalog/Raw。
- 复用现有 Comware parser，识别 V5/V7 的 version/release，并返回稳定的 vendor、version、release、versionFamily、recognized、source、sourceReference、confidence、observedAt、capabilities 和 failureCode。
- capability 只包含 `READONLY_DISPLAY_VERSION`、`READONLY_INTERFACE_BRIEF`、`PAGINATION_GUARD`；不映射写配置权限、采集调度权限或现场授权。
- 未知版本返回 `UNRECOGNIZED` 和空 capability；分页提示、无效编码、超限输入分别返回脱敏失败类别，不回显原始输出。
- 支持 UTF-8/GB18030 mock bytes，source reference 有长度和控制字符边界。

## 验证

- V5/V7 fixture、未知版本、source/confidence、分页 prompt、UTF-8/GB18030、invalid encoding、1 MiB 输入上限、失败脱敏均由 `H3cCapabilityTests` 覆盖。
- `dotnet build tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release --no-restore`：PASS。
- `dotnet tests/NetConsole.Next.Service.Tests/bin/Release/net10.0/NetConsole.Next.Service.Tests.dll`：PASS，输出 `PASS: H3C V5/V7 mock capability service...`。
- 未连接 SSH/Telnet/COM，未使用真实 IP、凭据、Host Key、VLAN、站点数据；真实 V5/V7 设备验收仍为 `REAL_DEVICE_PENDING`。
