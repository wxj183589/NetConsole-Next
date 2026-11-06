# R6.1 部署报告

本轮验证了 Service Release 构建、静态 UI 构建和既有离线候选门禁范围；未生成或发布新安装包。

Windows Service 安装、SCM 启停、跨机器 Web/TLS、升级、回滚、卸载和目标机兼容仍需独立环境验收。Web 默认 loopback 绑定和现有认证/Site scope 边界保持不变。不能以本机 `dotnet build` 或 Vite 构建替代部署 PASS。

状态：`BLOCKED_EXTERNAL`。
