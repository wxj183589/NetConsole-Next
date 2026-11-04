# R3 双 AC 主备切换与运行状态测试

## 预期拓扑

```ini
EXPECTED_TOPOLOGY=ONE_PLUS_ONE
TOPOLOGY_USER_CONFIRMED=YES
TOPOLOGY_RUNTIME_VERIFIED=EVIDENCE_PENDING
```

产品只在输入明确 HA/成员证据时返回：

```ini
TOPOLOGY_KIND=ONE_PLUS_ONE
TOPOLOGY_STATUS=CONFIRMED
```

否则保留 `UNKNOWN/EVIDENCE_PENDING`，不会因两个 AC 同时观察到同一 AP 自动升级。

## Fixture 状态映射

| 原始状态 | 规范化状态 | 结果 |
| --- | --- | --- |
| `R/M` | `RUNNING_MASTER` | `OFFLINE_FIXTURE_VERIFIED` |
| `R/B` | `RUNNING_BACKUP` | `OFFLINE_FIXTURE_VERIFIED` |
| `I` | `OFFLINE` 或 `UNKNOWN` 需结合 profile | `NOT_TESTED` |
| 采集失败/超时 | 不改写为 AP offline | `OFFLINE_FIXTURE_VERIFIED`（投影不覆盖来源） |

## 真实切换矩阵

| 场景 | 结果 |
| --- | --- |
| AC1、AC2 同时可达 | `NOT_TESTED` |
| AC1 不可达、AC2 可达 | `NOT_TESTED` |
| AC2 不可达、AC1 可达 | `NOT_TESTED` |
| 两台同时不可达 | `NOT_TESTED` |
| 切换前主备角色与时间 | `NOT_TESTED` |
| 受控主备切换 | `NOT_TESTED` |
| 切换后主备角色 | `NOT_TESTED` |
| FIT-AP 重新归属 | `NOT_TESTED` |
| AP 物理身份保持唯一 | `NOT_TESTED` |
| Observation 历史保留 | `NOT_TESTED` |
| 短暂 `UNKNOWN/STALE` 与恢复后的 Current | `NOT_TESTED` |
| 配置恢复 | `NOT_APPLICABLE_NO_CONFIG_CHANGE` |

未进行模拟器写配置、重启或切换，因此没有可报告的恢复动作，也没有把用户确认的主备关系写入真实设备。
