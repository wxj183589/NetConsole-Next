# 多任务并发验收

日期：2026-11-06。状态：PASS（本机隔离范围）。

全局 bounded queue、max concurrency、设备级 semaphore、失败独立重试、取消、Interrupted 显式 resume 保留；任务详情/取消/重试/恢复按 ID 查询，不受最近 100 条窗口限制。`CONCURRENCY_P1_TESTS=5/5 PASS`，最后 scope 修复后的完整 Service harness PASS（含 H3C Raw/Facts 13/13）。

Jump 回环集成另覆盖三路会话、取消、资源释放与错误隔离；Linux HTTP/WebSocket 和最终 Edge 浏览器覆盖多会话、并发限制、活动/完全静默撤权和 SSH 释放。网络终端完整 E2E 覆盖取消及重连。

上述模拟测试不证明真实设备并发容量、现场稳定性或双 AC HA；这些仍待独立验收。无设备配置写入、旧真实数据访问或远端发布操作。
