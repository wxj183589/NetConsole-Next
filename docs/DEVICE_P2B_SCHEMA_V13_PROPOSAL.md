# Device Management P2-B Schema v13 Proposal

## Status and safety boundary

This is a proposal only. No v13 migration, runtime schema version change, table creation or API implementation is included in the P2-B design branch. The proposal must be reviewed and accepted before any production migration work starts.

## Proposed tables

### `network_domains`

`id` (text primary key), `site_id`, optional `station_id`, optional `line_id`, optional `section_id`, `parent_domain_id`, `name`, `domain_kind`, `vrf_name`, `source_kind`, `source_ref`, `lifecycle`, `revision`, `created_ms`, `updated_ms`, `valid_from_ms`, `valid_to_ms`.

Constraints: unique active `(site_id, normalized_name, domain_kind)`; parent must not form a cycle; scope IDs must resolve when present. Index site/station/line/active and normalized name.

### `device_addresses`

`id` (text primary key), `device_id`, `network_domain_id`, `address_family`, `normalized_address`, `prefix_length`, `gateway_address`, `vrf_name`, `address_role`, `interface_name`, `source_kind`, `source_ref`, `observed_ms`, `valid_from_ms`, `valid_to_ms`, `is_primary_candidate`, `lifecycle`, `revision`, `created_ms`, `updated_ms`.

Constraints: valid family/prefix; unique active `(network_domain_id, normalized_address, prefix_length)` for the declared scope; foreign keys to device/domain with restrictive delete behavior; index device, domain, normalized address and source/revision.

### `device_domain_memberships`

`id`, `device_id`, `network_domain_id`, `membership_role`, `source_kind`, `source_ref`, `lifecycle`, `revision`, validity interval and timestamps.

Constraint: one active membership per `(device_id, network_domain_id, membership_role)` and no membership to an archived domain.

### `vlan_plans`

`id`, `network_domain_id`, `site_id`, optional station/line/section scope, `vlan_id`, `name`, `tagging_mode`, `native_vlan`, `pvid`, `qinq_outer_id`, `qinq_inner_id`, `source_kind`, `source_ref`, `lifecycle`, `revision`, validity interval and timestamps.

Constraint: VLAN ID range and tagging rules; unique active `(network_domain_id, vlan_id, validity bucket)` as defined by the migration review. The design must not impose global VLAN uniqueness.

### `vlan_assignments`

`id`, `device_id`, `network_domain_id`, `vlan_plan_id` nullable for observed-only rows, `interface_name`, `vlan_id`, `assignment_role`, `source_kind`, `source_ref`, `observed_ms`, `lifecycle`, `revision`, validity interval and timestamps.

Constraints: device/domain consistency; no duplicate active assignment for the same device/interface/domain/role/source revision; observed-only data is retained when no plan exists.

### `inventory_import_runs` and `device_data_reviews`

The import envelope stores source fingerprint, actor, started/completed state, row counts, error summary and idempotency key. Review rows store target type/id, compared revisions, decision (`ACCEPT_PLANNED`, `ACCEPT_OBSERVED`, `REJECT`, `DEFER`), reason, actor, timestamps and audit correlation.

## Versioning and migration

- Add the tables in one transaction only after a v13 preflight confirms v12 integrity, WAL mode and required indexes.
- Backfill one `DeviceAddress` from non-empty `DeviceInventory.management_ip` as `IMPORTED` with a migration source reference; do not claim it was observed.
- Preserve the v12 `management_ip` projection until all readers move to the address API.
- Do not backfill VLANs or network domains by guessing from names, stations, IP prefixes or fuzzy identity.
- Re-running the migration must be idempotent by source reference and deterministic row ID.
- On startup failure, leave v12 readable and record a migration failure; do not partially expose v13 writes.

## Rollback and integrity

Rollback is a controlled read-only/feature-disable procedure, not a destructive drop. The implementation must retain v13 tables until the release is retired, disable v13 writes behind a feature gate, and continue serving v12 projections. A separate approved migration plan must define backup verification, foreign-key checks, duplicate detection, downgrade behavior and retention before any schema code is written.
