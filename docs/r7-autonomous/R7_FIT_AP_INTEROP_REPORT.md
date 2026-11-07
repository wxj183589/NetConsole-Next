# R7.0 FIT-AP 与 H3C 能力完善

- 日期：2026-11-07；Agent：codex-A。
- 状态：**PARTIAL**。经 AC 管理的 FIT 身份与独立观察可用；直接 Telnet 认证 **BLOCKED / AUTH_CREDENTIAL_UNAVAILABLE**。

## 实际产品成果

新增 `H3cOperationalCapabilities.ProjectWireless`，为现有 AP Resource 输出：

- 身份状态与冲突；来源 AC 列表。
- 各 AC 的 AP Group 观察，不擅自合并不同组名。
- 原始 AP 状态、归一状态、捕获时间、Raw 引用和来源 revision。
- `AC_OBSERVATION_AVAILABLE` 与 `DirectAccessState=UNKNOWN` 分离；经 AC 成功采集不能冒充 FIT 直接登录成功。

复用既有 AP Resource、Observation、AP Group、Current/PreviousEffective/Recent10 与 Site claims 合同，没有新增模糊匹配或跨站身份推断。

新增 H3C 事实能力库按实际命令证据区分 `VERIFIED`、`STALE`、`FIXTURE`、`UNSUPPORTED`、`PARTIAL`、`EMPTY`、`FAILED`、`UNKNOWN`；输出只包含有限能力结论和证据引用，不返回原始配置或凭据。

## 模拟器实证

本轮新 SSH 探查两台 AC（证据目录 `reports\r70-device-20261107-233336`）：

- 两台都看到 AP `4c48-cb6e-0400`，型号 `WA6320-HCL`，相同序列身份。
- AC1 报告 R/M，AC2 报告 R/B，必须保留为两条独立设备观察。
- SW 新 LLDP 证据 `reports\r70-sw-checkpoint-20261107-234525\lldp-neighbors.txt`：GE1/0/2 连接该 FIT MAC；GE1/0/1 指向 AC1；GE1/0/3 指向 ac2。

这些结果证明 AC/SW 侧能观察该 AP，不证明 FIT 本地 Telnet 会话或空口客户端业务已验证。

## 直接 Telnet 阻断

授权端点 `192.168.56.4:23`，指定用户名 `h3capadmin`。

已有 R6.8/R6.9 诊断显示：TCP/Telnet 能取得 `Password:`，提交现有凭据后再次出现 Password 提示，没有取得 CLI prompt。未出现可用新凭据，因此本轮没有再次提交相同密码，没有猜测密码。

检查了当前 Netmiko `HPComwareTelnet` 的实际登录实现：它使用通用 `telnet_login`，遇到重复密码提示时可能在登录循环中再次提交相同凭据。自动重连不能作为本问题的安全修复；只调整超时也不能将认证失败变成成功。

可能原因仍需以可用认证凭据或设备控制台进一步核实。初始 Password-only 提示也不足以断言设备要求用户名；不能盲目发送用户名或更换身份。当前根因分类是“已有凭据未能建立 CLI，缺少已验证可用的直接访问凭据”，不是“端口不通”，也不是“Netmiko 完全不支持 Telnet”。

## 验证

- 专项 C# 测试 PASS：经 AC 的 FIT 身份、两个 AC 的独立角色/组信息、直接访问 UNKNOWN、claims revision 保留，以及受限空快照不补出隐藏设备。
- `tests/r70_device_capability_probe.py`：只读探查完成；FIT 记录 `attempted=false` 与认证阻断原因。
- Python 语法检查 PASS。
- FIT 直接登录、FIT 本机命令采集、FIT 配置写入、空口业务恢复：**NOT_TESTED**。
- 新 HTTP/UI 集成及最终浏览器验收由集成工作流记录于最终报告。

下一步：获得经确认的 FIT 直接访问凭据或控制台登录策略后，只执行一次有界认证验证，再由现有 Netmiko 驱动完成命令采集。经 AC 的业务管理可继续使用；不能把本地 Telnet 阻断升级为全部 FIT 管理阻断。

本工作流未写 FIT/AC 配置、未访问旧真实数据根、未执行远程 push/merge/release。提交由集成负责人统一完成。
