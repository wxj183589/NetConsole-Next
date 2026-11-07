# R7.1 备份批次历史重启闭环

日期：2026-11-08；Agent codex-A。整体仍 PARTIAL；本专项不改变整机恢复、HA、FIT 与 SCM 的验收边界。

## 实际问题与产品修复

自然到期 AC1 备份本身成功，但追加浏览器检查发现旧 SW 计划保留的最近批次 ID 在 Service 重启后返回404。原失败证据为 Workspace `reports/r71-browser-natural-closure-20261108/result.json`，不能以备份文件存在替代最近明细可读。

新终态批次元数据写入 Next DataRoot 的 `backup/config-batches.json`，最多128条、文件上限4MiB；不保存配置正文、凭据或管理端点，不重放运行中任务。写入采用同目录临时文件、flush与原子替换。损坏或不可写时返回明确 `CONFIG_BACKUP_BATCH_STORE_UNAVAILABLE`，保留旧证据与配置版本，拒绝继续接收备份；其它采集/故障服务继续运行。

读取每次检查当前有效 SUPERADMIN、所有者和profile的当前Site范围。计划返回实时计算的 `lastBatchAvailable`，页面保留旧执行结果，但对无法读取的旧批次禁用查看按钮并引导到各设备完整历史。已丢失的旧内存明细不根据文件时间或快照拼造。

独立审查还修复了悬空链接临时文件、启动响应提前发布完成状态、HTTP错误未映射和复杂任务错误使存储误判损坏的问题。批次错误只输出受限安全码；超时使用专码，复杂文本回退通用采集失败码。

## 验收边界

旧批次不会自动恢复；超出128条的终态元数据会淘汰，配置版本保留策略独立。中断的运行批次不在启动后自动重放。此能力不等于整机恢复可用，也不是整个DataRoot的磁盘配额。

## 最终验收记录

- 定向完整批次18/18、计划19/19；最终完整Service harness `reports/r71-service-harness-history-final.log` EXIT0。`r71-harness-history-build.log`、`r71-service-history-build.log`均0 warnings/errors。
- Web27文件306项、typecheck和Vite2115modules通过：`r71-web-history-final.log`、`r71-typecheck-history-final.log`、`r71-vite-history-final.log`。
- 隔离真实HTTP26组PASS：`reports/r71-feature-http-20261108-022949-aa3f4c.json`，包含503专码、损坏证据保留和故障中心继续200。首次新增断言误将Client返回的错误JSON字符串当字典，已修正测试解析；原失败 `r71-feature-http-history-final.log` 保留，产品当时已正确返回503。
- 页面创建SW计划 `de2832f1536043ee8f3e5918c438210c` →立即执行一次→成功→暂停。批次 `780ab2ecaa5148ed928935183e796920`，快照 `f424f963583e46deaeab52993d80e1e6`，私有/脱敏文件hash及字节数验证通过。`reports/r71-schedule-browser-2026-11-07T18-30-05-043Z/result.json`为PASS_UI_CREATE_RUN_PAUSE_RECENT_REAL_SW。
- Service从PID108044受控退出、PID85584启动后，同一owner浏览器仍读取完全相同的批次与快照，计划enabled=false、nextRunMs=null、lastBatchAvailable=true；旧SW批次明确false且按钮禁用。`reports/r71-browser-history-restart-20261108/result.json`0 API/page/console错误，1920/2560配置页截图已目视；未调用新采集或恢复。
- 新静态UI全流程 `reports/r71-browser-history-final-20261108/result.json`：登录、总览入口、故障CSV、故障筛选/历史/导航、配置历史/比较/下载、拓扑筛选/CSV、Syslog全部PASS，0 API/page/console错误。22张截图、布局检查通过；保留原失败证据。
