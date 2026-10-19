# NetConsole Next

下一代网络设备采集与诊断平台（开发中）。

> **优先级：系统稳定性 > 可靠采集和原始数据保全 > 工具集 > 实时分析 > 离线分析 > 报表。**

- 主要目标：低配置 Intel/AMD Windows 工业迷你主机。
- Windows 8/8.1、Server 2012/R2、Linux/ARM64 需逐平台验证，**暂不承诺兼容**。
- 后台采集独立于 Web UI 与本机管理器，SQLite + 原始 Raw 文件使用全新模型。
- 不读取或修改现有 NetConsole 的生产数据库；不开发独立 Agent Controller。
- 第一轮为 P0 技术与稳定性原型，代码、协议服务和安全能力未通过生产验收。

详见后续 `docs/` 设计与验证门禁。现有稳定版：[NetConsole](https://github.com/wxj183589/NetConsole)。
