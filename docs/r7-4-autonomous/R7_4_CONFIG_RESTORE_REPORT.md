# R7.4 Configuration Restore Report

The R7.4 change adds a regression for an uncertain in-flight configuration write: after a second Service instance reconstructs state, the change is exposed as `REQUIRES_REVIEW`, keeps the before-backup reference, has no after-backup, and does not replay the device operation. When the original operation resumes, it can finish readback without a second write.

`SIMULATOR_CONFIGURATION_TESTS=22/22 PASS` in the full Release Service harness. This uses an injected fixture and is not a device restore.

Full configuration transfer, native device replacement, independent readback/hash verification, business validation, and successful/failed rollback remain **NOT_TESTED**. Overall: **PARTIAL**. No hardware configuration write was made.
