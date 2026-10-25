# Web PR-2 acceptance

## Implemented in this branch

- Vue /tools and /files routes use the NetConsole Next service APIs.
- SUPERADMIN-only host-tool configuration, external launch request and host directory request.
- Local Named Pipe Desktop Helper with a constrained operation allow-list.
- Logical-root file listing, search/paging, single-file streaming and background ZIP jobs.
- Traversal, reparse-point and sensitive-file protections.
- Elevated local password reset command/scripts with session and CSRF revocation.
- PR-1 auth/CSRF and G2.3/G2.4 terminal code remains in place.
- ZIP admission is bounded before scan: one running/eight queued jobs, two active jobs per user, finite file/input/metadata/directory budgets and aggregate reserved input bytes.
- ZIP admission checks the actual `archive-tmp` volume before directory expansion. Below `ArchiveMinFreeDiskBytes` it returns HTTP `503` / `ARCHIVE_DISK_SPACE_LOW`; an unverifiable volume fails closed with `ARCHIVE_STORAGE_UNAVAILABLE`. No scan, job record, queue entry or temp file is created, and reservations are rolled back. Runtime checks remain in place for TOCTOU space loss.
- ZIP cancellation is linked to the running copy, terminal states are monotonic, `.zip.part` files are unpublished until complete, and a periodic maintenance loop cleans expired jobs and orphan archive files.
- `tools/acceptance/Test-WebPR2-Host.ps1` and `docs/WEB_PR2_WINDOWS_HOST_ACCEPTANCE.md` provide a Windows PowerShell 5.1-compatible, report-producing host check. Default CHECK_ONLY is read-only; GUI launch requires explicit operator switches, and real tools/devices are never substituted with mocks.

## Evidence and limitations

tests/web_pr2_contract.py is a fail-closed source contract check. tests/NetConsole.Next.Service.Tests includes deterministic injected disk-space tests for below/equal/above threshold admission, unavailable storage, no-scan rejection, retry after recovery and runtime space loss, plus startup orphan cleanup, elapsed periodic/idle cleanup, retained-job limits and download-lease cleanup race coverage. tests/web_pr2_e2e.py starts the real .NET Service against an isolated data root with small queue limits and a development-only bounded copy delay. It verifies list/download/archive/role/traversal behavior, controlled queue rejection, per-user admission, queued/running cancellation, temp cleanup and standard ZIP CRC/content integrity. It does not claim a real Windows GUI launch or a multi-gigabyte ZIP64 run.

The final report must keep these values explicit:

    ART_DESIGN_PRO_USAGE=VISUAL_REFERENCE_ONLY
    OLD_NETCONSOLE_USAGE=FUNCTIONAL_REFERENCE_ONLY
    PRIMARY_PROJECT=NETCONSOLE_NEXT
    BACKEND=EXISTING_CSHARP_DOTNET10
    UI_FRAMEWORK=VUE3_TYPESCRIPT_ELEMENT_PLUS
    REAL_SECURECRT=NOT_RUN
    REAL_XSHELL=NOT_RUN
    REAL_PUTTY=NOT_RUN
    REAL_EXPLORER=NOT_RUN

Windows service/SCM, real interactive desktop helper, LAN cross-host access and real external-program launches require the Windows acceptance job or an authorized host installation.
