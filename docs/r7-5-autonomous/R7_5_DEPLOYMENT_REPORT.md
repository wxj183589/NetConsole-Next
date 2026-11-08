# R7.5 Deployment Report

Status: **PARTIAL**. Candidate: `build/r75-candidate-verified-006f79b-r2`.

The release publisher built the Windows x64 Service and a fresh Vue bundle into a new Workspace directory. It verified the offline Netmiko bundle against the current source Worker and passed bundled Python, Netmiko 4.7.0, Paramiko 4.0.0, Telnet and Worker protocol smoke checks. The first publish attempt found missing `net10.0/win-x64` restore assets; after `dotnet restore -r win-x64`, the second candidate published successfully. The incomplete first output remains separate and was not used.

`tests/offline_candidate_e2e.py` ran against a synthetic isolated data root. Static UI/API, bundled SSH, diagnostic capture/history, reconnect, Linux WebSocket terminal, AC resources, configuration kinds, full diagnostic and scheduled backups passed. The full script exited 1 only because the required Windows loopback Traceroute returned `ICMP_PERMISSION_DENIED`; overall candidate acceptance is therefore PARTIAL.

The candidate was then started on the existing development instance at `http://127.0.0.1:18771/`, PID 17964, using the existing Next data authority `test-data/simulator-live-20261105`, loopback Syslog UDP 15514 and the candidate's bundled Worker. Startup migration succeeded. Current health is `HEALTHY`, SQLite integrity is `ok`, Raw shows 128 finalized / 0 incomplete / 0 missing, and recovery reports no issues. Browser acceptance passed on this instance.

Before the old R7.3 process was stopped, a single SQLite checkpoint was created at `test-data/r75-runtime-checkpoint-20261108/catalog.db` (1,904,640 bytes; SHA256 `404612c2f12b2e83ce4965d53e9a74b99a464a551861cff41bc9f7a22510cf08`; integrity `ok`). No full data root was copied. The prior PID/DLL was checked and stopped through `tests/r70_runtime_control.py`; no forced termination was used.

Windows SCM lifecycle, elevated ICMP Traceroute, multi-day stability and recovery rollback remain NOT_TESTED. No remote push, merge or release publication occurred.
