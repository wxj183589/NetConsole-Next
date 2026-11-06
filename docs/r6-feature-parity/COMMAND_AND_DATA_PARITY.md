# 命令与数据对标

旧版命令证据来自 H3C command profile、adapter 和 command guard；Next 命令执行经过 Netmiko worker、任务计划和 Raw/Facts/Current 发布链路。两者都保留原始回显与解析结果的区分。

| 能力 | 原版证据 | Next 证据 | 当前结论 |
| --- | --- | --- | --- |
| 版本/基础信息 | `display version` | `H3cComware.cs`、H3C 任务计划 | 本地回放通过，现场待验 |
| 接口 | `display interface brief/detail` | `H3cFacts.cs`、事实发布 | 本地回放通过 |
| LLDP | list/verbose 命令 | `H3cLldpTopology.cs` | 本地关系回归通过 |
| WLAN/AP | `display wlan ap ...` | `WirelessCollectionManifest.cs`、无线 projection | 多 AC/现场待验 |
| 配置 | `display current-configuration` | `ConfigBackupService.cs` | 备份本地通过，恢复写入关闭 |
| 原始数据 | task result/raw files | `RawSegmentWriter.cs`、task diagnostics | 保留 Raw provenance |
| 事实数据 | parser/repository facts | `H3cFactPublication.cs`、Current/History | 不把未解析回显伪装为事实 |

密码、token、私钥和连接凭据不能进入命令 URL、前端状态、日志或 Git。设备在线状态必须来自真实会话或采集证据；采集失败不能直接派生为 AP 离线。
