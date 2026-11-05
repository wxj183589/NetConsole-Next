# 模拟器自动登录验收（2026-11-05）

本次只访问用户授权的四个白名单地址，使用隔离测试根 `test-data/simulator-live-20261105` 中已有的 DPAPI Credential Reference。密码未进入命令行、源码、报告或日志；没有扫描白名单之外的地址，也没有执行配置写入、重启或主备切换。

| 设备 | 端点 | 结果 | 说明 |
| --- | --- | --- | --- |
| AC1 | `192.168.56.2:22` SSH | **PASS** | 自动凭据认证、Host Key AUTO_UPDATE、`display version` 成功；H3C Comware 7.1.064 |
| AC2 | `192.168.56.5:22` SSH | **PASS** | 自动凭据认证、Host Key AUTO_UPDATE、`display version` 成功；H3C Comware 7.1.064 |
| SW | `192.168.56.3:22` SSH | **PARTIAL** | SSH 密码认证和 Host Key 通过；旧 HCL Exec 通道在设备信息命令阶段断开/超时，结果为“连接成功，但设备信息识别失败” |
| FIT-AP | `192.168.56.4:23` Telnet | **BLOCKED** | 端口和 Telnet banner 可达；已按历史证据用 `h3capadmin` 建立隔离档案，但 DPAPI 引用对应的密码被设备拒绝。未猜测或重试密码 |

产品改动：一键测试的设备信息命令增加 15 秒独立超时；SSH 已完成认证但 Exec 通道异常时返回受控的 `CONNECTED`/识别失败结果，不再让 HTTP 请求变成 500。Telnet 登录检测到 `Login failed` 或重复密码提示时返回 `AUTHENTICATION_FAILED`，非请求取消的内部读取取消返回 `CONNECT_TIMEOUT`。

本轮没有进行双 AC HA 切换、配置变更、重启、AP 归属或生产验收。AC1/AC2 的登录成功不等同于双 AC 运行态已验证；SW 的连接成功不等同于命令采集成功；FIT-AP 仍需用户在本机安全录入正确密码后再测。

