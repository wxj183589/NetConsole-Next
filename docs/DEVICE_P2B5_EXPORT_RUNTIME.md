# P2B5 Local Export 运行时实现

日期：2026-10-27  
Agent：codex-A  
Branch：`codex-A/next-device-management-export-runtime-20261027`  
状态：`IMPLEMENTED_AND_VERIFIED`

## 实现边界

- 新增 isolated auxiliary `p2b5_exports` schema；只允许显式临时路径迁移，Catalog 启动不自动创建。
- Export API 只在 P2B feature、v14 isolated authority、P2B4 test/migration gate 同时开启时可用；当前仅 `SUPERADMIN` 可发起，并且请求必须携带显式 Site scope。
- capture 在固定 Network Domain scope 下生成 immutable snapshot，保存 schema version、capturedAt、source revision、scope、行数、manifest 和 content SHA-256；重复 idempotency key 返回同一 operation。
- JSON/CSV/HTML/TXT 由同一 snapshot 生成；CSV 有严格列集合、长度上限与公式注入保护，HTML 使用 CSP 和文本转义。
- 下载前重新计算 hash；缺失或篡改文件返回结构化拒绝，不返回路径或原始错误。
- 不接 WPS、KDocs、云 API、在线文档，不读取生产/开发真实数据，不触发设备采集、配置写入或 migration outside fixture。

## 验证

- Service/Test Release build：PASS（仅保留既有 NU1903 依赖告警）。
- `python tests/p2b5_export_e2e.py`：PASS；覆盖 v12 deny、ADMIN deny、fixed snapshot/idempotency、CSV hash/formula guard、HTML escape/CSP、scope conflict 和 hash tamper reject。
- 未读取或写入 `D:\NetConsoleData`、`D:\NetConsoleData-dev`，未连接真实设备、凭据或云服务。
