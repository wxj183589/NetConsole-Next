# Device Batch Operation State Machine

```text
                 create preview
                       |
          +------------+-------------+
          |                          |
   PENDING_CONFIRM                 CONFLICT
          |                          |
   cancel | confirm                  | re-preview required
          v                          |
      CANCELLED                      +----> new operation
          |
          +---- confirm
                       |
                     RUNNING
                 +-----+------+
                 |            |
              success       failure
                 |            |
                 v            v
             SUCCEEDED      FAILED -- confirm --> RUNNING

service restart: QUEUED/RUNNING -> INTERRUPTED
```

## State meaning

- `PENDING_CONFIRM`: valid preview persisted; no device row has been changed.
- `CONFLICT`: preview or confirmation found a scope/version/target conflict; confirmation is refused until a new preview is created.
- `RUNNING`: one owner has claimed the operation and is executing the single transaction.
- `SUCCEEDED`: all pending items were updated in one commit. Repeated confirmation returns the same result.
- `FAILED`: the transaction was rolled back for an execution failure. A later confirmation resets failed items to `PENDING` and safely retries the same snapshot.
- `CANCELLED`: explicit owner cancellation before commit; confirmation is refused.
- `INTERRUPTED`: service restart found an unfinished operation. It is never replayed automatically; the user must preview again.

## Idempotency

`(actor, idempotencyKey)` is unique. Reusing the key with the identical normalized JSON returns the existing operation. Reusing it with a different request is a conflict. A successful operation is read-only on repeated confirmation.

## Cancellation and retry

Cancellation is accepted only for a preview or failed operation. A running operation cannot be cancelled because the safe boundary is its SQLite transaction. Failed operations retain their request and expected revisions; retry revalidates all targets before writing. Conflicts and restart interruptions require a fresh preview rather than automatic replay.
