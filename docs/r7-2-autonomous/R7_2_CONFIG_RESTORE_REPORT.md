# R7.2 完整配置恢复与恢复前检查

2026-11-08，codex-A，branch `codex-A/netconsole-next-r3-dual-ac`。续作基线 `065a749c4dc7e81280010afc1293de004ea895ff` 加既有 R7.2 工作区修改。本专项结论为 **PARTIAL / FULL_RESTORE_BLOCKED**。

## 已交付产品能力

完整历史原件以私有配置和独立脱敏副本保存，继续使用既有配置 Service、Netmiko Worker、设备级 gate、Site claims 和审计。新增恢复前检查 API 与单设备/多设备 UI，用户为每台设备选择一份已核验原件，最多 16 台，逐台显示结果、耗时、失败原因、阶段和哈希。

检查冻结整批目标身份后才启动网络读取。先验证历史原件的完整性与归属，再独立连接两次采集完整配置，核对型号、软件版本、配置 SHA-256、接口 Link/Protocol 与 LLDP 邻居基线，同时只读调查原生替换、SFTP 和检查点能力。历史原件可以与当前配置不同。两次独立读取之间出现配置或业务基线变化时明确失败。

逐设备记录持久化到本轮隔离数据根的 `backup/config-complete/<profile>/restore-preflight`。单设备失败保留其余设备的执行结果，取消、断点历史、目标漂移、排队期间权限撤销均有回归。记录保留至多 100 条非活跃元数据，进行中的记录受保留保护，完整配置原件遵循既有容量边界。

本次续作修复了历史查询将同进程进行中检查误标为 `INTERRUPTED` 的缺陷。运行中记录保持 `RUNNING`，UI 显示“正在检查设备”。新 Service 实例读到未完成的旧记录仍显示中断。活跃记录在完成或落盘失败后退出活跃集合。

恢复前检查回归同时接入 Service 正常全量入口，后续全量执行会运行此组，独立 `--restore-readiness-tests` 入口继续保留。

## 已有模拟器证据复核

证据来自本轮续作开始前已经保存的真实只读运行结果，本次核对原始报告，未重做设备写入或恢复。

| 项目 | 结果与证据 |
| --- | --- |
| SW 原生能力 | `reports/r72-restore-capability-20261108-032842.json`，型号 S6850，系统视图提供 `configuration replace file`，`display configuration commit list` 返回 `No commit point exists.`，SFTP 子系统不可用 |
| AC1/AC2 能力调查 | `reports/r72-restore-capability-20261108-032638.json` 保留两台设备系统视图下的文件替换帮助。AC 提供文件替换语法，不能据此推定提交点恢复或文件传输可用 |
| 初期失败证据 | `reports/r72-restore-capability-20261108-032500.json` 保留三台设备交互帮助超时结果，后续正确处理交互的调查单独记录 |
| SW 产品预检 | `reports/r72-restore-preflight-20261108-034110.json`，profile `c18dcb225dda4d059ce9799a71a28b2b`，batch `22ef59ab79c748d7b94f38e8eab44c98`，设备记录 `374914be0db6453aa7bb522df66f4dd3` |
| 完整独立读回 | 原件、首次与第二次完整读取 SHA-256 均为 `5c38013d4c0408bbe9a7104d8dd3eefbf75ac45f47590d71ec6d1cdd4550cbb0`，报告逐份核对完整原件字节数与哈希，耗时 8353 ms |
| 业务基线 | 两次独立接口/LLDP 读数一致，SHA-256 `b3aba345170b7e4ff5e2975c9f8c4986723b964d826af51d3e1325b3623c0d85`。该基线范围是接口 Link/Protocol 和 LLDP，不包含无线客户端业务流量 |
| 持久历史 | 上述预检报告 `historyContainsResult=true`，结果为 `READ_ONLY_PREFLIGHT_COMPLETE_RESTORE_BLOCKED` |

报告中的 `writes=false` 表示设备无配置写入。产品采集会在隔离 Next 数据根保存配置原件、脱敏副本与检查元数据。

## 实际恢复验收

| 验收项 | 当前结果 |
| --- | --- |
| WRITE_PASS | NOT_TESTED，未上传历史文件、未执行整机替换 |
| READBACK_PASS | NOT_TESTED，独立配置读取只发生在恢复前 |
| CONFIG_HASH_PASS | NOT_TESTED，只有恢复前原件与当前读数核验 |
| BUSINESS_PROBE_PASS | NOT_TESTED，未发生整机恢复后的业务验证 |
| ROLLBACK_PASS | NOT_TESTED，未执行整机失败注入和自动整机回滚 |

`writeAllowed`、`restorable` 和五项恢复 PASS 标志保持 false。设备帮助提供原生命令、两次读取稳定和原件完整性，分别保留为阶段证据。

## 尚未完成的实现与互操作

- 授权模拟器当前 SFTP 不可用，完整历史文件上传、设备 staging、长度及设备端哈希尚未验证。其他受控传输方式也尚未闭环。
- 文件替换确认交互与完整历史恢复执行路径仍未完成。当前 API 提供只读恢复前检查，多设备批量真正恢复仍未实现。
- SW 没有可枚举原生 commit point。已有 checkpoint 测试覆盖合成 transport，不代表该模拟器支持创建/恢复检查点。
- 任意完整恢复后的独立重连、全配置哈希核验、状态核验、故意失败与自动整机回滚仍待支持能力及恢复条件具备后验证。
- UI 的阶段/失败/耗时历史已经可用，实际整机恢复历史需在写入路径完成后扩展。浏览器及最终集成重启证据由主集成验收统一记录。

## 本次定向验证

| 检查 | 结果 |
| --- | --- |
| 恢复 readiness Service harness | 16/16 PASS，包含活跃历史查询、终态落盘、旧运行中断历史 |
| 原生 checkpoint 合成 harness | 14/14 PASS |
| SimulatorConfiguration harness | 19/19 PASS |
| 单设备恢复检查 UI | 7/7 PASS |
| 多设备恢复检查 UI | 6/6 PASS |
| SimulatorConfiguration UI | 12/12 PASS |
| 旧 `--config-restore-tests` | 独立 ArtifactsPath 首次FAIL，fixture找不到repository；主集成从仓库默认bin定向及完整harness均复验PASS |

本次构建使用 `build/r72-restore-closure` 独立输出，避免与主集成输出争用。定向 readiness 最终输出留在 `reports/r72-restore-closure-readiness.log`。数据仅为 Next Workspace 内最小 fixture，设备配置无本次修改，旧 NetConsole 真实数据根未访问。产品和 DevStatus 提交由主集成统一完成。
