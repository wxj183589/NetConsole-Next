# 单目标采集任务Scope增量验收（Checkpoint G）

日期：2026-11-05。实现/最小SQLite/调度器离线PASS，HTTP Gate仍pending。

本批复查发现，现有单目标collector无需新表：可把creation Scope Snapshot写入既有task_definitions.request_json，task_runs继续通过definition_id和source_id引用。只对当前已有单目标H3C Task作增量，不冒充已经实现多目标无线Plan/Report系统。

## 数据与不可变边界

Scope Snapshot记录Version、创建actor稳定ID、inventory设备稳定ID、Profile ID、Site、创建时间和实际claims revision。它不存密码、session token、credential reference或命令输出。

一旦definition已被任何run引用，目标device/profile/Site边界不可扩大、不可清除。相同目标的命令/超时显式编辑仍可用，已排队WorkItem保留自己的request；数据库deadline也改用入队时timeout。要改目标必须使用新definition ID，避免修改定义后回溯扩大旧run可见范围。入队事务复核定义及scope，存在并发修改则拒绝。

已有无Scope Snapshot的历史任务：全局operator保留读取；Site-scoped list不推测其创建范围，不暴露其结果。已有run的无scope definition不能追填新scope给旧数据“补授权”，需要新ID。无scope definition不经公开执行入口启动。这是任务范围缺失的fail-closed语义，不是Legacy HistoryStore fallback。

## 执行与撤权

- 公开API仍SUPERADMIN-only创建和运行；创建/执行重新读取当前actor/role，防止初始HTTP身份缓存掩盖后续降权或disabled。
- 每个run以实际启动operator作为当前执行actor，目标上限继承definition的冻结Snapshot；运行actor写入安全审计用于追溯，审计不承担运行时授权Authority。
- 入队、factory/连接前、每个命令前、命令结束及结果commit前重新检查actor、角色、当前Site claims、目标设备/站点/profile一致性与enabled。新claims、revision-only变更不扩张旧目标。
- 尚未启动的撤权目标不连接；已经接受的有界connect可以结束，但不发后续命令。原子command中撤权可完成当前有界操作，封存PARTIAL Raw，Task=CANCELLED，reason=REVOKED_AFTER_START；不记为设备offline或SUCCEEDED。
- cancel与revoke同时发生保留双方安全reason；先前transport failure与结果边界撤权同样均可追踪。NEVER restart策略保持不变，无自动resume/retry绕过门禁。
- generic task list在limit之前同时检查当前source Site与不可变creation subject proof。修改Site/profile绑定不能借当前global摘要泄露旧结果。新增claims revision指纹依据有效授权来源的revision与有效期，只有revision变更时也能追踪；不含被排除Site业务数据。

## 验证

新增11个定向case通过：持久快照/不可扩张/超时冻结、claims revision变化与新增授权、legacy隔离、排队撤权不连接、connect后撤权不发command、有界command撤权封存、同时cancel/revoke、正常授权采集、排队cancel/revoke、dispose/result-commit边界撤权（有/无先前失败）。使用实际最小SQLite、现有H3cTaskRawResultSink和可控IDeviceSession，不使用真实设备，不削弱原有断言。

公开API/真实模拟器仍待原Service维护窗口与凭据；多目标Plan/Report、正式AP identity lifecycle及Raw manifest/hash关联不由本批宣称完成。
