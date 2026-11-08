# R7.8 设备互操作与配置原始读取

日期：2026-11-09。设备工作流基线：`codex-A/netconsole-next-r3-dual-ac` / `47f9427`。本报告的产品改动为 Worker 配置读取完整性修复，不以离线测试替代设备验收。

## 已完成的产品改进

启动配置正文通过 `more flash:/startup.cfg` 等受服务端文件策略约束的命令采集。此前只有普通 Comware `display` 使用 R7.7 的完整行终止提示符读取；`more` 仍采用 Netmiko 默认非行锚定匹配。配置描述中合法出现 `description recovery prompt <AP.1(2)>` 时，官方 SSH/Telnet `send_command` 会提前返回，丢失后续接口、`return` 和最终提示符。默认 `find_prompt()` 失败时还可退回裸主机名；不能把所有默认读取都描述为裸主机名匹配。

`tools/netmiko_worker.py` 现在将 Comware `more` 复用到既有完整读取逻辑：官方 Netmiko 检测单行、有界、带括号的完整提示符；读取必须在完整行和响应终点匹配；提示符检测时间计入本条命令预算。残缺、变更提示符、超时和断连关闭会话，不重放命令、不返回完整 Raw。文件路径与命令授权仍由服务端现有策略负责。没有增加自建 SSH/Telnet 栈、文件传输或配置写入。

R7.7 已完成的 display 读取、双 AC 精确资源归并与独立观察保持复用；其它厂商与模式切换命令继续现有路径。

## 验证

新增测试先在旧 Worker 上运行，11 个测试方法中的新增场景出现 6 个失败子项；其中真实安装的官方 Netmiko SSH、Telnet 两种 `send_command` 分片循环均在配置描述行提前返回。修复后执行 `python tests/netmiko_worker_test.py`：**60 tests PASS**，0.397 秒。

新增 3 个测试方法覆盖：两传输对正文内提示符的抵抗与尾部完整保留；无终止提示符、内嵌提示符和变更提示符不发布且不重放；共享命令预算和非法提示符阻止发送。均为离线传输回归，未连接网络设备。

## 当前设备与恢复边界

只读查询当前模拟器 catalog 的凭据存在性元数据，未读取密码内容、未输出凭据引用/用户名、未建立设备会话。证据：`reports/r78-device-credential-availability.json`。AC1/AC2 各有 2 个已存凭据档案，SW 有 1 个，FIT 有 0 个；本次 shell 对这些引用无环境变量凭据。未检查运行 Service 的独立环境，因此 shell 无值不能证明 Service 无凭据。存在凭据不等于认证有效。

SW 历史已知认证失败；没有新有效凭据证明，不重复认证。FIT 原生认证与精确 MAC/Serial 仍待闭环。双 AC 历史只读通过不等于本轮新 Worker 部署验收或 HA。配置恢复仍需 SFTP/其它已批准传输、设备文件哈希、原生 replace、重连、回滚和业务证据；历史 `SFTP_SUBSYSTEM_UNAVAILABLE` 不以本次离线测试改写为通过。

候选集成需要主集成刷新 Worker 与离线依赖清单，执行候选级 Worker/重连门禁，再进行已授权 AC 产品只读采集。应读取启动配置正文并验证完整 Raw；不应为了验证本修复制造设备配置修改。

本工作流没有启动、停止或替换共享 Service/Vite，没有设备配置写入或认证提交，没有复制数据根、修改旧 NetConsole 数据、变更检查点或处理既有静态资源。提交与候选集成由主集成精确暂存完成，远端 push/merge/release 禁止。

## 最终主集成补充（2026-11-09）

主集成已将3396d25部署到含固定离线依赖的候选，Worker60、完整Service及捆绑运行时smoke通过。reports/r72-product-dual-ac-20261109-005924/summary.json中AC1/AC2各6条只读命令成功，新增12 Raw，164→176，最终逐长度/hash通过。无线命令不包含新的startup正文采集，不将more离线回归写成设备备份通过。SW/FIT未重试，无设备配置写入。最终运行和门禁以R7_8_RUNTIME_HANDOFF及FINAL_ACCEPTANCE为准。
