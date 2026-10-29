# Phase 9 ZTE 采集 parity

## 当前实现

`ZteCommandPolicy` 只读登记：`show version`、`show interface brief`、`show opticalinfo brief`、`show lldp neighbor brief`、`show lldp entry`。`ZteZxr10Parser` 已支持版本/型号、运行时长、接口状态列、光模块 `NO_MODULE/NO_LIGHT/NORMAL/ABNORMAL`、RX/TX nullable、LLDP wrapped remote port、TTL 和管理地址。

## 证据边界

`phase9_c89e4_*` 是脱敏 C89E-4 fixture；`phase9_hzdt10_*` 是脱敏文档/测试样本。原项目文档记载的 C89E-4 V1.9.0 真实验证不等于 Next 已真实连接，也不推广到其他 ZXR10 型号。

Next 当前没有 ZTE SSH session、任务类型、Raw sink、SQLite 当前/历史事实、HTTP API 或 GUI projection。因此 ZTE 状态为“离线 parser 已实现，运行时缺失”。

## 旧业务规则保留

- `offline` 清空模块身份和功率，状态为 `NO_MODULE`。
- `UNKNOWN` 或缺 RX 保持 `NO_LIGHT`，不把缺值写成 0。
- 接口物理 DOWN 与模块状态独立显示。
- 空结果/页码/不支持/权限失败以结构化状态返回；不得覆盖上一份有效当前事实。
- C89E-4 现场结论仅对已验证 Release 有效。
