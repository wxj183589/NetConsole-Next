# R7.7 Fault center development report

## Final integration evidence

Status **PARTIAL**. Service full harness EXIT0 in reports/r77-api-service-full.log, including FAULT_CENTER_TESTS=27/27 and OPERATIONAL_HEALTH_TESTS=23/23. The pending-harness statements below are superseded. Web393 and fault frontend16 pass. Final candidate real HTTP authorization43 checks and both-resolution shared fault filter checks pass; they do not simulate a real device outage or prove outage recovery. Active Long Ping projection, multi-resource completeness, Syslog/config incidents remain open.

## 此前工作流记录

Date: 2026-11-08. Base: `b4d942e`, branch `codex-A/netconsole-next-r3-dual-ac`.

## Product change

Fault discovery now distinguishes cumulative Ping statistics from confirmed loss and recovery evidence. A single lost probe remains diagnostic history; three consecutive actual valid losses produce one `PROBE_PACKET_LOSS` incident. Existing device/Site/profile/resource fingerprinting and durable incident lifecycle remain in use.

Recovery requires the same exact protocol/address/port resource and three trailing valid replies, all later than the incident. The same completed Long Ping may therefore create a loss event and subsequently prove recovery while its cumulative loss remains greater than zero. Existing background `VerifyObservedDevices` uses this evidence without network admission or repeated blocked-history writes. Re-reading that diagnostic does not reopen the resolved incident.

Failure timestamps now represent actual confirmed loss, avoiding the previous case where a run completion time prevented recovery from later samples inside that run. Partial, invalid, stale, future, foreign-target or cancelled evidence cannot create confirmation or recovery windows. Ping never proves AP/device offline.

## Verification coverage

`OperationalHealthTests` adds coverage for isolated losses, contiguous confirmation, trailing recovery, retained cumulative loss, invalid samples, sample freshness, exact endpoints, Quick/Long parity, and unsent samples. `FaultCenterTests` adds boundary checks for every recovery reply being newer, protocol/port separation, later-loss rejection, actual diagnostic-to-durable-fault discovery, and background recovery inside one completed Long Ping.

Frontend regression command `pnpm exec vitest run src/components/FaultCenter.test.ts` completed **16/16 PASS**. Scoped diff whitespace validation passed. Service harness execution is pending parent-coordinated integration; expected group labels after successful execution are `OPERATIONAL_HEALTH_TESTS=23/23` and `FAULT_CENTER_TESTS=27/27`, not current pass claims.

## Data and boundaries

No schema migration, legacy data copy, service restart, physical device operation, device credential access, or retired HistoryStore fallback was introduced. Test data uses disposable existing `fault-center-*` fixtures inside the Next Workspace. Simulator and browser checks remain owned by the root integration task.

The projection remains bounded to completed fresh jobs and the latest matching probe per device. It does not yet evaluate active Long Ping, all protocol resources simultaneously, Syslog/configuration incidents, or new AP/client associations. Existing AP live-evidence and source-scope recovery rules are preserved. This report does not claim full product closure or real-device outage acceptance.
