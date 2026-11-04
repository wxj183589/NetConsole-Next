# 阻断登记

| ID | 任务 | 当前证据 | 解锁条件 |
| --- | --- | --- | --- |
| B01 | A12/A19 设备实测/共享组 | 四台TCP可达；3台SSH auth_none/本机密钥/Agent均未认证；已检查的12个Next测试catalog没有白名单凭据引用，两个标准vault位置不存在 | 对应安全密码或有效密钥；自动登录和四台联调已授权，无需再逐台审批 |
| B02 | A13 HTTP/Service-backed GUI | 实际运行r3_dual_ac_api_e2e.py，Service在SingleInstanceGuard报Another instance already running | 保留PID60784，需用户结束原预览后的维护窗口；不绕过Mutex、不结束用户进程 |
| B03 | A10/A11/A20 完整Current/Recent10、任务snapshot、持久身份/provenance | 现有schema不足以保留业务主体/actor snapshot/审计关联；独立方案见AUTONOMOUS_PERSISTENCE_PROPOSAL.md | 专项schema方案审查及真实数据应用授权；本轮未新增或执行migration |
| B04 | A15 原生125% | GetDpiForSystem=96，浏览器DPR=1；本轮无有效125%验收证据 | 有效原生125%环境；不修改系统缩放、不CSS模拟 |

其他可独立修复的高优先级缺陷已完成。B01不是授权不足，是设备明确要求的认证材料缺失；不会将端口可达或Fixture通过记为设备认证/HA/切换成功。
