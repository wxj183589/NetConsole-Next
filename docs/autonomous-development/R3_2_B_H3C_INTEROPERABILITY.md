# R3.2-B H3C 设备互操作验收

更新时间：2026-11-15（Asia/Shanghai）

## 结论

```text
R3_2_B_STATUS=PARTIAL
H3C_AC1=PASS_READ_ONLY
H3C_AC2=PASS_READ_ONLY
H3C_SW=BLOCKED_AUTHENTICATION
H3C_FIT_AP=BLOCKED_CONNECTION_LOST
H3C_FACT_PERSISTENCE=NOT_VERIFIED
DEVICE_CONFIGURATION_WRITES=0
GUESSED_CREDENTIALS=0
```

## 网络与只读探测

四个目标 TCP 端口在本轮探测中均可达：

| 设备 | 地址 | 端口 | TCP | 只读 worker 结果 |
| --- | --- | ---: | --- | --- |
| AC1 | `192.168.56.2` | 22 | REACHABLE | 已认证，Comware 已识别 |
| AC2 | `192.168.56.5` | 22 | REACHABLE | 已认证，Comware 已识别 |
| SW | `192.168.56.3` | 22 | REACHABLE | `NetmikoAuthenticationException` |
| FIT-AP | `192.168.56.4` | 23 | REACHABLE | `CONNECTION_LOST` |

AC1 和 AC2 使用现有隔离候选中的凭据引用执行只读探测，没有打印或猜测密码。每台 AC 执行了 11 条命令，包括版本、设备、接口、LLDP、AP、AP 地址、AP 组、Mesh 和冗余信息。`display version` 均识别为 Comware；两台设备均明确返回以下能力限制：

- `display wlan mesh`：UNSUPPORTED；
- `display wlan mesh-link`：UNSUPPORTED；
- `display redundancy`：UNSUPPORTED。

证据目录：`D:\study\NetConsole-Next-Workspace\reports\r3.2-b-h3c-20261114-235450`，汇总为 `summary.json`，AC 原始只读输出为 `AC1-*.txt` / `AC2-*.txt`。

## 边界

本轮探测是独立只读 worker 证据，不等同于 NetConsole Service 已将这些输出写入本产品的 sealed Raw、H3C Facts 和无线投影。因此 parser/Facts persistence 保持 `NOT_VERIFIED`。SW 认证失败后没有尝试替代密码，FIT-AP 会话中断后没有重试配置命令；没有对任何设备执行配置、保存、重启、切换或 failover。

