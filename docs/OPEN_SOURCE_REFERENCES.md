# Open-source reference and dependency register

Initial provenance record: no source from the existing NetConsole GPL-3.0 repository was copied in that phase; the read-only reference was revision `d457f6d`. The 2026-11-06 Netmiko inheritance and source review are documented in [the current connection audit](NETMIKO_CONNECTION_AUDIT_20261106.md).

Current connection checkpoint: `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`. Network CLI uses CPython + Netmiko; SSH.NET is retained for the separate Linux terminal. The local Windows candidate bundles CPython 3.13.9, Netmiko 4.7.0, Paramiko 4.0.0 and pinned transitive wheels. Netmiko/Paramiko versions and license metadata below were read from that candidate. This update does not rerun or extend the historical vulnerability scans.

## Current direct dependencies

| Component | Use | Version | License / risk status |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | SQLite access | 10.0.4 | MIT (NuGet package license metadata) |
| Microsoft.Extensions.Hosting.WindowsServices | Windows SCM host adapter | 10.0.4 | MIT (NuGet package license metadata) |
| SQLitePCLRaw core / bundle / provider | SQLite provider | 2.1.12 | Apache-2.0 (NuGet package license metadata) |
| SQLitePCLRaw.lib.e_sqlite3 | Bundled SQLite native engine | 2.1.12 package carrying SQLite 3.53.3 | SQLite public domain (`LICENSE.txt` in the restored NuGet package) |
| SSH.NET | Separate Linux SSH terminal and connection test; obsolete network-device transport removed | 2026.0.0 | MIT (NuGet license metadata) |
| BouncyCastle.Cryptography | SSH.NET cryptographic dependency | 2.7.0 | MIT (NuGet license metadata) |
| System.Security.Cryptography.ProtectedData | Retained credentials assembly dependency; ordinary device/Profile passwords do not use the DPAPI vault | 10.0.4 | MIT (NuGet license metadata); Windows-only provider |
| Netmiko | All network-device SSH/Telnet CLI and network Web Shell | 4.7.0 | MIT (bundled distribution metadata) |
| Paramiko | Netmiko dependency; direct client limited to Jump forwarding/probe; synthetic SSH servers in tests | 4.0.0 | LGPL-2.1 (bundled distribution metadata); included in the candidate Python runtime |
| System.ServiceProcess.ServiceController | Local Manager SCM access | 10.0.0 in Local Manager; 10.0.4 transitively in Service | MIT (NuGet package license metadata) |
| System.IO.Pipes.AccessControl | Windows Desktop Helper named-pipe ACLs | 6.0.0-preview.5.21301.5 | MIT (NuGet package license metadata); Windows host helper only; preview API must be reevaluated before production packaging |
| System.Diagnostics.EventLog | ServiceController transitive dependency | 10.0.0 | MIT (NuGet package license metadata) |
| .NET 10 shared/runtime components | Host runtime | 10.0 | Microsoft .NET runtime licensing and framework-dependent/self-contained package contents must be recorded with the release artifact |

Historical 2026-10-20 evidence: SSH.NET, BouncyCastle, ProtectedData NuGet license metadata, the bundled SQLite `LICENSE.txt`, and the then test-only Paramiko license were checked. Service and Credentials `dotnet list package --vulnerable --include-transitive` scans and `pip-audit --requirement tests/requirements-ssh-protocol.txt` reported no known vulnerable packages at that time. That requirements file and old SSH.NET wire simulator have now retired; this result is not a scan of the current bundled versions. Current pinned worker dependencies are in `tools/netmiko_requirements.txt`; synthetic E2E clients share them through `tests/requirements-netmiko-e2e.txt`.

## Design references (no code copied)

| Project | Intended reference |
| --- | --- |
| Telegraf | Bounded input/output buffering and backpressure concepts |
| Fluent Bit | Disk-backed buffering and visibility into dropped/retained records |
| Zabbix | Modular monitoring and task/component status concepts |
| iPerf3 / fping | Future tool behavior and measurement semantics; evaluate packaging/license/runtime separately |
| SFTPGo | Optional future SFTP service behavior; do not embed without a dependency and license review |

The bounded scheduler uses .NET channels and cancellation primitives. Current network CLI and offline candidate evidence are recorded in [unified architecture acceptance](NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md); package versions in this register do not establish device compatibility.
