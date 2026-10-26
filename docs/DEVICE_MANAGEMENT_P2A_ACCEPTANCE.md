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

The Web checks cover TypeScript compilation and production asset generation for the batch selection, preview, confirm, cancel and refresh-recovery UI. The Windows Chrome E2E also exercised the real service: select one device, create a disable preview, reload the page, recover the persisted operation and confirm it.

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

The implementation is browser-ready and keeps the existing device-management selectors intact. Windows Chrome evidence passed with `tests/device_management_browser_e2e.mjs` against `http://127.0.0.1:18773`; the temporary data root and screenshots were retired after inspection. GitHub Actions run `40302560590` passed both Ubuntu and Windows jobs, and Windows deployment syntax run `40302560532` passed. Real devices, production data and the development Authority are out of scope.

## PR21 review status

PR #21 latest head was reviewed against the four P1 fixes and associated CI evidence. The GitHub review state remained `COMMENTED` with no formal `APPROVED` review. P2-A Draft PR #22 is open at `https://github.com/wxj183589/NetConsole-Next/pull/22`, so the correct combined status is `P2A_VERIFIED_PR21_APPROVAL_PENDING` until an authorized reviewer records approval.
