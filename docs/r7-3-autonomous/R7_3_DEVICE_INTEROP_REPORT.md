# R7.3 设备互操作与环境诊断

2026-11-08，codex-A，基线 `220c979`。本次设备互操作 **BLOCKED**，环境调查完成。没有本次成功SSH/Telnet认证或新设备Raw。

## 实际检查

证据位于Next Workspace `reports/r73-initial/network-environment.json` 与 `network-probes.json`。

| 检查 | 本次结果 |
| --- | --- |
| Host-Only网卡 | VirtualBox Host-Only Network为Up，192.168.56.1/24，DHCP关闭 |
| 授权网段路由 | 192.168.56.0/24直连上述网卡，未由默认网关或VPN转发 |
| 默认路由 | 板载10.0.0.14，经10.0.0.254；未发现接管授权网段的VPN接口/路由 |
| 邻居解析 | AC1、AC2、SW为Unreachable，MAC全零；FIT同样无法建立TCP |
| 模拟器进程 | 初始未发现H3C Cloud Lab、VirtualBoxVM、VBoxHeadless、vmware-vmx或QEMU运行进程 |
| VirtualBox | 实际安装位于D盘，版本6.0.14；当前默认注册上下文的list runningvms与list vms均空。该默认上下文结果不能证明其他HCL自定义上下文不存在项目 |
| 安装状态 | HCL5.10.3、VirtualBox、VMware安装记录存在；安装记录不证明设备运行 |
| 防火墙 | Private/Public关闭，Domain开启，默认出站NotConfigured。没有修改任何配置，未据此断言所有过滤层放行 |
| 四目标TCP | `.2:22`、`.5:22`、`.3:22`、`.4:23`各一次、3秒有界并行连接，全部TCP_TIMEOUT |

## 原因与分类

本轮直接阻断位于认证前的TCP连接及同网段邻居解析阶段，同时没有运行中的模拟器虚拟机证据。当前结论为 **SIMULATOR_RUNTIME_UNAVAILABLE / TCP_TIMEOUT**。具体HCL项目与设备启动状态需要恢复模拟器上下文后确认，未自动启动未知拓扑或修改虚拟网络。

未发生AUTH_FAILED或CLI_TIMEOUT的新实证，也没有新证据证明WORKER_FAILURE。R7.2已记录的Worker连接超时与本轮宿主机直连超时一致。四目标连接检查后未继续重复SSH，不扫描其他地址，不提交FIT凭据。

## 保留与后续验收

R7.2凌晨AC1/AC2/SW的成功认证、Raw/Facts/LLDP与双源AP观测保留为历史结果。R7.3本地浏览器读取这些已存数据，不能将其计为本次模拟器互操作成功。

模拟器运行环境恢复后，使用现有产品Netmiko任务串行重采Version、Interface、WLAN/AP、LLDP和完整配置，逐段核验Raw、Facts、Current与浏览器。四台授权设备之外无本次网络活动。实际设备写入、整机恢复、回滚与HA切换均未执行。
