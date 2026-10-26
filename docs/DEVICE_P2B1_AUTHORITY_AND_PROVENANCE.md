# P2B-1 来源、权威与有效投影契约

状态：contract draft，待 reviewer 确认。

## 要求

- source kind 当前冻结为 `PLANNED`、`OBSERVED`、`IMPORTED`、`MANUAL`。
- 每条事实必须带 bounded `sourceReference`、正数 `revision`，可选 `observedAtMs`；来源 revision 不得被覆盖。
- 系统不设置隐式来源优先级，不因 observed 或 planned 自动覆盖另一来源。
- 相同 fingerprint 的多来源事实可以形成 `CONSISTENT`，但多来源一致时不自动选择某个 source；不一致时为 `CONFLICT`。
- 人工复核必须引用 compared source revisions，并对精确 source reference + revision 作选择；过期选择保持 conflict。
- effective projection 必须保留 supporting source revisions，便于审计和重放。

## 选择、替代与影响

- 选择：事实不可变追加/保留，effective projection 可重算，人工决策是显式 revision-bound review。
- 拒绝：按 source kind 固定优先级、最后写入者胜出、observed 永远覆盖 planned；这些策略无法解释冲突历史。
- DB：未来 schema 需保存 source kind/reference/revision/fingerprint 和 review record；本阶段不新增表。
- API：DTO/error 返回 source reference/revision 和 `REVIEW_REQUIRED`、`STALE_REVISION` 等稳定码；不泄露不必要的设备/IP细节。
- 兼容：legacy source reference 采用确定性 `legacy-management-ip:<deviceId>`，旧 ID 与 terminal profile link 保持不变。
- 测试：planned-only、observed-only、一致/不一致、manual select、stale review、provenance preservation。
- Reviewer pending：待确认 review actor 的身份格式和人工复核是否需要二次确认，不在本阶段扩展认证模型。

## 有效状态

`EMPTY` 表示无事实；`CONSISTENT` 表示事实值一致且可用；`CONFLICT` 表示需人工处理或 review 无法匹配精确 revision；`MANUALLY_SELECTED` 表示 review 已选择存在的精确 source revision。所有状态均可从输入事实重算。

## Reviewer pending

权威选择、review API 和持久化属于后续 P2B 阶段；本提交只冻结纯函数语义和合成测试。
