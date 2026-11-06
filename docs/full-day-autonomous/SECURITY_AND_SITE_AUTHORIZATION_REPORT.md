# 安全与局点授权报告

## 2026-11-07 下载与脱敏补充：ef57213

日期：2026-11-07。Branch：`codex-A/netconsole-next-r3-dual-ac`。功能提交 `e93b38280328beb96bbbb1b172b3d0a01179cfef`；备份下载 no-store 修复及当前产品 HEAD `ef5721354fc56004f018a2b851b1aee9fd8b0bdd`。整体 **PARTIAL / REAL_DEVICE_PENDING**。保留当前分支，不 Push、Merge、Release。

证据提交范围：完整后端 session23456、Worker40/40、前端14files/140tests及typecheck通过于 `e93b382`；`ef57213` no-store修复后重跑Service Release构建、配置HTTP28与完整离线十项，未重复完整harness/Worker/前端单测。

三类备份在Raw前及下载防御脱敏，覆盖完整/截断PEM和敏感行，backend9/9；全机诊断沿用保存前/下载防御脱敏及Site/任务归属/完整性/撤权，helper30/30/evidence96/96 PASS。Profile自动取密码，无普通DPAPI中心。

ef57213修复下载no-store hook，精确SHA离线三类下载已验证缓存头/脱敏。历史/同类diff/下载不重连、不采集；配置及全机诊断正文不发布普通Facts。AC单条403清空旧证据，同名组来源隔离。

Web仍本机，跨机TLS/真实设备/目标机待验收。仅合成回环，真实配置写入0，旧数据根未访问/复制。

专项：[AC 资源](../H3C_AC_RESOURCES_ACCEPTANCE_20261106.md)、[三类备份](../CONFIG_BACKUP_KINDS_ACCEPTANCE_20261106.md)、[全机诊断](../H3C_FULL_DIAGNOSTIC_ACCEPTANCE_20261106.md)。最新结构化报告位置：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-ef57213-20261107.json`。当前总入口为 [运行状态](AUTONOMOUS_RUN_STATE.json)、[测试矩阵](COMPLETE_TEST_MATRIX.md)、[最终交接](FINAL_HANDOVER.md)。


## 2026-11-06 及更早记录（历史）

以下旧检查点、任务状态、测试数量、候选路径及“当前/最终”均保留当时证据范围；最新结果和未完成项以上方 2026-11-07 章节为准。

日期：2026-11-06。状态：PASS（本机合同与定向回归）。

普通设备、ZTE、Linux 和 Site Jump 密码按所属记录自动读取，不建设 DPAPI 凭据中心。SQLite 密码字段为明文，不能声称数据库加密；Catalog 创建 token/数据库前收紧 Next 数据根 ACL。Windows 授权当前运行账号、SYSTEM、Administrators，Linux 目录为 `0700`；已有显式文件 ACL 不递归重写。

密码可由用户在表单输入，经请求及本机 worker 管道传递；普通 DTO 不返回已保存明文，URL、命令行、日志、Raw、终端录制和报告不回显密码。空白更新保留旧密码，明确清除才删除。Host Key 与密码分开保存，认证成功后自动首次信任和轮换。

Site scope snapshot 与当前 claims 取交集，任务、文件、下载和终端操作由服务端复核。Linux HTTP/WebSocket 已覆盖 Site 隔离、活动和静默会话撤权；网络终端及 Jump 回归覆盖作用域、取消、并发与释放。新增设备和 Profile 同事务提交，冲突不遗留半条库存记录。

服务仍默认仅 loopback。跨机器 Web/TLS 尚未完成；本机认证与回环 SSH 测试不替代远程部署或真实主机验收。
