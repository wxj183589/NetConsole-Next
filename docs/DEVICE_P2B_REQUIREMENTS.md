# Device Management P2-B Requirements

## Boundary

P2-B is a design-only preparation package. It does not add Schema v13, API routes, UI code, migrations, device writes, VLAN configuration, SSH/Telnet/COM access, or changes to the Production/Dev Authority. The current P2-A Schema v12 and batch-operation engine remain the implementation baseline.

## Problem statement

One physical device can have several management addresses, several logical network memberships, and different planned/observed values during commissioning. A single `management_ip` field cannot represent that safely. The next model must preserve the existing device identity while making address, network-domain and VLAN planning explicit and reviewable.

## Required scenarios

1. Register a device with one legacy management address and later add a second IPv4 or IPv6 address without rewriting the original record.
2. Represent the same device in a station management domain, a train-ground or service domain, and an out-of-band domain with explicit scope.
3. Import planned addresses/VLANs from a controlled file and compare them with observed values from a future collector.
4. Record a manual verification that confirms or rejects an observed/planned value without erasing either source.
5. Show address and VLAN conflicts at device, domain, station and site scope before a write is accepted.
6. Keep a device attached to Site/Station/Line context while allowing a device to be temporarily unassigned or shared by a line segment.
7. Export a reviewable difference set with source, revision, observed time, actor and resolution state.

## Source and authority rules

Every address, network-domain membership and VLAN value has a `source_kind` from:

- `PLANNED`: engineering intent or approved design;
- `OBSERVED`: read-only telemetry collected from a device;
- `IMPORTED`: imported external inventory or commissioning file;
- `MANUAL`: an operator assertion or verification.

Source kind is provenance, not an implicit overwrite priority. The system must retain concurrent values and expose a field-level comparison. A manual verification may resolve a conflict, but it must not mutate or delete the planned/observed/imported row. Any effective-value projection must include the selected source and revision.

## Functional requirements

- Preserve `DeviceInventory.id` and the current `management_ip` compatibility projection until all consumers migrate.
- Support IPv4 and IPv6, prefix length, gateway/VRF metadata, address role, lifecycle and domain scope.
- Support VLAN ID, VLAN name, tagging mode, native/PVID intent, QinQ metadata where required by the domain, and an explicit validity interval.
- Enforce uniqueness within an explicit scope, not globally across unrelated sites or domains.
- Use optimistic concurrency (`revision`/ETag) for all writes and return a structured conflict containing current and submitted versions.
- Make imports idempotent through an import/run key and row fingerprint; a repeated import must not duplicate rows.
- Emit audit records for create/update/archive/import/verify/resolve operations without storing secrets or full sensitive payloads.
- Keep all writes behind the existing authorization, CSRF, request-size and audit boundaries.
- Support pagination and bounded filters so an address/VLAN review cannot load an unbounded site into one response.

## Non-functional requirements

- No silent destructive merge between planned and observed data.
- Reads must remain useful when a target device is offline or a collector is unavailable.
- Migrations must be restart-safe, forward-only in production, and accompanied by a rollback/read-disable procedure.
- UI must work in light and dark themes and at the existing acceptance viewport sizes.
- Existing P2-A batch operations must remain idempotent and independent from address/VLAN projection writes.

## Acceptance gates for implementation

The implementation is not complete until fixtures cover legacy compatibility, IPv4/IPv6, duplicate-in-scope rejection, cross-scope coexistence, import repeatability, planned-v-observed conflict, manual verification, stale revision rejection, permission isolation, audit redaction, migration restart, and rollback/read-only behavior. Real devices and real VLAN changes require a separate authorized gate.
