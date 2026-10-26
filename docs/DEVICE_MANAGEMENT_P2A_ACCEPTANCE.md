# Device Management P2-A Acceptance

## Automated acceptance

The isolated service harness covers:

- schema v12 creation and repeated initialization;
- five operation kinds and explicit target persistence;
- preview/confirm separation and idempotency;
- all-or-nothing commit and optimistic version conflict;
- missing target, site/group scope and management-IP conflict handling;
- cancellation, failure retry and concurrent confirmation;
- owner authorization, 100-target resource limit and audit redaction.

The Web checks cover TypeScript compilation and production asset generation for the batch selection, preview, confirm, cancel and refresh-recovery UI.

## Commands and results

Run from the P2-A worktree:

```text
dotnet build src/NetConsole.Next.Service/NetConsole.Next.Service.csproj --no-restore       PASS
dotnet build tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj --no-restore  PASS
dotnet run --project tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj --no-build  PASS
pnpm --dir web run typecheck                                                               PASS
pnpm --dir web run build                                                                  PASS
```

The .NET build reports the repository's existing `SQLitePCLRaw.lib.e_sqlite3 2.1.11` NU1903 warning. The Web build retains the existing large-chunk warning. Neither is a P2-A compile failure.

## Browser and CI boundary

The implementation is browser-ready and keeps the existing device-management selectors intact. Required final evidence is a real Windows Chrome run against the local service, plus the repository's Ubuntu and Windows CI jobs. Those external/runtime checks are not represented as passed unless their logs are available. Real devices, production data and the development Authority are out of scope.

## PR21 review status

PR #21 latest head was reviewed against the four P1 fixes and associated CI evidence. The GitHub review state remained `COMMENTED` with no formal `APPROVED` review, so the correct combined status is `P2A_VERIFIED_PR21_APPROVAL_PENDING` until an authorized reviewer records approval.
