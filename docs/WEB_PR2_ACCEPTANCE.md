# Web PR-2 acceptance

## Implemented in this branch

- Vue /tools and /files routes use the NetConsole Next service APIs.
- SUPERADMIN-only host-tool configuration, external launch request and host directory request.
- Local Named Pipe Desktop Helper with a constrained operation allow-list.
- Logical-root file listing, search/paging, single-file streaming and background ZIP jobs.
- Traversal, reparse-point and sensitive-file protections.
- Elevated local password reset command/scripts with session and CSRF revocation.
- PR-1 auth/CSRF and G2.3/G2.4 terminal code remains in place.

## Evidence and limitations

tests/web_pr2_contract.py is a fail-closed source contract check. tests/web_pr2_e2e.py starts the real .NET Service against an isolated data root and verifies list/download/archive/role/traversal behavior. It does not claim a real Windows GUI launch.

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
