# Open-source reference and dependency register

No source from the existing NetConsole GPL-3.0 repository was copied in this phase. Its `main` branch was read-only at revision `d457f6d`; future reuse needs explicit source-level license review and must preserve required notices/source obligations.

## Current direct dependencies

| Component | Use | Version | License / risk status |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | SQLite access | 10.0.4 | MIT (NuGet package license metadata) |
| Microsoft.Extensions.Hosting.WindowsServices | Windows SCM host adapter | 10.0.4 | MIT (NuGet package license metadata) |
| SQLitePCLRaw core / bundle / provider | SQLite provider | 2.1.11 | Apache-2.0 (NuGet package license metadata) |
| SQLitePCLRaw.lib.e_sqlite3 | Bundled SQLite native engine | 3.53.3 | SQLite public domain (`LICENSE.txt` in the restored NuGet package) |
| System.ServiceProcess.ServiceController | Local Manager SCM access | 10.0.0 in Local Manager; 10.0.4 transitively in Service | MIT (NuGet package license metadata) |
| System.Diagnostics.EventLog | ServiceController transitive dependency | 10.0.0 | MIT (NuGet package license metadata) |
| .NET 10 shared/runtime components | Host runtime | 10.0 | Microsoft .NET runtime licensing and framework-dependent/self-contained package contents must be recorded with the release artifact |

On 2026-10-19, NuGet license metadata and the bundled SQLite `LICENSE.txt` were checked from the restored packages; the service and Local Manager transitive vulnerability scans both reported no known vulnerable packages. CI does not yet enforce license metadata or emit an SBOM/license notice bundle. There is no external executable bundled or invoked by NetConsole Next at this stage.

## Design references (no code copied)

| Project | Intended reference |
| --- | --- |
| Telegraf | Bounded input/output buffering and backpressure concepts |
| Fluent Bit | Disk-backed buffering and visibility into dropped/retained records |
| Zabbix | Modular monitoring and task/component status concepts |
| iPerf3 / fping | Future tool behavior and measurement semantics; evaluate packaging/license/runtime separately |
| SFTPGo | Optional future SFTP service behavior; do not embed without a dependency and license review |

No new external library is introduced by the collection scheduler; it uses .NET channels and cancellation primitives.
