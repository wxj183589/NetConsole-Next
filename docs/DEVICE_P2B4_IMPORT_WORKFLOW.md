# P2B-4 import workflow

导入流程固定为：

`UPLOAD -> VALIDATE -> DRY_RUN/PREVIEW -> CONFIRM -> ATOMIC_COMMIT -> AUDIT`

`POST /api/system/import-runs` 只解析 CSV、做 bounded validation，并持久化 PREVIEWED operation；不会立即写 device/domain/address/membership/VLAN。当前 CSV 列包含 entityType、targetId、deviceId、networkDomainId、membership/address/VLAN 字段、source 和 expectedRevision；实体 ID 必须精确匹配，不做名称、IP 或模糊匹配。

限制：UTF-8 payload 不超过 2 MiB，最多 10,000 条数据行，字段长度由 API 约束。`dry-run` 只返回同一个 preview；`confirm` 重新读取 payload、revision 和冲突规则，在一个 Serializable transaction 中全部提交或全部回滚；`cancel` 只结束 workflow。preview 过期、revision 变化、地址/VLAN 冲突均拒绝确认并保留冲突 operation/audit。

相同 actor+idempotency key+payload fingerprint 返回同一个 import run；同 key 不同 payload 返回 `IDEMPOTENCY_KEY_REUSE`。
