# 多任务并发验收

状态：PASS（隔离调度 fixture）。全局 bounded queue、max concurrency、设备级 semaphore、失败单独重试、取消、Interrupted 显式 resume 均保留；任务详情、取消、重试、恢复按 ID 查询，不受最近 100 条列表窗口限制。`CONCURRENCY_P1_TESTS=5/5 PASS`；完整 Service harness 通过。真实设备并发未宣称。
