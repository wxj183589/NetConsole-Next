# R7.1 FIT-AP 互操作实证

- 日期：2026-11-08；状态：**PARTIAL**。
- AC 管理观察：PASS；FIT 直接认证：`AUTH_CREDENTIAL_UNAVAILABLE`；无线客户端业务：NOT_TESTED。

## 实际结果

AC1 `.2:22`、AC2 `.5:22` 使用已有凭据与主机密钥 pin 完成 SSH 认证。两台独立观察同一 WA6320-HCL：完整 MAC `4c48-cb6e-0400`、serial `H3C_4C-48-CB-6E-04-00`、AP IP `.4`。两侧 Run、CAPWAP data-tunnel Up、Ready-for-Switchover Ready，AC1 Master、AC2 Backup，两个 Radio 均 Down。独立观察保持来源 AC、时间、Raw 和 revision，不以名字或模糊 MAC 合并身份。

FIT `.4:23` 仅做无凭据 Telnet 协商。`tests/r71_device_interop_probe.py::fit_handshake` 只发送 IAC DONT/WONT 协商响应，不提交用户名、密码或应用命令。实测 TCP 连通、8 次 Telnet 协商、414 字节应用 banner、Password 提示存在，无 Username 提示。报告只存计数和提示布尔值，不保存认证 banner。未再次尝试历史已拒绝的凭据，未猜测/枚举凭据。

证据：`D:\study\NetConsole-Next-Workspace\reports\r71-device-20261108-003716\summary.json` 和 `r71-ha-admission-20261108-004028`。

Password-only 提示说明目标 Telnet 登录形态，不能证明已认证，也不能推断 AC 的用户名/密码适用于 FIT。需要来源明确且适用于该 FIT 接入口的凭据后才能验证直接 CLI。

## 产品变化

无线能力面板保留 AC 管理状态和直接接入状态两个独立结论，显示两台 AC 角色、五分钟 freshness、Raw 引用和 revision。选择切换会失效前一次未完成请求；所选 AP 没有可见证据时显示空状态。

设备身份解析现在支持真实 HCL 型号/Alpha 版本；控制器角色通过产品有界任务采集并发布。AC 管理 AP 事实和控制器 Common 均不会将 FIT `directAccessState=UNKNOWN` 或 HA 未验证状态提升为成功。

## 验证及剩余项

- 无线能力组件 4/4 PASS；设备详情 13/13 PASS；相关任务/无线/H3C 页面 70/70 PASS；typecheck PASS。
- 两台 AC 的产品 HTTP identity+role 任务均 SUCCEEDED，证据见 `reports\r71-product-identity-20261108-005330`。
- FIT 未执行认证或配置写入；未执行无线客户端接入/漫游/业务恢复；未执行 AP 重启或 HA 故障注入。
- Radio Down 且未有可验证客户端，真实无线业务恢复仍需单独测试。TCP、Telnet 握手、AC 观察不能代替直接认证或业务互通 PASS。

数据影响仅为 Next 隔离测试根中的产品任务/Raw/事实及 Workspace 报告；未访问旧真实数据根，未创建完整数据根副本。提交、push 和最终状态由集成负责人统一记录。
