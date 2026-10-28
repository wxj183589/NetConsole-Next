# P2B5 Rail Reference 运行时实现

日期：2026-10-27  
Branch：`codex-A/next-device-management-rail-reference-runtime-20261027`  
状态：`IMPLEMENTED_AND_VERIFIED`（仅隔离 synthetic fixture）

## 已实现

- 新增 `p2b5_rail_reference_imports`、`p2b5_rail_entities`、`p2b5_rail_relations` auxiliary schema；只允许显式 migration harness 在临时 v14 authority 上创建，不改变 Schema v14 版本号。
- 运行时提供 `GET /api/system/rail-reference/entities`、import preview/status 和显式 `confirm` API；接口仅向隔离 SUPERADMIN 暴露，ADMIN 继续拒绝。
- reference entity 只接受显式稳定 ID 和精确类型：`LINE`、`SECTION`、`STATION`、`SITE`。关系显式表达 `LINE_SECTION`、`SECTION_STATION`、`STATION_SITE` 与 `LINE_STATION`，因此支持一条线路关联多个车站，不靠名称、IP、设备名或模糊匹配推断。
- preview 固定 source name、source revision、canonical source hash、scope、schema version、effective/captured time，展示新增、更新、未变化、冲突、未知引用和删除候选；未 confirm 不写入实体或关系。
- confirm 重新校验 preview 与 authority，拒绝过期、revision/hash/source/scope 冲突和未知关系；成功后保留 import operation、actor、before/preview 证据，旧 revision 不会被自动覆盖。
- 不触发设备采集、SSH/Telnet/COM、地址/VLAN 写入、Site membership 写入或生产数据迁移；没有提交真实线路、车站、设备或现场凭据。

## 验证

- `dotnet build src/NetConsole.Next.Service/NetConsole.Next.Service.csproj -c Release --no-restore`：PASS；存在既有 `NU1903` 依赖漏洞警告。
- `dotnet build tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release --no-restore`：PASS。
- `dotnet tests/NetConsole.Next.Service.Tests/bin/Release/net10.0/NetConsole.Next.Service.Tests.dll`：PASS，包含 rail migration、preview/confirm、层级关系和冲突 guard。
- `python tests/p2b5_rail_reference_e2e.py`：PASS，覆盖 v14 gate、ADMIN 拒绝、hash/status、many-to-many、幂等、未知引用 fail-closed 和显式 revision update。

## 尚未宣称

- 没有授权真实线路基础数据源，也没有进行生产/开发数据导入。
- 本阶段不代表真实轨交拓扑、杭州 10 号线、设备 identity 或现场 VLAN 规划已验证。
