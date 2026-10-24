# Open-source reference and dependency register

No source from the existing NetConsole GPL-3.0 repository was copied in this phase. Its `main` branch was read-only at revision `d457f6d`; future reuse needs explicit source-level license review and must preserve required notices/source obligations.

## Current direct dependencies

| Component | Use | Version | License / risk status |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | SQLite access | 10.0.4 | MIT (NuGet package license metadata) |
| Microsoft.Extensions.Hosting.WindowsServices | Windows SCM host adapter | 10.0.4 | MIT (NuGet package license metadata) |
| SQLitePCLRaw core / bundle / provider | SQLite provider | 2.1.11 | Apache-2.0 (NuGet package license metadata) |
| SQLitePCLRaw.lib.e_sqlite3 | Bundled SQLite native engine | 3.53.3 | SQLite public domain (`LICENSE.txt` in the restored NuGet package) |
| SSH.NET | H3C read-only SSH session, host-key pin and command lifecycle | 2026.0.0 | MIT (NuGet license metadata); active upstream release, but target firmware interoperability and maintenance/security update process must be reviewed for release |
| BouncyCastle.Cryptography | SSH.NET cryptographic dependency | 2.7.0 | MIT (NuGet license metadata) |
| System.Security.Cryptography.ProtectedData | Windows DPAPI machine-scope H3C credential vault | 10.0.4 | MIT (NuGet license metadata); Windows-only provider, Linux returns unavailable |
| Paramiko | Test-only synthetic SSH server for SSH.NET wire-level regression tests; not shipped or referenced by the Service | 5.1.0 | LGPL-2.1 (`LICENSE` in PyPI wheel); Python test-only dependency |
| System.ServiceProcess.ServiceController | Local Manager SCM access | 10.0.0 in Local Manager; 10.0.4 transitively in Service | MIT (NuGet package license metadata) |
| System.IO.Pipes.AccessControl | Windows Desktop Helper named-pipe ACLs | 6.0.0-preview.5.21301.5 | MIT (NuGet package license metadata); Windows host helper only; preview API must be reevaluated before production packaging |
| System.Diagnostics.EventLog | ServiceController transitive dependency | 10.0.0 | MIT (NuGet package license metadata) |
| .NET 10 shared/runtime components | Host runtime | 10.0 | Microsoft .NET runtime licensing and framework-dependent/self-contained package contents must be recorded with the release artifact |

On 2026-10-20, SSH.NET, BouncyCastle, ProtectedData NuGet license metadata, the bundled SQLite `LICENSE.txt`, and Paramiko's LGPL-2.1 test license were checked. Service and Credentials `dotnet list package --vulnerable --include-transitive` scans and `pip-audit --requirement tests/requirements-ssh-protocol.txt` reported no known vulnerable packages from current sources. Paramiko is test-only and is not included in product outputs. CI does not yet enforce license metadata or emit an SBOM/license notice bundle. There is no external executable bundled or invoked by NetConsole Next at this stage.

## Design references (no code copied)

| Project | Intended reference |
| --- | --- |
| Telegraf | Bounded input/output buffering and backpressure concepts |
| Fluent Bit | Disk-backed buffering and visibility into dropped/retained records |
| Zabbix | Modular monitoring and task/component status concepts |
| iPerf3 / fping | Future tool behavior and measurement semantics; evaluate packaging/license/runtime separately |
| SFTPGo | Optional future SFTP service behavior; do not embed without a dependency and license review |

The bounded scheduler uses .NET channels and cancellation primitives. H3C SSH uses SSH.NET; its package version, direct/transitive licenses, vulnerability scan and update policy remain part of ongoing maintenance.
