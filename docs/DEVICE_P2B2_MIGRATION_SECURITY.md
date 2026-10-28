# P2B-2 迁移安全边界

## 受保护的入口

`P2B2SchemaV13Migration.Apply` 首先执行 `P2B2MigrationContext.Validate`。入口同时要求：

1. `IsolatedTest=true`；
2. `NETCONSOLE_NEXT_P2B_V13_MIGRATION_ALLOWED=1`；
3. migration context、临时根目录和 open SQLite connection 都能被规范化为绝对路径；
4. migration path 位于临时根目录本身或其目录子树内；
5. migration path 与 open connection 的 `DataSource` 完全一致；
6. 临时根目录、数据库文件和其现有父级路径组件没有 `FileAttributes.ReparsePoint`。

任何一步失败都抛出 controlled `InvalidOperationException`，不会进入 schema 创建、backfill 或 version 写入。

## 路径威胁与处理

| 风险 | 防护 |
| --- | --- |
| `Temp2` 等相似前缀 | 目录边界比较要求完整目录分隔符，不使用裸 `StartsWith(tempRoot)` |
| 相对路径 | `Path.IsPathFullyQualified` 失败即拒绝 |
| `..` 逃逸 | 先规范化，再按临时根目录边界校验 |
| 其他盘符/UNC/生产路径 | 规范化后不在临时根目录内即拒绝 |
| symlink | 对数据库路径及所有现有父级读取 `ReparsePoint` |
| junction | junction 同样是 `ReparsePoint`，按同一规则拒绝 |
| 其他 reparse point | 任意现有路径组件命中 `ReparsePoint` 即拒绝 |
| 连接指向另一文件 | 规范化 context path 与 `connection.DataSource` 后比较 |
| 路径无法安全检查 | 权限检查异常转换为拒绝，不降级放行 |

数据库文件可以尚不存在；这不会绕过检查，因为会继续检查其现有父级路径。无法确认的文件系统路径不会被视为安全。

## 自动化用例

`P2B2MigrationPathSecurityTests` 使用临时 fixture，不复制或打开真实数据目录，并覆盖：

- 合法临时数据库；
- 相似前缀逃逸和 `..` 逃逸；
- symlink、junction 和一般 reparse-point；
- connection path mismatch；
- 生产路径；
- 缺少显式迁移 flag；
- 缺少 isolated context。

Windows 当前运行环境已获得创建 symlink 和 junction 的权限，两个逃逸测试均为 `PASS`。若后续环境没有相应权限，测试会分别输出 `SKIPPED_NO_SYMLINK_PERMISSION` 或 `SKIPPED_NO_JUNCTION_PERMISSION`，并保留 `MIGRATION_REPARSE_POINT_REJECTED` 的实际证据；这类环境限制不能被报告为安全测试通过。

## 不在本次授权内的事项

- 不允许从正常服务启动路径自动迁移 v12 数据库。
- 不允许指向 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`。
- 不允许通过 API、真实设备连接或 VLAN 规划触发迁移。
- 不允许把临时 fixture 的成功升级为生产迁移成功。
