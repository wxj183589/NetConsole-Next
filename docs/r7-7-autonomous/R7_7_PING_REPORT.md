# R7.7 Ping development report

## Final integration checkpoint

Status **PARTIAL**. Service full EXIT0: operational-health23/23, fault27/27, LongPing control3/3, authority7/7 and QuickPing9/9. Earlier cfc801c candidate LongPing HTTP13, loss HTTP18 and loss browser18 checks PASS. Final API-boundary candidate shared both-resolution Ping P95/history CSV checks PASS using existing QuickPing history; no stored completed LongPing points exist, so that specific real-data history remains NOT_TESTED. No authoritative client/AP identity was invented; link remainsUNKNOWN. Final shared instancePID21540, no new probe admission from browser.

## 此前工作流记录

Date: 2026-11-08. Base: `b4d942e`, branch `codex-A/netconsole-next-r3-dual-ac`.

## Delivered behavior

Quick Ping and completed Long Ping now use actual ordered samples to confirm fault and recovery windows. A single loss or two consecutive losses remains in diagnostic statistics without creating a `PROBE_PACKET_LOSS` incident. Three consecutive valid `TIMED_OUT` / `UNREACHABLE` samples confirm the event; its time is the last confirmed loss sample, rather than task completion.

Three trailing valid `REPLIED` samples can prove recovery, including within the same completed Long Ping that recorded the loss. Every reply in the recovery window must be newer than the incident. Cumulative loss percentage remains unchanged; recovery does not erase historical loss.

The projection validates the current device ID, Site, management address, protocol and TCP port. It rejects cancelled/active jobs, nonbusiness failures, stale or future samples, sequence gaps, time reversal, invalid latency and inconsistent sample counts. Requested but unsent samples do not count as loss. No AP association, AP offline or device offline state is inferred.

## Changed files

- `src/NetConsole.Next.Service/OperationalHealthService.cs`: bounded sample confirmation and optional `probeWindow` health evidence.
- `src/NetConsole.Next.Service/FaultCenterService.cs`: exact-resource recovery verification independent of cumulative loss.
- `tests/NetConsole.Next.Service.Tests/OperationalHealthTests.cs`: six additional sample/resource/freshness regression groups.
- `tests/NetConsole.Next.Service.Tests/FaultCenterTests.cs`: recovery boundary tests and actual diagnostics → health → durable fault integration.
- `web/src/components/FaultCenter.vue`: explains confirmation, recovery and retained cumulative statistics.

## Validation

- `pnpm exec vitest run src/components/FaultCenter.test.ts`: **16/16 PASS**.
- Scoped `git diff --check`: **PASS**.
- Service compilation and test-harness result: pending root integration execution; no unexecuted tests are claimed as passed here.
- New integration coverage runs normal NetworkDiagnosticsService, OperationalHealthService and FaultCenterService with a scripted injected probe. It includes a 10-second Long Ping and actual fault persistence. This is deterministic service integration, not real-device outage acceptance.

## Limits and remaining acceptance

The existing bounded read model remains: latest 20 authorized diagnostic jobs, completed within five minutes, latest matching probe per device. Sample timestamps must also be fresh. Running/paused Long Ping does not yet create or resolve incidents; its existing live sampling and control UI remain available. Protocol-specific simultaneous fault projection and active-run recurrence are not added by this batch.

No simulator outage was introduced by this workstream. Parent-coordinated HTTP/browser or real-device results must be reported separately. Frozen R3 contracts, AP identity, and client association are unchanged. No production or legacy NetConsole data was used or copied; isolated tests reuse the existing fixture lifecycle under the Next Workspace.
