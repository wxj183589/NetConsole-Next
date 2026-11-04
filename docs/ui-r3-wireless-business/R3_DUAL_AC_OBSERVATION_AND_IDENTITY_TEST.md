# R3 双 AC Observation 与身份测试

## 测试合同

同一个 FIT-AP 在两个 AC 下出现时，必须分离：

```text
PHYSICAL_AP_IDENTITY
AC1_OBSERVATION
AC2_OBSERVATION
```

AP 名称、MAC 尾号和“最近一条输出”不能作为合并依据。Serial 与完整规范化 MAC 集合相同才允许形成同一个物理资源；冲突必须保留。

## 离线 fixture 结果

fixture：`tests/NetConsole.Next.Service.Tests/fixtures/r3_dual_ac_wireless.json`

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| AC1/AC2 设备库存 ID 分开 | `OFFLINE_FIXTURE_VERIFIED` | `ac-device-1`、`ac-device-2` |
| 同一 Serial + 完整 MAC 只生成一个物理 AP | `OFFLINE_FIXTURE_VERIFIED` | 1 个 `ap-resource` |
| Observation 保留两条 | `OFFLINE_FIXTURE_VERIFIED` | `sourceAcDeviceId` 分别为 AC1/AC2 |
| R/M 与 R/B 不互相覆盖 | `OFFLINE_FIXTURE_VERIFIED` | 两条 Observation 的 normalized state 独立 |
| 同 Serial 不同 MAC | `OFFLINE_FIXTURE_VERIFIED` | 两个 `CONFLICT` resource，不自动合并 |
| 同名 AP、无 Serial/MAC | `OFFLINE_FIXTURE_VERIFIED` | 两个 `UNKNOWN` resource，不按名称合并 |
| 同名 AP Group | `OFFLINE_FIXTURE_VERIFIED` | 两个 AC-owned group |

新增自动化入口：

```text
dotnet run --project tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -- --wireless-tests
WIRELESS_PROJECTION_TESTS=PASS
```

## 双 AC 专项矩阵（追加 ID，不覆盖 SC-01～SC-57）

| ID | 场景 | 状态 |
| --- | --- | --- |
| `DUAL-AC-01` | AC1 独立连接 | `NOT_TESTED` |
| `DUAL-AC-02` | AC2 独立连接 | `NOT_TESTED` |
| `DUAL-AC-03` | AC1/AC2 设备身份识别 | `NOT_TESTED` |
| `DUAL-AC-04` | AC1/AC2 稳定 Device ID | `OFFLINE_FIXTURE_VERIFIED` |
| `DUAL-AC-05` | AC1/AC2 Domain 身份 | `OFFLINE_FIXTURE_VERIFIED` |
| `DUAL-AC-06` | 同一 FIT-AP 双 AC Observation | `OFFLINE_FIXTURE_VERIFIED` |
| `DUAL-AC-07` | Serial/MAC 跨 AC 去重 | `OFFLINE_FIXTURE_VERIFIED` |
| `DUAL-AC-08` | 两个 AC 的 AP Group 差异 | `OFFLINE_FIXTURE_VERIFIED` |
| `DUAL-AC-09` | 两个 AC 的运行状态差异 | `OFFLINE_FIXTURE_VERIFIED` |
| `DUAL-AC-10` | R/M 与 R/B | `OFFLINE_FIXTURE_VERIFIED` |
| `DUAL-AC-11` | AC1 不可达时 AC2 状态 | `NOT_TESTED` |
| `DUAL-AC-12` | AC2 不可达时 AC1 状态 | `NOT_TESTED` |
| `DUAL-AC-13` | 两个 AC 同时可达 | `NOT_TESTED` |
| `DUAL-AC-14` | 两个 AC 同时不可达 | `NOT_TESTED` |
| `DUAL-AC-15` | 主备切换过程 | `NOT_TESTED` |
| `DUAL-AC-16` | 切换后 Observation/Current 更新 | `NOT_TESTED` |
| `DUAL-AC-17` | 多 AC 状态冲突与跨 Site 部分可见性 | `NOT_TESTED` |
| `DUAL-AC-18` | AC1/AC2 权限隔离与 GUI | `BLOCKED` |

`DUAL-AC-01`～`DUAL-AC-18` 是本轮新增唯一 ID；原有 `SC-01`～`SC-57` 保留，未覆盖或重新计数。

## 真实模拟器结果

```ini
AC1_AUTH_STATUS=NOT_TESTED
AC2_AUTH_STATUS=NOT_TESTED
AP_SERIAL_MAC_IDENTITY=NOT_TESTED
AC1_OBSERVATION=NOT_TESTED
AC2_OBSERVATION=NOT_TESTED
PHYSICAL_AP_IDENTITY=NOT_TESTED
```

目前仅有四个白名单 TCP 端口 `PASS`，没有安全凭据、CLI 输出或 parser 输入证据。不能将离线 fixture 结果提升为真实模拟器结果。
