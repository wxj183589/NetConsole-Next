# Phase 9 H3C 采集 parity

## 已迁移/重新实现

- `display version`：Comware 版本和 Release 识别。
- `display interface brief`：route/bridge 两段、V7/V9 说明行过滤、UP/DOWN、IP、Speed、Duplex、Port mode、PVID 和 Description。
- Phase 9 离线命令：`display device`、`display wlan ap all`、`display wlan ap all address`、`display wlan ap all radio`、LLDP list/verbose、`display transceiver diagnosis interface`。
- 命令输出状态：`PARSED`、`EMPTY`、`PARTIAL`、`UNRECOGNIZED`；分页、权限不足、不支持命令和未闭合提示符不会伪装成成功。
- 角括号提示符（例如 `<CLD-PIS1332>`）、ANSI、退格和命令 Echo 分离已纳入 C# 回归。

## 离线证据

| 样本 | 结果 |
| --- | --- |
| `display_version.txt` | Comware `7.1.070` / `6607P20` |
| `display_version_v5.txt` | V5 fallback 样本，保留未识别字段边界 |
| `phase9_wlan_ap_all*.txt` | AP、地址、Radio 各 2 行 |
| `phase9_lldp_list.txt` | 本地/远端端口和管理地址 |
| `phase9_transceiver.txt` | RX/TX、温度、电压和状态，缺值保持 null |

## 尚未达到 parity 的部分

Next 尚未把 AC/FIT-AP、Carborne AP、MESH、Rail Reference 的采集任务接入 SSH runtime，也没有把这些 parser 结果写入 Next 领域 schema、HTTP API 和 GUI。真实 H3C V7/V9、AC、FIT-AP 和 MESH 仍未在本轮执行；Comware V5 仍暂缓。
