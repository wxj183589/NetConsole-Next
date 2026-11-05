# 剩余阻断

1. 四台模拟器没有可用安全凭据，不能猜测或穷举；因此真实登录、双 AC 1+1、切换/恢复和实时 AP 归属保持 BLOCKED。
2. 真实生产验收与发布仍未授权，不能用 fixture 证据替代。
3. 模拟器凭据可安全引用后，仍需执行四台设备认证、双 AC HA 切换/恢复和实时 AP 归属验收。

本轮收口验证中，独立启动的一次 API fixture 进程命中已有 Service Mutex；随后在无并发服务占用时重试通过。该阻断不改变产品代码，也不构成测试失败。

快速添加设备的本地/API/UI 流程已完成，但四台模拟器仍未取得 DPAPI Credential Reference；因此 onboarding 可以创建受控 Profile 并返回 `CREDENTIAL_UNAVAILABLE`，不能宣称真实认证成功。

2026-11-05 凭据复核补充：确认四台地址由本机 HCL/VirtualBox 模拟器承载，项目配置和设备导出配置只含本地用户及单向认证哈希，没有可供 NetConsole 直接使用的明文凭据或 DPAPI vault。未尝试从哈希恢复密码，未猜测默认口令，未写入设备配置。继续等待通过本机掩码录入保存一个或多个 DPAPI Credential Reference 后，再执行四台连接测试。
