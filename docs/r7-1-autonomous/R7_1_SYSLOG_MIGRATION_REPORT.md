# R7.1 Syslog 原版功能迁移

2026-11-08；原版仅只读源码对照，未运行原版或访问其真实数据根。迁移目标为原版 `ground_unattended/syslog_runtime.py` / `SyslogPage.vue` 中当前Next缺少的日志工作台。

## 已交付

`SyslogWorkspace.vue`提供接收状态、received/flushed/dropped/错误诊断、时间/IP/severity/文本过滤、原文及Raw引用、加载/空态/错误/部分结果和授权范围内的CSV。身份改变或KeepAlive停用会清除旧结果，迟到响应不会写入新身份界面。

`POST /api/system/syslog/query`只检索注册的本地Syslog来源和当前Site授权；no-store。检索最多8个segment、100000条记录、31天和500条返回，单段大小有界。读取前校验Raw路径、reparse、长度及SHA256；不存在/不完整/损坏返回显式状态，不将其变成完整空结果。CSV带BOM、转义公式单元格。

UDP worker在低流量下每5秒seal，原有8192队列、32MiB容量、100ms flush和4MiB分段合同保留。接收启用为loopback15514；不是远程开放Syslog服务。UDP不保证绝对不丢包。

## 验证与边界

- Service专项8/8，包括真实loopback低流量UDP在持续监听时seal；完整Service harness重跑EXIT0。
- Vue专项4/4，包含身份切换的迟到结果和接收状态失败清除旧值；Web整批291/291、typecheck、构建通过。
- 实际运行服务接收一条 `<134>R71_LOCAL_TEST browser CSV acceptance local fixture only`，received1/flushed1/dropped0；浏览器查询severity6/facility16并下载280bytes、1行CSV，Raw引用存在。该证据为本地合成日志，不是H3C设备发日志互操作。
- Chrome1920/2560截图及结果：Workspace `reports/r71-browser-syslog-udp-20261108`；API/page/console错误0，实际视觉检查无重叠/溢出。

原版长期日志规则、全部厂商事件解析、外部设备发日志、持续负载和长期保留策略未因此全部迁移。当前已交付可用的本地接收→Raw→授权查询→CSV业务链。

## 后续分类集成验收

增加五类确切 H3C 事件标识分类及 eventType 精确筛选，未知、多标识（包括重复同一标识）保持 UNKNOWN。分类不会推导设备身份、HA 或业务恢复。详见 `R7_1_SYSLOG_EVENT_CLASSIFICATION_REPORT.md`。

分类后的 Service 专项为原8组加6组，Vue专项6/6；第二批完整 Service harness EXIT0，Web当批27文件297项通过。后续最终门禁计数见 `R7_1_TEST_EVIDENCE.md`。

当前运行实例再经15514接收三条本机合成UDP：MESH_LINKUP、IFNET_PHY_UPDOWN及两个标识冲突。真实浏览器精确筛选、Raw引用和新增CSV字段通过，API/page/console错误0，1920/2560共6张截图复核无重叠或横向溢出。证据：Workspace `reports/r71-browser-syslog-classification-20261108`。这些是本地合成日志，仍不表示远端设备Syslog互操作或长稳通过。
