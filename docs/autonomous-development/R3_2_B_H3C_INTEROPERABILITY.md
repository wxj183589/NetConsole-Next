# R3.2-B H3C 设备互操作验收

更新时间：2026-11-15（Asia/Shanghai）

## 结论

```text
R3_2_B_STATUS=PARTIAL
H3C_AC1=PASS_DIRECT_USERNAME_PASSWORD_READONLY
H3C_AC2=PASS_DIRECT_USERNAME_PASSWORD_READONLY
H3C_SW=PASS_DIRECT_USERNAME_PASSWORD_READONLY
H3C_FIT_AP=PASS_PRIMARY_AC_ACTION_AND_PASSWORD_ONLY_TELNET_READONLY
H3C_FIT_AP_REMOTE_STATUS_FIELD=DISABLED_AFTER_RECHECK
H3C_FACT_PERSISTENCE=NOT_VERIFIED
AC1_ISOLATED_TEST_WRITE=1_EXEC_CONSOLE_ENABLE
GUESSED_CREDENTIALS=0
```

本轮没有再使用历史 `HCL_SIM_ADMIN`、Vault 或旧连接档案作为登录前置条件。SW、AC1、AC2 均使用用户提供的 `admin / admin` 直接登录；FIT-AP 使用用户提供的 AP 密码，并在 Telnet 密码模式下保持空用户名。

## 网络、认证与只读命令

| 设备 | 地址 | 协议 | 直接登录 | 只读结果 |
| --- | --- | --- | --- | --- |
| AC1 | `192.168.56.2:22` | SSH | PASS | Comware 识别并完成只读采集 |
| AC2 | `192.168.56.5:22` | SSH | PASS | Comware 识别并完成只读采集 |
| SW | `192.168.56.3:22` | SSH | PASS | `display version`、`display device`、`display interface brief`、LLDP 均成功 |
| FIT-AP | `192.168.56.4:23` | Telnet | PASS，Password-only，空用户名 | `display version` 成功 |

SW 的 SSH 分层探测先确认 TCP、SSH Banner、KEX、Host Key、Cipher 和 MAC 正常；随后独立 Netmiko worker 使用直接用户名密码认证成功，Host Key 指纹为 `SHA256:rK8bR0Y2fa79me+jYSqCooaqwURpq5ORm7sUnXmx0wo`。这证明本次失败不是 SSH 协商、Host Key 或旧 Vault 参数导致的。

FIT-AP 的 Telnet 分层探测确认设备实际返回 `Password:`，没有 Username 提示。第一次受控认证结果保留为失败证据；在主用 AC 侧完成授权的远程控制台动作后，第二次 worker 使用 `username=''`、用户提供的 AP 密码登录成功，并完成 `display version`。设备版本为 H3C Comware 7.1.064 Alpha 7165，设备提示符为 `<4c48-cb6e-0400>`。

## FIT-AP 主用 AC 与远程控制台

通过 AC/AP 实时只读状态确认：

- AC1 `192.168.56.2` 返回 `R/M`、`Backup type: Master`，是当前主用 AC；
- AC2 `192.168.56.5` 返回 `R/B`，是备 AC；
- AP `4c48-cb6e-0400` 的当前 AC 为 `192.168.56.2`，备 AC 为 `192.168.56.5`；
- AC1 变更前的 AP 详情显示 `Remote configuration : Disabled`。

在已授权的隔离模拟 AC1 上执行了：

```text
system-view
probe
wlan ap-execute all exec-console enable
```

随后也执行了按 AP 名称限定的尝试：

```text
wlan ap-execute name 4c48-cb6e-0400 exec-console enable
```

两次命令均返回 CLI 提示符且没有错误文本；复查的 `Remote configuration` 字段仍显示 `Disabled`。因此本轮准确结论是：`AC1_ISOLATED_TEST_WRITE=1_EXEC_CONSOLE_ENABLE`，状态字段没有形成“Enabled”确认；但动作之后 FIT-AP Password-only Telnet 已实际登录并完成只读命令。不能把状态字段写成已验证 Enabled，也不能把 FIT-AP 的错误归类为密码错误。

## 证据目录

- SW SSH pre-auth：`D:\study\NetConsole-Next-Workspace\reports\r3.2-blocker-20261115-003406\sw-ssh-preauth.json`；
- FIT-AP Telnet Password 提示：`D:\study\NetConsole-Next-Workspace\reports\r3.2-blocker-20261115-003443\fit-ap-telnet-preauth.json`；
- SW 直接用户名密码：`D:\study\NetConsole-Next-Workspace\reports\r3.2-direct-connection-20261115-004737\sw-direct-admin-auth.json`；
- AC/AP 主备归属：`D:\study\NetConsole-Next-Workspace\reports\r3.2-ac-ap-ownership-20261115-004807`；
- AC1 远程控制台变更前状态：`D:\study\NetConsole-Next-Workspace\reports\r3.2-ac-console-status-20261115-004850`；
- AC1 控制台动作与复查：`D:\study\NetConsole-Next-Workspace\reports\r3.2-ac-enable-console-20261115-0054`；
- FIT-AP 登录与 `display version`：`D:\study\NetConsole-Next-Workspace\reports\r3.2-fit-ap-post-console-20261115-0058\post-console-login.jsonl`。

以上报告不包含用户名、密码或密码内容；测试密码只在运行时以内存变量使用。

## 边界

以上是独立 worker 的真实设备证据，不等同于当前 NetConsole Service 已将输出写入 sealed Raw、H3C Facts 和无线投影，因此 `H3C_FACT_PERSISTENCE=NOT_VERIFIED`。本轮仅在隔离模拟 AC1 执行了用户明确授权的远程控制台配置动作，没有修改生产 AC、生产数据或真实开发数据，也没有执行保存、重启、切换或长稳测试。
