# P2B-4 验收记录

## 自动化证据

`tests/p2b4_write_api_e2e.py` 使用临时 data root，覆盖：

- v12 不隐式迁移、read/write gate 分离、ADMIN 拒绝、缺 CSRF 拒绝；
- 地址同 domain/family/normalized address 冲突、prefix 不绕过、跨 domain 复用、同设备多来源、revision stale、management_ip 不变；
- membership 写入；
- VLAN planned/observed 共存、重叠拒绝、相邻和跨 domain 允许；
- stale review、source preservation；
- import preview/dry-run/confirm/idempotency/cancel、atomic rollback、audit redaction；
- v13→v14 显式迁移和 v14 authority 校验。

本地命令：

```text
dotnet build src/NetConsole.Next.Service/NetConsole.Next.Service.csproj -c Release
dotnet build tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release
python -m py_compile tests/p2b4_write_api_e2e.py
python tests/p2b4_write_api_e2e.py
```

## 边界

本阶段不包含 Vue UI、真实设备配置、真实 VLAN 下发、生产/开发数据写入或 main 合并。由于 PR23–PR26 尚无 formal approval，结论只能是：

`P2B4_WRITE_BOUNDARY_VERIFIED_APPROVAL_PENDING`
