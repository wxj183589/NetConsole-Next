# Raw → Facts → Current 验收

状态：PASS（隔离 fixture）。schema v16 `task_run_command_outputs` 保存命令序号、命令、Raw 路径、hash、capture/parse 状态；多命令按序执行。成功终态现在要求每个计划命令都有独立封存 Raw manifest，缺失命令会 fail closed 且不提交部分 command set。完整集合使用 collection provenance 发布 Facts；任一命令失败或取消时只保留 partial Raw，不发布 Facts。`H3C_TASK_FACT_PUBLICATION_TESTS=13/13 PASS`。
