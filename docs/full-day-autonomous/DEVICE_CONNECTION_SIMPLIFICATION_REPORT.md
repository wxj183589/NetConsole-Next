# 设备连接简化报告

状态：PASS（本地/API/UI）；设备列表新增“测试连接”，直接使用已保存 profile 与服务端 credential reference，返回友好状态和版本摘要。新增设备的复杂 profile/凭据仍由服务端引用管理；浏览器不接触密码。四台模拟器认证因凭据缺失 BLOCKED。
