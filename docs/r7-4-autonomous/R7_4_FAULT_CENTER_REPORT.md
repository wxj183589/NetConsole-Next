# R7.4 Fault Center Report

`FaultCenterService` now treats a fresh, complete, Raw-verified `RUNNING_BACKUP` AP observation as recovery evidence for `AP_OFFLINE`, while retaining the source AC/Site requirement. Backup-only evidence does not resolve `AP_CONFLICT`; conflicting multi-source state still requires the existing stronger evidence contract.

The Service harness passed `FAULT_CENTER_TESTS=23/23`; Web Fault Center coverage remained in the 34-file/353-test suite. Dashboard alerts gained a direct action to focus the existing Fault Center evidence area, covered by a component test.

Overall: **PARTIAL**. This checkpoint includes fresh AP read-only observations but no induced device fault, failover, or real-world automatic recovery cycle. Existing historical fault evidence is not upgraded to current live evidence.
