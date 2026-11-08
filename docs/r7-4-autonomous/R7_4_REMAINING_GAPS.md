# R7.4 Remaining Gaps

As of 2026-11-08, overall status is **PARTIAL**.

| Area | Status | Remaining work |
| --- | --- | --- |
| AC1 / AC2 connection and wireless reads | PASS for this read-only run | Add `display interface brief` and LLDP to a product task where the supported command set permits; do not infer HA from historical data alone. |
| SW SSH | BLOCKED | Its only enabled inventory-linked profile returned `H3cAuthenticationFailedException`. Obtain a valid authorized profile through the existing credential workflow before another attempt. |
| FIT-AP Telnet | PARTIAL | TCP and negotiation reached a password prompt; no credentials were sent. Native inventory and read-only capture need a valid device-approved credential. |
| Dual AC HA | NOT_TESTED | No switching or link-down injection. Obtain independent management/recovery path and controlled recovery procedure first. |
| Configuration restore | PARTIAL | Interrupted writes remain reviewable and are not replayed. Hardware upload, native replacement, readback hash, business validation and rollback remain unverified. |
| Fault center | PARTIAL | AP offline recovery accepts fresh verified R/B. Real device fault/recovery evidence and full source coverage remain outstanding. |
| Windows deployment | BLOCKED | SCM lifecycle needs an elevated isolated Windows environment. Readiness tests do not prove install, recovery, upgrade, rollback or uninstall. |
| Long Ping | LOCAL PASS | UI and Service tests pass. Live Service is an older build; integrated/browser and long-duration runtime acceptance remain outstanding. |
| Product UI | PARTIAL | Component tests pass. Browser E2E against a build containing this checkpoint was not run. |
| Long-running operation | NOT_TESTED | No R7.4 soak was run; prior 300-second R7.3 evidence is not upgraded. |
| Feature parity | PARTIAL | Mesh graphics, professional rail reports, complete Excel workflows and broader vendor/site acceptance remain open. |

No R7.4 device configuration writes, HA switching, remote push, merge or release publish occurred.
