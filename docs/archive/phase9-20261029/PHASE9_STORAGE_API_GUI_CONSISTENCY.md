# Phase 9 存储、API、GUI 一致性

## 已验证

- H3C command output 以 UTF-8 解码文本写入单条 Base64 NDJSON Raw segment。
- capture status、parse status、task terminal state 分离；PARTIAL 不会成功收口。
- Raw segment、sha256、task run 和 manifest 通过 SQLite 事务关联；恢复路径保持证据。
- C# harness 验证空输出、未知格式、分页、断线、取消、输出上限、SQLite 事务和会话释放。

## 当前缺口

Next 尚未提供 H3C/ZTE/AC/轨旁统一的厂商事实表、当前/历史 bounded history、领域 HTTP DTO 或 GUI 表格。ZTE parser 目前只在离线测试中运行，不能被 HTTP 或 GUI 误认为已采集。Phase 9 因此把“解析正确”和“跨层一致”分开计分：前者已通过，后者对新增厂商能力仍为缺失。

## 安全与数据影响

本轮只读检查 `D:\NetConsoleData-dev` 的文件名、大小和 SHA-256 摘要，没有复制、修改或写回 Production/Dev Authority；仓库 fixture 均为脱敏样本。未执行真实设备命令、配置读取或写操作。
