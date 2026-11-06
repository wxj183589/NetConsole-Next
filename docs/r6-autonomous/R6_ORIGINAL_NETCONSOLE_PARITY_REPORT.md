# R6 原版 NetConsole 对标报告

原版源码的只读盘点与 Next 映射见 `../r6-feature-parity/ORIGINAL_NETCONSOLE_FEATURE_INVENTORY.md`、`ORIGINAL_TO_NEXT_FEATURE_MATRIX.md` 和 `COMMAND_AND_DATA_PARITY.md`。盘点覆盖设备、无线、配置、诊断、任务、终端/跳板、拓扑、文件及系统治理入口。

总体判断：PARTIAL。Next 已有设备/Netmiko、任务、终端、配置备份和诊断等本地闭环；完整业务 UI、真实设备互操作及智能配置仍有缺口。旧版业务行为以源码盘点为准，不将旧架构或私有资产复制到 Next。
