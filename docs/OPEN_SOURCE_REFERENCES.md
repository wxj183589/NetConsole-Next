# Open-source reference and dependency register

No source from the existing NetConsole GPL-3.0 repository was copied in this phase. Its `main` branch was read-only at revision `d457f6d`; future reuse needs explicit source-level license review and must preserve required notices/source obligations.

## Current direct dependencies

| Component | Use | Version | License / risk status |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite | SQLite access | 10.0.4 | Microsoft package license and complete native dependency notices must be checked before redistribution |
| Microsoft.Extensions.Hosting.WindowsServices | Windows SCM host adapter | 10.0.4 | Microsoft package license to be included in release notice review |
| SQLitePCLRaw.lib.e_sqlite3 | SQLite native engine | 3.53.3 | Native bundled component; exact license and distribution notice review required before packaging |
| System.ServiceProcess.ServiceController | Local Manager SCM access | 10.0.0 | Microsoft package license to be included in release notice review |
| .NET 10 shared/runtime components | Host runtime | 10.0 | Runtime licensing and self-contained/framework-dependent package contents must be documented at release |

Current transitive vulnerability scan reports no known vulnerable packages from NuGet. This is not a license audit. There is no external executable bundled or invoked by NetConsole Next at this stage.

## Design references (no code copied)

| Project | Intended reference |
| --- | --- |
| Telegraf | Bounded input/output buffering and backpressure concepts |
| Fluent Bit | Disk-backed buffering and visibility into dropped/retained records |
| Zabbix | Modular monitoring and task/component status concepts |
| iPerf3 / fping | Future tool behavior and measurement semantics; evaluate packaging/license/runtime separately |
| SFTPGo | Optional future SFTP service behavior; do not embed without a dependency and license review |

No new external library is introduced by the collection scheduler; it uses .NET channels and cancellation primitives.
