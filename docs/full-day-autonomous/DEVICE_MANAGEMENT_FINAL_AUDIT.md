# 设备管理最终审计

日期：2026-11-06。状态：PASS（本机服务/API/UI），实机兼容 `REAL_DEVICE_PENDING`。

设备 UUID、Site claims、分组、CSV preview/commit、批量操作、FIT-AP 独立创建限制和终端 Profile 关联保留。快速添加填写用户名/密码及设备参数，库存/Profile 在同一事务保存；冲突/失败不会留下半条设备。后续连接自动读取所属 Profile，普通 DTO 不返回已保存密码。

H3C/ZTE 采集、测试连接和网络 Web Shell 共用 Netmiko 驱动与认证策略。ZTE 页面已覆盖新增、密码更新/留空保留/明确清除、采集操作。Site Jump 由 Site 设置统一管理，FIT-AP 由 AC 管理。当前 HCL AC1/AC2/SW 只读命令 PASS，不等于实际库存或全业务兼容。

当前 Service harness、Vue typecheck、8 files/23 tests、原子 onboarding 和 ZTE HTTP/组件回归 PASS。没有修改真实设备库存、写入设备配置或访问旧 NetConsole 数据根。
