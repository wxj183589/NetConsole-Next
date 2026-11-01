# AC/AP 身份模型

状态：设计稿。以下表为后续实现的规范草案，不代表本轮已经创建或迁移数据库表。

## 1. 身份原则

- AC 的唯一身份是设备库存 `device_inventory.id`。厂商、型号、管理 IP 和名称是属性或候选匹配字段，不是主键。
- FIT-AP 的业务身份是 `fit_ap_id`。序列号和规范化主 MAC 是外部身份证据；不能用 AP 组、名称、IP 尾段或 AC 行号替代。
- 同一 AP 被多个 AC 看到时，保留多个 `wireless_ap_observation`，由身份协调器把它们指向一个 FIT-AP；来源不会被覆盖。
- 任何关键身份字段冲突都要生成可审计的 `CONFLICT/QUARANTINED` 状态，等待人工或受控规则确认。

## 2. 规范化规则

### 2.1 序列号

去除首尾空白，保留大小写无关的比较键；原始值仍保存在观测记录中。空字符串按 `NULL` 处理。序列号重复时不能静默选择“最新”或“第一条”。

### 2.2 MAC

接受冒号、短横线、点号和连续十六进制形式，转换为小写、12 位十六进制的规范键，例如 `aa:bb:cc:dd:ee:ff` → `aabbccddeeff`。非法 MAC 不参与身份合并并产生数据质量问题。主 MAC 只作为已信任的身份证据；无线接口 MAC 可作为关联线索，但不能单独覆盖主身份。

## 3. 逻辑实体

### 3.1 `wireless_ac_binding`

| 字段 | 约束 | 含义 |
| --- | --- | --- |
| `ac_device_id` | PK/FK → `device_inventory.id` | AC 设备库存身份 |
| `site_id` | FK → `sites.id`，不可空 | 业务范围，必须与设备库存一致 |
| `mode` | `INDEPENDENT` / `STACK_MEMBER` / `CLUSTER` / `VIRTUAL_PRIMARY_STANDBY` | AC 运行形态；缺少证据时使用 `INDEPENDENT` |
| `cluster_id` | 可空 | 集群/主备关系的稳定关系 ID，不是设备 ID |
| `status` | `ACTIVE` / `DISABLED` / `UNRESOLVED` | 绑定状态 |
| `source`、`source_revision` | 不可空 | 能力/关系来源及版本 |
| `created_at`、`updated_at` | 不可空 | 审计时间 |

`site_id` 与设备库存的 Site 不一致时拒绝写入。堆叠成员仍保留独立 `ac_device_id`，不得把多台物理设备压成一个设备库存行。

### 3.2 `fit_ap_identity`

| 字段 | 约束 | 含义 |
| --- | --- | --- |
| `id` | PK | NetConsole 内部 FIT-AP 身份 |
| `site_id` | FK → `sites.id` | 归属 Site；跨 Site 迁移需要显式操作和审计 |
| `serial_number` | 可空；非空有效身份全局唯一 | 厂商序列号 |
| `primary_mac` | 可空；非空有效身份全局唯一 | 规范化主 MAC |
| `identity_status` | `ACTIVE` / `REPLACED` / `CONFLICT` / `UNRESOLVED` | 身份生命周期 |
| `replacement_of` | 可空，自引用 FK | AP 替换链；新 AP 不能复用旧 ID |
| `created_at`、`updated_at` | 不可空 | 审计时间 |
| `confirmed_by` | 可空 | 人工确认者或受控系统动作 |

建议数据库使用“非空且不处于冲突”的部分唯一索引，避免把冲突记录丢失：

```sql
UNIQUE (serial_number) WHERE serial_number IS NOT NULL AND identity_status <> 'CONFLICT';
UNIQUE (primary_mac) WHERE primary_mac IS NOT NULL AND identity_status <> 'CONFLICT';
```

如果目标数据库不支持该形式，应使用独立身份候选表和事务内唯一性检查实现同等语义。

### 3.3 `fit_ap_interface_mac`

记录 FIT-AP 已确认或观测到的接口 MAC：`fit_ap_id`、`mac`、`role`（`PRIMARY/RADIO/UPLINK/MANAGEMENT/UNKNOWN`）、`source`、`observed_at`。只有 `PRIMARY` 且经过信任校验的值参与主身份协调；接口 MAC 不作全局唯一要求。

### 3.4 `wireless_ap_observation`

每一次 AC 采集结果都保留：

| 字段 | 含义 |
| --- | --- |
| `id` | 观测 ID |
| `fit_ap_id` | 可空；身份未解析前为空 |
| `ac_device_id`、`site_id` | 来源 AC 与权限边界 |
| `ap_group_id` | 可空，来源 AC 内的组 |
| `reported_serial`、`reported_primary_mac` | 原始身份字段 |
| `state_raw` | 厂商原始状态，不做丢失性覆盖 |
| `state_semantics_version` | 状态码解释版本；未验证时使用 `ASSUMPTION_PENDING_H3C_EVIDENCE` |
| `observed_at`、`expires_at` | 新鲜度边界 |
| `source_revision` | 同一 AC 结果的修订/采集版本 |
| `identity_match_kind` | `SERIAL` / `PRIMARY_MAC` / `MANUAL` / `UNRESOLVED` |
| `conflict_state` | `NONE` / `IDENTITY_CONFLICT` / `SITE_CONFLICT` / `QUARANTINED` |
| `payload_ref` | 指向受保护的原始负载，禁止把凭据放入负载 |

推荐唯一键为 `(ac_device_id, source_revision, reported_serial, reported_primary_mac)` 的幂等约束（空值按规范化后处理），并保留原始采集时间，避免重复提交产生重复观测。

### 3.5 `wireless_ap_group`

字段为 `id`、`ac_device_id`、`site_id`、`name`、`source_revision`、时间戳。唯一性为 `(ac_device_id, normalized_name)`；组名只在单个 AC 内有意义，不能跨 AC 推导 AP 身份。

### 3.6 `wireless_ap_group_membership`

记录 `observation_id` 或 `fit_ap_id`、`group_id`、来源修订、有效时间和冲突状态。当前组应由最新可信观测投影得到，历史组关系不可被覆盖删除。

### 3.7 `wireless_ac_peer`

可选关系表保存 AC 集群/主备成员：`cluster_id`、`left_ac_device_id`、`right_ac_device_id`、`relation_role`、`source_revision`、`observed_at`、`confidence`。关系表不改变任一 AC 的设备库存身份，也不授权跨 Site 读取。

## 4. 匹配优先级

1. 规范化后序列号与一个有效身份精确匹配；
2. 无序列号时，使用已信任的主 MAC 精确匹配；
3. 序列号和主 MAC 同时存在但指向不同身份时，生成冲突并隔离；
4. 两者均缺失或均无法匹配时保持 `UNRESOLVED`，不因名称、组、IP 或 AC 相似度自动合并；
5. 人工确认只能通过受保护接口完成，必须写入确认者、理由和前后身份证据。

## 5. 替换与跨 Site

AP 替换意味着新的序列号/主 MAC 组合产生新的 `fit_ap_id`，旧身份标记 `REPLACED`，新身份的 `replacement_of` 指向旧 ID。原有观测保持不可变。

Site 不是可选展示字段，而是所有 AC/AP 表的强制隔离键。发现同一序列号或主 MAC 跨 Site 出现时，不自动跨 Site 合并；先生成 `SITE_CONFLICT`，由具备全局身份管理权限的流程处理。
