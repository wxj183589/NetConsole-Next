# R7.7 设备互操作与完整读取报告

## 最终候选新增实采（取代下文待集成状态）

状态：**PASS（SIMULATOR_READONLY，仅双AC）**。候选build/r77-candidate-api已部署，Service18771/PID21540；使用候选内2ad4698 Worker与离线依赖。实际SSH认证并通过产品任务采集AC1(.2:22)、AC2(.5:22)各6条display命令，任务均SUCCEEDED，12条Raw长度/segment SHA/output SHA与Facts范围校验通过。

证据：reports/r72-product-dual-ac-20261108-233229/summary.json；采集窗口UTC15:32:29–15:32:41。run AC1=111e3284608f446b92fbbe6a186c8f51，AC2=257b53bdc30c442d9e5e728856367295。双AC同一精确MAC/Serial形成一个资源、两个独立观察，AC1 R/M、AC2 R/B。不是HA切换验收。SW没有重复失败认证，FIT没有猜测凭据，配置写入0。Worker57/57、最终候选显式DLL完整离线门禁PASS；APPHOST权限失败独立保留。

## 此前工作流记录

日期：2026-11-08。Agent：codex-A。工作分支：`codex-A/netconsole-next-r3-dual-ac`。本报告记录设备工作流的产品改动；运行候选与真实 AC 的新增验收由主集成补充，不以单元测试代替设备验证。

## 产品改动

`tools/netmiko_worker.py` 的普通 Comware `display` 读取现在通过官方 Netmiko `find_prompt()` 获取带 `<...>` 或 `[...]` 的完整提示符，并以完整行和返回文本终点匹配。原来默认依赖去掉括号的 `base_prompt`，正文中的同名设备字符串可能导致提前停止读取；这会把部分版本、接口或 AP 输出误认为完整。

SSH `hp_comware` 与 Telnet `hp_comware_telnet` 共用上述逻辑。提示符只接受单行、有长度上限且括号完整的 Comware 形式；检测提示符消耗的时间从本条命令的读取预算中扣除。收到无终止提示符、变更提示符、超时或断连时不发布完整结果，并沿用现有会话关闭及错误分类，不自动重放命令。Raw 保留命令回显和终止提示符。

修改仅覆盖普通 `display` 读取。既有诊断确认交互、模式切换命令和其它厂商驱动继续原有路径，避免把进入 system-view 后的合法提示符变化当作失败。Netmiko 继续负责 SSH/Telnet 登录、终端准备、分页与通道读取；未引入自建 CLI 协议栈。

## 验证

`python tests/netmiko_worker_test.py`：**57 tests PASS**。日志：`D:\study\NetConsole-Next-Workspace\reports\r77-device-worker-unit.log`。新增 8 项测试覆盖双传输、Raw 保留、大小写及空白、非法/超长/多行提示符、检测超预算、部分结果、模式变化、超时断连不重放，以及非 display/其它驱动兼容。

其中分片读取测试直接使用已安装的官方 Netmiko SSH/Telnet 实例（`auto_connect=False`），把带正文设备名的三段输出交给真正的 `send_command` 读取循环，确认一直读取到 `<AP.1(2)>`。测试未连接任何网络设备。这是传输解析回归证据，不是实际设备互操作通过。

## 真实设备证据边界与集成事项

此前 [R7.6 设备报告](../r7-6-autonomous/R7_6_DEVICE_INTEROP_REPORT.md) 已记录双 AC 产品只读采集、12 条 Raw/Facts 校验，以及 SW 已知认证失败。该历史证据不等于本次 Worker 修改已经部署或经过新的设备验收。设备工作流本批没有建立设备会话、提交任何凭据、配置写入或 HA 操作。

主集成须把新 Worker 放入实际运行候选并刷新离线依赖/Worker 清单，然后完成候选级 HTTP/Worker 重连测试及双 AC 定向读取；当前运行的 R7.6 候选不能被描述为已经包含本修复。SW 继续等待有效既有凭据，不重试已知失败配置。FIT 原生管理边界见 [FIT-AP 报告](R7_7_FIT_AP_REPORT.md)。

本批没有启动、停止或替换共享 Service/Vite，没有访问旧 NetConsole 数据根，没有复制或修改真实数据，没有触碰 19 个既有未跟踪静态资源。改动文件为 Worker、Worker 测试及本报告和 FIT 报告；本地提交由主集成精确暂存完成，不执行远端 push、merge 或 release。
