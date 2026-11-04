# 剩余缺口与阻断

## External

- 四台模拟器缺少可用凭据；自动登录授权已存在，但不得猜测、穷举、记录密码。TCP/协议可达不等于认证或 HA 已验证。
- HTTP/Service-backed Gate 受已有单实例 Mutex 影响；不终止用户服务、不绕过 Mutex。

## Contract / implementation

- 单命令与多命令 Raw 已可独立封存；多命令按设备批量子任务和跨 AC collection 仍需继续集成。
- 正式 AP identity/replacement、Domain/group 生命周期和多目标 Report 需要独立持久合同评审。
- 配置备份、拓扑变化历史和完整模块工作台仍待推进；诊断批次已完成当前进程内的有界调度/API/UI，重启后历史持久化仍未实现。

## 本轮新增证据

- 产品 HEAD=`b5380e0`；多命令 H3C fixture `H3C_TASK_FACT_PUBLICATION_TESTS=11/11 PASS`。
- 单命令/全量 Service harness 在本轮改动前后均通过；前端 `vue-tsc --noEmit` 与 Vitest `4 files / 7 tests` 通过。
- 设备列表已提供“测试连接”，服务端沿用 DPAPI 引用、自动 HostKey 与 Telnet 回退；不会在浏览器或日志接触密码。
- 网络诊断已提供 Ping/TCP/DNS/LLDP 批量任务；目标限定为当前 Site 可见的已登记设备，LLDP 仅读取带 captured scope 的 H3C Facts。

## Verification boundary

- 离线 fixture、Fake transport、隔离 SQLite 证据不得写成模拟器实时验收。
- 1920×1080 和 2560×1440 是本阶段 UI 视口；125% 不作为本阶段阻断。
