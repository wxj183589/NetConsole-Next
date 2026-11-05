# 设备连接简化报告

状态：PARTIAL（本地/API/UI + 实时 AC1/AC2）；设备列表新增“测试连接”和“快速添加设备”，支持 SSH/Telnet/自动协商及默认端口。快速引导自动生成连接 Profile、启用 AUTO_UPDATE HostKey 策略并立即测试；只接收已保存的 Credential Reference，浏览器不接触密码。AC1/AC2 已自动登录并识别版本；SW 为认证后 Exec 兼容性问题，FIT-AP 为凭据拒绝。
