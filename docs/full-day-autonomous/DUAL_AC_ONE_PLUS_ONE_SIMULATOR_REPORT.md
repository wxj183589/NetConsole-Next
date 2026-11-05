# 双 AC 1+1 模拟器报告

目标白名单：AC1 `192.168.56.2`、AC2 `192.168.56.5`、SW `192.168.56.3`、FIT-AP `192.168.56.4`。状态：BLOCKED。四台均取得 TCP 与协议 banner 可达证据；没有可用认证材料，因此未执行登录、配置写入、切换、恢复或 AP 归属宣称。默认 Next 数据根 `C:\ProgramData\NetConsoleNext\data` 没有 `credentials\h3c-credentials.v1.json`，`h3c_device_profiles` 为空，且 Next 服务未安装。未猜测密码，配置变化为 0。
