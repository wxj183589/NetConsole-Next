# Device Batch Concurrency Contract

## Optimistic revision

The preview records `device_inventory.updated_ms` for each target. Confirmation requires the current value to equal `expected_revision`. Existing device writes and batch writes both advance `updated_ms` monotonically. A stale target therefore produces `DEVICE_VERSION_CONFLICT`.

## Atomicity

The confirmation path first validates every pending item, then updates every item with `WHERE id = ? AND updated_ms = ?` inside one serializable SQLite transaction. A zero-row update rolls the transaction back and marks the operation `CONFLICT` in a separate transaction. There is no best-effort subset commit.

## Competing confirmations

Claiming an operation is serialized by the Catalog transaction. The first claimant changes `PENDING_CONFIRM` or `FAILED` to `RUNNING`; a concurrent claimant sees `RUNNING` or the terminal result. At most one claimant can write the operation. Repeated confirmation after `SUCCEEDED` is an idempotent read.

## Scope checks

- group assignment requires the group to exist at the current device site;
- site assignment requires the site to exist and clears the old group;
- metadata and enablement preserve fields not named by the request;
- management-IP conflicts are checked against non-target rows and duplicate requested targets;
- disappeared targets remain in the operation item list and become `DEVICE_NOT_FOUND` conflicts.

## Restart behavior

Startup marks `QUEUED` and `RUNNING` operations as `INTERRUPTED` and writes a failure audit. It does not guess whether an external side effect occurred and does not replay automatically. The UI exposes the persisted result and directs the administrator to create a fresh preview.
