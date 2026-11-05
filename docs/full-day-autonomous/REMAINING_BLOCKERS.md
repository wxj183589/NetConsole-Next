# 剩余阻断

1. 四台模拟器没有可用安全凭据，不能猜测或穷举；因此真实登录、双 AC 1+1、切换/恢复和实时 AP 归属保持 BLOCKED。
2. 已有 Service Mutex 阻断 HTTP/service-backed Gate；不终止用户服务、不绕过 Mutex。
3. 配置备份和完整模块工作台仍未完成；LLDP topology history 已完成离线/API/UI，但诊断批次的重启持久化仍待后续任务。
4. 真实生产验收与发布仍未授权，不能用 fixture 证据替代。
