# R7.7 双 AC / HA 验收报告

日期：2026-11-08。Agent：codex-A。结论：**HA 切换、回切及客户端业务连续性尚未验证**。双 AC 同时存在和 AP 的 R/M、R/B 观察只说明各控制器当时看到的状态，不能升级为 HA 成功。

## 既有准入证据

历史原件为 `D:\study\NetConsole-Next-Workspace\reports\r71-ha-admission-20261108-004028\summary.json`，采集窗口始于 UTC `2026-11-07T16:40:28.084900+00:00`。AC1 的 `default-group` 有 `backup-ac ip 192.168.56.5`、`priority 7`；AC2 有 `backup-ac ip 192.168.56.2`。这些是经过白名单过滤的配置网络指令，不保存完整配置及凭据。该快照不能证明当前配置未变化，优先级和备 AC 指令也不能证明状态已切换。

历史命令树原件 `reports/r71-ap-help-20261108-005711/summary.json` 记录双 AC 认证、AP-group 中 `backup-ac`、`priority`、`switch-back` 等帮助读取，并记录读前/读后配置 SHA256 一致。只查询帮助不等于执行帮助列出的命令；`reset wlan ap ?` 同样没有执行 reset。更早的 `reports/r69-ha-20261107/summary.json` 中 `display ha service` 被标为 unsupported，不能把不支持命令的错误输出解释成 HA 状态。

## 本轮操作边界

设备验收子任务限定为只读采集，不关端口、不 reset AP、不改变优先级、不触发主备切换，不执行 `save`，也不为本次测试创建 HA 配置。既有 SW 配置已在后续出现认证失败，本轮不重复提交该配置；历史 SW 登录成功不代表当前有效凭据。

本轮候选的双 AC 独立观察及 Raw 验证由 [设备报告](R7_7_DEVICE_INTEROP_REPORT.md) 与 [无线报告](R7_7_WIRELESS_REPORT.md) 记录。控制器角色 `Common` 应保留原始语义，不能标成 ACTIVE/STANDBY。零客户端或 Radio Down 时，即使控制器读取成功，也没有客户端连续性证据。

## 剩余验收

仍需在完整配置备份与原始哈希验证、可靠控制路径、确定的窄范围触发命令、回滚及业务读回具备后，执行有边界的切换和回切；保留切前/切中/切后每个 AC 的时间、独立 Raw、AP 身份及业务客户端证据。没有上述实际窗口时，HA 维持 NOT_TESTED，不由产品单元测试、TCP 可达或静态 R/M/R/B 替代。

本报告没有新增设备操作、没有修改业务数据，没有访问旧 NetConsole 数据根。本地提交由主集成统一完成；本轮不 push、不远端 merge、不发布。
