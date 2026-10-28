# 设备 CSV 导入规则

## 文件格式

UTF-8（允许 BOM），首行必须包含 `name,managementIp`。支持列：

`deviceId,name,managementIp,vendor,model,deviceType,siteId,groupName,description,enabled,terminalProfileId`

`siteId` 省略时使用 `local`；`deviceType` 省略时使用 `NETWORK_DEVICE`；`enabled` 支持空/`true`/`false`/`1`/`0`/`yes`/`no`/`启用`/`禁用`。不允许 `password`、`secret`、`privateKey`、`credential`、凭据引用等敏感列；未知列直接拒绝预览。

解析严格要求 Header 不重复、不为空，每行字段数量必须完全匹配 Header；支持 UTF-8 BOM、CRLF/LF、引号字段和 `""` 转义，拒绝非法闭引号、未闭合引号和超长字段。单次输入不超过 2 MiB、10,000 行、单字段 4,096 字符。

preview 绑定当前管理员，最多保留 8 个管理员 preview、全局最多 32 个，10 分钟过期。commit 在 SQLite Serializable transaction 中执行；更新使用 preview 时的 `updated_ms`，目标发生变化时整批冲突回滚。成功 commit 的短期 receipt 允许客户端安全重试，不会重复写入。

导入拒绝批次内重复 device ID、重复 Terminal Profile、ID/IP 错配和以 `=`, `+`, `@` 或公式样式 `-` 开头的值，避免后续导出到表格时形成 Formula Injection。

## 匹配和动作

1. 优先按显式 `deviceId` 匹配。
2. 没有显式 ID 时，按同一 `siteId` 下规范化后的 `managementIp` 匹配。
3. 唯一匹配为 `UPDATE`，无匹配为 `CREATE`。
4. ID 与 IP 指向不同记录、CSV 内同 site/IP 重复、目标分组/终端档案不存在或终端档案已被其他设备关联时，显示 `CONFLICT`。
5. 不使用模糊名称、跨局点 IP 或模糊 MAC 匹配；IPv4/IPv6 解析失败是错误。

## 提交和回滚

预览只生成短期 preview token，不写 SQLite。存在任何冲突/错误时不可提交。用户必须明确确认，服务端在单个 SQLite transaction 中完成全批 create/update、关联同步与审计；任一行失败，整个批次回滚。预览 token 成功提交后即失效。

CSV 不改变既有终端 profile 的 host/credential/Host Key 身份；管理 IP 变更只改变设备库存字段，并在详情中保留地址差异提示。
