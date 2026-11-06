# 安全与局点授权报告

日期：2026-11-06。状态：PASS（本机合同与定向回归）。

普通设备、ZTE、Linux 和 Site Jump 密码按所属记录自动读取，不建设 DPAPI 凭据中心。SQLite 密码字段为明文，不能声称数据库加密；Catalog 创建 token/数据库前收紧 Next 数据根 ACL。Windows 授权当前运行账号、SYSTEM、Administrators，Linux 目录为 `0700`；已有显式文件 ACL 不递归重写。

密码可由用户在表单输入，经请求及本机 worker 管道传递；普通 DTO 不返回已保存明文，URL、命令行、日志、Raw、终端录制和报告不回显密码。空白更新保留旧密码，明确清除才删除。Host Key 与密码分开保存，认证成功后自动首次信任和轮换。

Site scope snapshot 与当前 claims 取交集，任务、文件、下载和终端操作由服务端复核。Linux HTTP/WebSocket 已覆盖 Site 隔离、活动和静默会话撤权；网络终端及 Jump 回归覆盖作用域、取消、并发与释放。新增设备和 Profile 同事务提交，冲突不遗留半条库存记录。

服务仍默认仅 loopback。跨机器 Web/TLS 尚未完成；本机认证与回环 SSH 测试不替代远程部署或真实主机验收。
