# 剩余阻断

1. FIT-AP 的 DPAPI 引用密码未被设备接受，不能猜测或穷举；因此 FIT-AP 实时采集仍 BLOCKED。SW 已认证但旧 Exec 通道无法完成设备信息命令，需单独处理命令兼容性。
2. 真实生产验收与发布仍未授权，不能用 fixture 证据替代。
3. 模拟器凭据可安全引用后，仍需执行四台设备认证、双 AC HA 切换/恢复和实时 AP 归属验收。

本轮收口验证中，独立启动的一次 API fixture 进程命中已有 Service Mutex；随后在无并发服务占用时重试通过。该阻断不改变产品代码，也不构成测试失败。

快速添加设备的本地/API/UI 流程已完成；隔离测试根中的 DPAPI 引用已用于 AC1/AC2/SW 自动登录测试。onboarding 不接收明文密码，继续保持凭据引用边界。

2026-11-05 自动登录补充：使用隔离测试根已有 DPAPI 引用逐台测试。AC1/AC2 返回 `CONNECTED` 且识别 H3C Comware 7.1.064；SW 返回认证成功但设备信息识别失败；FIT-AP 已知用户名 `h3capadmin` 仍返回认证失败。没有从哈希恢复密码、没有猜测默认口令、没有写入设备配置。详见 `docs/autonomous-development/AUTO_LOGIN_SIMULATOR_ACCEPTANCE_20261105.md`。
