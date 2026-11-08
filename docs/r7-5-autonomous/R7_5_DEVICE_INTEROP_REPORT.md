# R7.5 Device Interop Report

Overall: **PARTIAL**. This closure did not open new device sessions; the device evidence below remains scoped to the capture times documented in the R7.4 handoff and Workspace reports.

| Device | Current evidence | Status |
| --- | --- | --- |
| AC1 `192.168.56.2:22` | R7.4 product SSH read-only collection succeeded; fresh AP observation was `R/M`. | `SIMULATOR_READONLY PASS` at that capture time |
| AC2 `192.168.56.5:22` | R7.4 product SSH read-only collection succeeded; same AP identity had an independent `R/B` observation. | `SIMULATOR_READONLY PASS` at that capture time |
| SW `192.168.56.3:22` | Sole enabled inventory-linked profile returned authentication failure; no alternate credential was used. | `BLOCKED` |
| FIT-AP `192.168.56.4:23` | Credential-free Telnet negotiation reached a password prompt. No username/password was sent. | `SIMULATOR_TCP PASS`; authentication NOT_TESTED |

No device configuration mutation occurred in R7.5 closure. The AC1 running configuration command `wlan ap-execute all exec-console enable` was accepted in the prior authorized R7.4 operation, but effective state and FIT-AP login remain unverified; no `save` was issued. No HA transition, restore or recovery was attempted.

Evidence references: `reports/r72-product-dual-ac-20261108-185931/summary.json`, `reports/r72-product-lldp-scope-20261108-185958/summary.json`, and the R7.4 device handoff. None of these are new R7.5 live captures.
