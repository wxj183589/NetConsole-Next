# R7.8 无线与双AC HA
日期2026-11-09；只读采集PASS，**REAL_HA NOT_TESTED**。

复用冻结R3合同：AC Domain/Member、AP Resource/Observation/Group、Serial/MAC精确身份和ONE_PLUS_ONE。双AC各自Observation不互相覆盖，同一精确AP资源不因管理IP变化自动新建身份。

## 本轮实际产品采集
reports/r72-product-dual-ac-20261109-005924/summary.json：
- AC1 192.168.56.2，run b2e7408252d141b8b94ee705b69682da。
- AC2 192.168.56.5，run 76ecb8dae60946f69f3a0b64cf242254。
- 各6条只读命令；SUCCEEDED、Raw完整、Facts发布；新增12 Raw，总数164→176，最终逐文件长度/hash通过。
- 命令为display version、display wlan ac-role、display wlan ap all、display wlan ap all address、display wlan ap all radio、display wlan ap all verbose。精确命令和Raw路径以summary为准。
- 本轮未切换角色、修改优先级、断链、重启AP、保存配置或执行回切。只读命令不证明无线业务连续。

## HA准入与缺口
需要两个AC完整配置与启动指向备份，可逆触发、终止/回滚步骤，以及同步AP运行/关联和真实客户端业务证据。没有实际切换、耗时、业务恢复和回切证据，不宣称HA通过。
[原子工作流记录](R7_8_HA_TEST_REPORT.md)保留其当时未建立会话的范围；本报告补充主集成后续实际只读证据。FIT原生状态见[FIT报告](R7_8_FIT_AP_REPORT.md)。
