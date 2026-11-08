# R7.3 Windows SCM 与 ICMP

2026-11-08，codex-A，实际基线 `220c979050192eafce2e77613041c5bd89a845f9`。状态 **PARTIAL**。

## 新增产品入口

Service支持显式 `--icmp-diagnose`。入口位于SingleInstanceGuard、RuntimeSettings、Host与Catalog创建之前，不初始化数据根、不占服务互斥锁、不绑定Web端口。复用生产IcmpNetworkTracerouteProbe，固定127.0.0.1、TTL1、2秒探针与5秒取消上限。输出JSON包含精确程序路径、PID、.NET运行时、架构、IP结果、异常类型/HResult和逐层native错误码，失败退出1。

新增3组回归使用可注入probe验证真实结果到退出码的映射、固定回环和有界超时、DNS不调用、嵌套Win32错误码保留及无任意错误消息泄露。主线已注册 `--icmp-deployment-tests` 和原Service完整harness入口，独立ArtifactsPath执行3/3 PASS。

## 实际同 DLL 对照

独立Release构建位于Next Workspace `build/r73-deployment/bin/NetConsole.Next.Service/release`，0警告、0错误。`tests/icmp_host_diagnostics.ps1` 使用同一DLL、当前相同用户令牌、相同.NET 10.0.13 X64及相同生产probe，不启动Host，不访问数据，未修改安全配置。

| 方式 | 实际结果 |
| --- | --- |
| Service apphost EXE | FAILED，PingException内层Win32Exception，nativeErrorCode 5 |
| dotnet.exe + 相同DLL | PASS，127.0.0.1返回Success |
| DLL前后SHA256 | 相同 |
| EXE签名 / MOTW | NotSigned / 无Zone.Identifier |
| 精确EXE及dotnet应用防火墙过滤记录 | 查询未发现精确Program匹配，不涵盖全部WFP/第三方策略 |
| 当前管理员 | false |

证据为Next Workspace `reports/r73-icmp-host-diagnostics.json`。补充只读观察位于 `reports/r73-icmp-policy-observations.json`。WFP安全事件/CodeIntegrity日志查询未得到可用于定位的记录，记录查询异常类型。Defender和防火墙可读状态已封存，未修改任何开关、规则或ACL。

此结果确认差异与实际宿主EXE路径/进程身份相关，尚未定位具体阻断策略。不能从无精确应用规则或无MOTW推出完整防火墙、终端防护或WFP已经排除。候选EXE的原ICMP门禁继续FAIL/PENDING，dotnet成功未替代候选通过。

## SCM权限及新增验收脚本

17时本轮重新调用原生OpenSCManager，SC_MANAGER_CREATE_SERVICE仍返回Win32 5，无SCM变更。证据为Next Workspace `reports/r73-scm-permission.json`。实际安装、启动、升级、回退及卸载未在本轮执行。

新增 `tests/r73_scm_lifecycle.ps1` 使用现有install/update/uninstall入口，要求全新Next Workspace test-data根和三个独立版本目录。PlanOnly不创建数据根/报告或修改SCM。实际执行先拒绝非管理员、已有服务、活动交互实例互斥锁，避免干扰主线Service。

具备管理员隔离环境后脚本验证自动启动配置/虚拟账户、精确SCM PID与健康、同instance重启、受控自有PID崩溃及SCM恢复、故意失败升级及旧版本健康回退、成功升级、明确回滚到旧版本、卸载后catalog和sentinel保留。失败报告和数据根保留，cleanup仅作用于本脚本已安装的服务。

设备功能默认关闭，Worker进程回收和系统重启后自动启动仍明确NOT_TESTED。新脚本不把自动启动配置当作开机实测，不把健康HTTP当作Worker验收。原CI脚本已有DPAPI/token/ACL测试保留。

## 已执行测试

| 测试 | 数量与结果 | 环境 |
| --- | --- | --- |
| Service独立Release build | PASS，0警告/0错误 | 真实编译，无Host |
| ICMP诊断回归 | 3/3 PASS | 注入生产probe seam，固定回环/错误码/退出码，未执行网络 |
| 新SCM PlanOnly | 5/5 PASS | 微型exe文件fixture，未执行任何fixture EXE |
| 原update-service PlanOnly | 5/5 PASS | 微型fixture，无SCM变更 |
| LocalDeploymentDoctor | 13/13 PASS | 独立SQLite、HTTP fixture、真实worker导入/关闭 |
| Portable runtime边界 | 9/9 PASS | 端口/根/收据/容量/日志fixture，无产品Host启动 |
| apphost/dotnet回环对照 | PARTIAL，EXE失败/DLL宿主通过 | 实际生产probe，无模拟器 |
| 真SCM全生命周期 | NOT_TESTED / ACCESS_DENIED | 非管理员 |

主线Service与Vite未由本工作流停止。没有访问旧项目真实数据根，没有连接模拟器，没有Push/Merge/Release。ICMP诊断本轮仅回环流量。
