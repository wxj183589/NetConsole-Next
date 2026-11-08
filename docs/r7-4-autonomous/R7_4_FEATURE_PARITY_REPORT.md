# R7.4 Feature Parity Report

The original-to-Next matrix is extended by one implemented product slice: Long Ping. It reuses Quick Ping's authorized device target validation, ICMP/TCP probe, cancellation, progress snapshots and durable diagnostic history. Bounds are 10-3,600 seconds, 1,000-5,000 ms intervals, at most eight targets and at most 3,600 stored samples. Quick Ping's existing sampling and 120-second per-device budget remain unchanged.

The Tools view exposes duration and protocol controls and reuses the sample timeline. Web tests verify request construction and the Service harness verifies bounds, sample output and cancellation. Full Web validation passed: 34 files / 353 tests and typecheck.

Status: **PARTIAL**. Live integrated Long Ping, long-duration operation, retained-history growth and original `fping` process semantics are not yet accepted. Mesh graphics, rail reports and full Excel parity remain open.
