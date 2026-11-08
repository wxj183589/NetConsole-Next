# R7.4 Device Interoperability

Date: 2026-11-08. Scope was limited to the four user-authorized simulator endpoints.

## Environment

The VirtualBox Host-Only adapter was Up and the four endpoint TCP ports accepted connections: AC1 `192.168.56.2:22`, AC2 `192.168.56.5:22`, SW `192.168.56.3:22`, FIT-AP `192.168.56.4:23`. No other addresses were scanned. Current service health was `HEALTHY`, PID 33036. The prior R7.3 report's TCP timeouts are superseded only for TCP reachability, not for authentication or device capability.

## Product read-only results

- AC1 and AC2 each completed the existing product Netmiko task. `display version`, AC role, AP inventory, address, radio and verbose AP commands completed with complete Raw capture and parsed facts. Both were identified as Comware 7.1.064 / WX5540H-HCL.
- A fresh exact AP Serial/MAC identity was observed from both ACs: AC1 `R/M` / `RUNNING_MASTER`, AC2 `R/B` / `RUNNING_BACKUP`. The product probe verified each Observation against its current task Raw fact. No HA switch was performed.
- SW's product task failed authentication before any command was captured. Read-only Catalog inspection found one enabled inventory-linked profile for this address; the other stored profile was not inventory-linked. No alternate credential was attempted.
- FIT-AP TCP/Telnet negotiation reached a password prompt. The probe sent no username or password and did not authenticate.

Evidence:

- `../../../reports/r72-product-dual-ac-20261108-185931/summary.json`
- `../../../reports/r72-product-lldp-scope-20261108-185958/summary.json`
- `../../../reports/r71-device-20261108-190230/summary.json`

Classification: AC1/AC2 **SIMULATOR_AUTH + SIMULATOR_READONLY PASS**; SW **SIMULATOR_TCP PASS / SIMULATOR_AUTH BLOCKED**; FIT-AP **SIMULATOR_TCP PASS / SIMULATOR_AUTH NOT_TESTED**. No `SIMULATOR_WRITE` or `SIMULATOR_RESTORE` was attempted.
