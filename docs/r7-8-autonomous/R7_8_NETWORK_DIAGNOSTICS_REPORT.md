# R7.8 网络诊断
日期2026-11-09；状态PARTIAL。

## 本批产品行为
- 活动Long Ping按精确Site/device/协议/目标/端口保留窗口；3次连续有效丢失确认故障，同资源故障后3次新回复恢复；重复幂等、恢复后丢失重开。
- 暂停/正在暂停/取消不确认故障或恢复；续跑从ConfirmationStartSequence重建确认，累计统计保留。Quick/Long历史、RTT/P95、CSV沿用既有实现。
- DNS结果保留真实解析地址，IP字面量与DNS查询分开，避免把输入IP写成查询结果。
- 诊断设备服务端搜索和分页，跨页选择，运行中保留当前选择和结果；切账号清理旧状态。
- ICMP权限拒绝后提供明确TCP端口检查入口。必须用户选择端口并发起；不自动更换协议、不声称TCP逐跳Traceroute。

## 验证
Worker60、Web450、完整Service harness通过；网络诊断、健康26、故障30及暂停续跑边界见reports/r78-service-full-final.log。
reports/r78-live-ping-fault-bbce467c2b13/result.json：12项SERVICE_E2E_ACTUAL_LOOPBACK_TCP；真实自有监听器丢失→恢复→复发、暂停不采样、续跑不用旧样本，取消历史保持TCP。0设备写入，不代表模拟器或无线业务故障。
最终候选诊断浏览器两个视口通过：备份实际API读取，ICMP/TCP响应明确标注fixture，无设备命令；见UI报告。

## ICMP失败必须保留
reports/r78-closure-offline-dll.log与r78-handoff-offline-dll.log均EXIT1：Windows回环Traceroute为PARTIAL/ICMP_PERMISSION_DENIED；其余子门禁不抵消失败。
最终DLL --icmp-diagnose窄探针PASS，Host未启动/数据未访问；AppHost窄探针error5。不能把窄探针推广为完整Host通过，不能将DLL结果推广为SCM。当前尚未确定执行上下文/安全拦截的具体原因，没有修改全局安全或防火墙。
TCP逐跳Traceroute和吞吐尚缺产品实现；普通TCP连接检查不能代替。
