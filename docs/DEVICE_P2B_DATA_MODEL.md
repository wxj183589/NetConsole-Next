# Device Management P2-B Logical Data Model

## Stable identity and compatibility

`DeviceInventory` remains the stable device identity and keeps its current primary key and existing `management_ip` compatibility projection. The projection is derived from the selected primary management address for legacy consumers; it is not the source of truth for the multi-address model. No legacy caller should infer that the device has only one address.

## Logical entities

| Entity | Purpose | Key relationships |
|---|---|---|
| DeviceInventory | Stable physical/logical device identity | existing device row; optional site/station/line context |
| DeviceAddress | One address assigned to a device in a domain | device, network domain, source revision |
| NetworkDomain | Bounded management/network namespace | site/station/line/section scope; optional parent |
| DeviceDomainMembership | Device presence and role in a domain | device, domain, lifecycle, source |
| VlanPlan | Planned VLAN definition and intended scope | network domain, site/station/line scope |
| VlanAssignment | VLAN membership/intent for a device interface or address | device, domain, VLAN plan, source |
| InventoryImportRun | Idempotent import envelope | source file/run fingerprint and actor |
| DeviceDataReview | Manual verify/reject/resolve decision | target entity/revision, actor, reason |

The implementation may normalize or combine membership tables only after the schema review confirms that a device can have multiple interfaces and multiple domains without ambiguity. The logical contract above is the required behavior, not a mandate to prematurely choose physical table names.

## Site, station, line and section

The context hierarchy is `Site -> Station -> Line/Section` for ordinary rail deployment, but the model must not force a line to own a station globally. A station can participate in more than one line, and a device can be scoped to a station or section while its site is inherited. Therefore:

- Site is the top operational boundary and permission/filter boundary.
- Station is a physical location within a site.
- Line is a service/route context that may cross sites or stations.
- Section is an optional bounded segment used where station/line alone is too broad.
- Device-to-context links carry `scope_kind`, `source_kind`, `valid_from`, `valid_to` and `revision`.
- Missing or ambiguous context remains explicit (`UNASSIGNED`/`CONFLICT`) instead of being guessed from a name or IP.

## Address model

Each `DeviceAddress` records address family, normalized address, prefix, gateway/VRF metadata, role (`MANAGEMENT`, `SERVICE`, `OUT_OF_BAND`, `LOOPBACK`, `OTHER`), interface hint, network-domain reference, source kind, source reference, observed/received time, validity, primary-selection intent and revision. The normalized address is used for uniqueness; display formatting is not.

An address may be `PLANNED`, `OBSERVED`, `IMPORTED` or `MANUAL`. A planned address and an observed address with the same textual value are separate provenance observations linked by a comparison key, not blindly merged.

## VLAN model

`VlanPlan` defines the intended ID/name/tagging/native/PVID and the domain/scope in which it is meaningful. `VlanAssignment` binds a device interface or domain membership to a plan or observed VLAN, with source, lifecycle and revision. A VLAN ID may coexist in different domains; uniqueness is scoped to the domain and effective interval. A mismatch is a conflict record, not an automatic rewrite.

## Authority and effective projection

The UI/API returns all retained source rows plus an explicit `effective` projection. The projection is selected by a documented policy and can be overridden only by a manual review record. It must report `effective_source_kind`, `effective_revision`, `conflict_state` and `as_of`. Deleting a source row is not a resolution operation; archival and review history are retained.

## Invariants

- A device ID is never inferred from a fuzzy name/IP match.
- A normalized address is unique only within its declared domain and active validity scope.
- A VLAN assignment cannot point to an archived/nonexistent domain or device.
- Every mutable row has a monotonic revision and provenance.
- Every comparison is reproducible from stored source revisions.
- Legacy reads continue to receive a deterministic primary-address projection during migration.
