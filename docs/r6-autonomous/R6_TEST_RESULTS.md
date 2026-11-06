# R6 测试结果

R6.1 验证结果：

- Web 全量 Vitest：20 文件、211 测试 PASS（含 SmartConfig、Topology、ConfigRestore UI/API client）。
- `npm run typecheck`：PASS。
- `npm run build`：Vite 2095 modules PASS；Dashboard chunk 约 1.14 MB、index chunk 约 1.03 MB，保留为性能后续项。
- C# SmartConfig：`SMART_CONFIG_TESTS=5/5 PASS`。
- C# 完整 Service harness：EXIT 0，既有无线、Netmiko、配置备份/恢复、诊断、并发和迁移门禁通过。
- `dotnet build src/NetConsole.Next.Service/NetConsole.Next.Service.csproj -c Release --no-restore`：0 warnings / 0 errors。
- `git diff --check`：PASS。

四台白名单模拟器仅做 TCP 可达性检查；未执行认证、命令、配置写入、HA/failover 或恢复。未执行的现场设备、GUI 安装、跨机器 TLS、生产数据和配置写入测试不得标记为 PASS。
