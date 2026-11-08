# R7.2 最终集成验收

2026-11-08，codex-A，branch `codex-A/netconsole-next-r3-dual-ac`。实际接续HEAD为 `065a749c4dc7e81280010afc1293de004ea895ff`，包含开始时已有R7.2未提交代码。

结论为 **PARTIAL / REAL_DEVICE_PENDING**。本地产品代码、API、Web、持久数据和浏览器集成已推进，完整设备运维闭环仍有明确环境与产品缺口。

## 已交付功能

- 完整恢复原件只读预检，单台/多台选择、独立两次读取、设备身份与完整配置hash、接口/LLDP基线、原生能力限制、阶段耗时和持久历史。运行中历史假中断已修复。
- schema27按稳定authority和捕获scope分区Current，保留旧事实ID、UNSCOPED与历史，空LLDP表只退役本分区。
- 真实八列表头Radio解析与旧五列兼容，保留Down及零客户端。设备详情精确LLDP邻居、来源/捕获范围筛选和CSV。
- 故障PROCESSING/REOPENED、范围统计、同authority/scope新鲜恢复证据，采集失败恢复收紧为相同完整命令计划、类型、冻结身份和端点。补Traceroute故障筛选。
- Quick Ping跨任务历史趋势与CSV，按设备、Site、执行人、协议及目标分别统计，RTT以已回复原始样本加权。
- LocalManager单入口启动、停止、重启、诊断、端口/容量/日志检查，受控进程归属和升级PlanOnly。实际SCM仍待。
- 完成原版25项功能迁移现状核对，保留具体业务缺口。

## 集成验收

Web339项、Worker49项、HTTP35检查通过。完整Service在Rebuild后退出0，覆盖故障21、恢复预检16、并发6、Worker崩溃回收、迁移故障注入及完整Raw生命周期。真实Service静态Web浏览器10场景通过，两个分辨率截图无横向溢出，API/页面/控制台错误均0。SQLite integrity ok，schema27，90段持久Raw逐段hash/字节/run/命令核验通过。Service受控重启后HEALTHY，正式Web和Vite入口均保留运行。

## 设备与操作

凌晨既有产品实证AC1/AC2各六条、SW两条Raw通过。本次接续AC1和SW产品任务TIMED_OUT，三台白名单SSH端口各检查一次均超时，因此本次未得到新成功设备Raw。FIT未猜密或重复被拒凭据。HA切换/回切未执行，完整恢复及故意失败回滚未执行，五项恢复PASS均未提升。此前只读基线结果与真实恢复执行分开记录。

SCM权限复核Win32 5，实际SCM生命周期未通过。未进行物理设备、外部Syslog、数小时长稳、多厂商和125%验收。Web构建保留大chunk警告。

## 交付与数据影响

只使用Next Workspace既有模拟器隔离根与最小fixture，实际catalog保留schema27及新增失败任务记录。旧NetConsole真实数据根未访问。沿用当前分支，无架构重建。DevStatus原有dirty保留，交接采用独立新文件。

详细证据见测试证据、设备互操作、配置恢复、双AC、Windows部署、原版迁移、事实来源和运行交接专项。九份要求的验收文档均已生成。

## 本地检查点

- 产品提交 `2157206ac54b99ec55617f51a57c8f80cf819fe9`。
- 最终文档采用后续本地提交，当前HEAD由 `git rev-parse HEAD` 获取。
- 工作树中先前生成、当前index未引用的untracked静态资源保留，未纳入本次产品提交。没有其他未提交产品源码。
- DevStatus采用 `tasks/netconsole-next-r72-closure-20261108.md` 独立交接文件与本地提交，原有 `current-status.md`、`handoff.md`、`tasks/doing.md` dirty完整保留。
- 两个仓库均仅本地提交，本轮Push、Merge、Release次数为0。
