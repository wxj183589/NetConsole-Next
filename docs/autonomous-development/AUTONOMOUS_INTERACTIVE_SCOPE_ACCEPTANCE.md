# 交互会话与全局文件权限增量验收（Checkpoint H）

日期：2026-11-05。离线实现、实际最小SQLite与浏览器fixture验收通过；完整HTTP Gate仍受现有Service互斥锁阻断。

WebSocket每次输入/输出边界重新读取当前session、账号角色及Site claims。降权、禁用、退出或过期不能继续使用握手时缓存的SUPERADMIN身份。检查不更新session last_seen，不把审计记录作为权限Authority，也不擅自关闭其他观看者可能共用的底层终端。

机器级Raw、录屏、导出、备份文件根没有逐Site归属，原通用文件API允许Site用户列出这些根，是实际隔离缺口。七条文件路由现在共用当前全局管理员门禁；Site用户继续使用已有带Site授权的业务导出。ZIP在接收、排队启动、下一个文件及发布边界复核创建会话；撤权后不发布下载、不暴露任务元数据，并退役临时输出。已接受的单个有界文件操作可以结束。

文件UI显示权限/会话失效状态，清空旧目录、文件、任务、选择及本地路径缓存；请求代数保证撤权前晚到的200响应不能恢复旧元数据。未授权初始页面不请求全局文件根。

验证：InteractiveScopeTests六项、后端完整harness通过；Python Web PR2静态契约通过，HTTP E2E仅完成语法检查（未宣称运行通过）。原HTTP E2E的全局文件正向断言保留，另补七路Site ADMIN拒绝断言。浏览器共15种状态通过（无线11、文件4），包含撤权后晚到响应、401/403、跨Site初始拒绝及全局用户正向列表；page errors/unexpected requests/routes均为空。vue-tsc、Vitest五项、Vite构建通过。生产数据及schema零修改。

复查发现后续仍有可安全推进的工作：A23真实Raw到facts发布缺口；A24旧回放回退Current及同revision覆盖；A25事实采集时Site归属缺失。它们进入动态任务池，当前任务仍在推进，不能再将全部剩余项概括为外部阻断。
