# R7.8 配置备份、恢复与回滚
日期2026-11-09；备份/比较/变化事件有产品实现；**整机恢复/失败回滚 BLOCKED**。

## 已实现与验证
单设备、批量、定时、版本、差异、完整私有原件/脱敏下载继续复用现有Service与UI；RUNNING/SAVED/STARTUP_INFO/STARTUP_BODY分别表达，不把启动指向当正文。
3396d25将Comware more纳入完整行终止提示符读取，正文内嵌提示符不能截断；官方SSH/Telnet循环与预算/超时/断连失败封闭在Worker60 tests通过。
76cda4f增加当前设备备份新鲜度提示与入口，复用已存连接档案；无备份、过期、近期、读取失败明确区分。
5500014将最新两个连续完整备份变化接入故障中心；新鲜度、设备/Site/Profile、版本连续及文件完整性均校验。后续稳定版本可恢复事件，说明“稳定不表示回滚”。

完整配置28、比较10、readiness16、checkpoint restore18测试通过；Service全量日志reports/r78-service-full-final.log。配置漂移HTTP14、配置故障HTTP12、浏览器23项通过，均为明示合成配置。最终离线候选配置种类和计划持久化子门禁通过，但完整门禁因ICMP失败。
提醒浏览器读取实际API，显式TCP结果使用fixture；证据详见UI报告。

## 实际设备边界
本轮双AC12 Raw是无线只读采集，没有执行完整恢复或证明新的设备端启动配置正文备份。本修复的离线SSH/Telnet正文测试不能代替设备备份验收。
SW历史认证失败，FIT缺可用认证；历史SFTP_SUBSYSTEM_UNAVAILABLE未被本轮推翻。缺设备传输、长度/hash、原生replace、重连、完整回读、接口/业务及失败回滚，因此本轮设备写入0。
必须分别记录WRITER_ACCEPTED、CONFIG_APPLIED、CONFIG_READBACK_VERIFIED、BUSINESS_VERIFIED、ROLLBACK_VERIFIED；当前不能将它们声明为已验证。

下一步：有效SW档案先只读确认身份/型号/版本并取得完整备份与当前hash；在既有受控恢复门禁满足后执行可逆恢复、回读和业务验证，失败分支另验回滚。缺前提保持阻断，不以命令接受推进状态。
