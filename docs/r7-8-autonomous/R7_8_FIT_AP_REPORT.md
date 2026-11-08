# R7.8 FIT-AP 原生管理状态

日期：2026-11-09。状态：**BLOCKED，原生认证与身份尚未证明**。

当前模拟器 catalog 只读元数据查询发现 `192.168.56.4:23` 的 4 个 Telnet 档案均没有已存密码；本次 shell 对对应引用也没有环境变量凭据。未读取密码内容，未检查运行 Service 的独立进程环境，不由此断言所有环境均无凭据。证据：`reports/r78-device-credential-availability.json`。

此前 TCP/Telnet 协商和 Password 提示仅证明服务可达，没有原生认证成功证据。用户提供的 AC 侧前提为：

```text
sys
probe
wlan ap-execute all exec-console enable
```

历史命令被接受不证明 FIT 远程登录已生效。本轮未重发命令、未保存、未尝试密码或重复失败认证。后续需要有效现有凭据与原生登录，然后从 FIT 原生输出读取 MAC/Serial，与 AC 侧记录精确核对；不得用 AC 观察代替 FIT 原生身份。

本轮 Comware `more` 完整读取修复覆盖官方 SSH/Telnet 离线循环；它不是 FIT 实机认证、采集或长稳通过。
