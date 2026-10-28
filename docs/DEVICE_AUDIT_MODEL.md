# Device Batch Audit Model

## Event shape

Device mutations use the existing `audit_events` table:

```json
{
  "actor": "admin",
  "subjectId": "op-...",
  "detail": {
    "correlationId": "op-...",
    "kind": "BATCH_DISABLE",
    "targetCount": 3,
    "safeChangedFields": ["enabled"],
    "safeFailureReason": null
  }
}
```

`outcome` is `SUCCESS`, `CONFLICT`, `FAILED` or `DENIED`. Operation IDs are used as the subject so a batch can be reconstructed without duplicating a sensitive request into every device detail view.

## Event vocabulary

- `DEVICE_BATCH_PREVIEW`
- `DEVICE_BATCH_CONFIRM`, `DEVICE_BATCH_START`, `DEVICE_BATCH_RETRY`
- `DEVICE_BATCH_SUCCESS`, `DEVICE_BATCH_CONFLICT`, `DEVICE_BATCH_FAILED`
- `DEVICE_BATCH_CANCEL`, `DEVICE_BATCH_INTERRUPTED`
- `DEVICE_BATCH_PERMISSION_DENIED`
- `DEVICE_WRITE_PERMISSION_DENIED`, `DEVICE_UPDATE_FAILED`, `DEVICE_CREATE_FAILED`
- `DEVICE_GROUP_PERMISSION_DENIED`, `DEVICE_GROUP_FAILED`
- `DEVICE_IMPORT_PERMISSION_DENIED`, `DEVICE_IMPORT_PREVIEW_FAILED`, `DEVICE_IMPORT_COMMIT_FAILED`

## Safety rules

Audit input is normalized, bounded and control-character checked. Only field names and coarse failure codes are accepted from the batch service. Passwords, credential references, private keys, command text, raw CSV and exception messages are excluded. If an audit write fails, the API returns the original safe operation result while the service records a diagnostic marker; audit failure is not allowed to turn a committed device update into an apparent rollback.

Permission failures are recorded with `DENIED` and `SUPERADMIN_REQUIRED` when an authenticated identity is available. Ownership failures do not reveal another administrator's operation contents.
