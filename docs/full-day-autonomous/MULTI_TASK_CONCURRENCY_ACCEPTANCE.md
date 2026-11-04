# 多任务并发验收

状态：PASS（隔离调度 fixture）。全局 bounded queue、max concurrency、设备级 semaphore、失败单独重试、取消、Interrupted 显式 resume 均保留。`CONCURRENCY_P1_TESTS=4/4 PASS`；完整 Service harness 通过。真实设备并发未宣称。
