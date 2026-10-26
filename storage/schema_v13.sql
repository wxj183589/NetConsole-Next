-- NetConsole Next V13: additive P2B-2 address, domain and VLAN persistence.
-- This file is executed only by the explicit isolated migration harness.
-- It is intentionally not registered in Catalog.Initialize.

CREATE TABLE IF NOT EXISTS network_domains(
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    domain_kind TEXT NOT NULL CHECK(domain_kind IN ('MANAGEMENT','BUSINESS','OUT_OF_BAND','TRAIN_GROUND','SHARED_SERVICE','OTHER')),
    parent_domain_id TEXT REFERENCES network_domains(id) ON DELETE RESTRICT,
    vrf_name TEXT NOT NULL DEFAULT '',
    source_kind TEXT NOT NULL CHECK(source_kind IN ('PLANNED','OBSERVED','IMPORTED','MANUAL')),
    source_reference TEXT NOT NULL,
    lifecycle TEXT NOT NULL CHECK(lifecycle IN ('ACTIVE','ARCHIVED')),
    scope_resolution TEXT NOT NULL CHECK(scope_resolution IN ('RESOLVED','UNRESOLVED','CONFLICT')),
    revision INTEGER NOT NULL CHECK(revision>0),
    valid_from_ms INTEGER,
    valid_to_ms INTEGER,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    CHECK(parent_domain_id IS NULL OR parent_domain_id<>id),
    CHECK(valid_from_ms IS NULL OR valid_from_ms>=0),
    CHECK(valid_to_ms IS NULL OR valid_to_ms>=0),
    CHECK(valid_from_ms IS NULL OR valid_to_ms IS NULL OR valid_from_ms<valid_to_ms)
);
CREATE INDEX IF NOT EXISTS idx_network_domains_parent ON network_domains(parent_domain_id);
CREATE INDEX IF NOT EXISTS idx_network_domains_scope_state ON network_domains(scope_resolution,lifecycle,updated_ms);
CREATE INDEX IF NOT EXISTS idx_network_domains_source ON network_domains(source_kind,source_reference,revision);

-- Scope references are references to existing or future catalog authorities. The
-- migration never creates fake Station/Line/Section rows.
CREATE TABLE IF NOT EXISTS network_domain_scopes(
    id TEXT PRIMARY KEY,
    network_domain_id TEXT NOT NULL REFERENCES network_domains(id) ON DELETE RESTRICT,
    scope_kind TEXT NOT NULL CHECK(scope_kind IN ('SITE','STATION','LINE','SECTION')),
    scope_reference TEXT NOT NULL,
    resolution TEXT NOT NULL CHECK(resolution IN ('RESOLVED','UNRESOLVED','CONFLICT')),
    source_kind TEXT NOT NULL CHECK(source_kind IN ('PLANNED','OBSERVED','IMPORTED','MANUAL')),
    source_reference TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK(revision>0),
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    UNIQUE(network_domain_id,scope_kind,scope_reference,source_kind,source_reference,revision)
);
CREATE INDEX IF NOT EXISTS idx_network_domain_scopes_reference ON network_domain_scopes(scope_kind,scope_reference,resolution);

CREATE TABLE IF NOT EXISTS device_domain_memberships(
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL REFERENCES device_inventory(id) ON DELETE RESTRICT,
    network_domain_id TEXT NOT NULL REFERENCES network_domains(id) ON DELETE RESTRICT,
    membership_role TEXT NOT NULL CHECK(membership_role IN ('PRIMARY','SECONDARY','SHARED','OTHER')),
    source_kind TEXT NOT NULL CHECK(source_kind IN ('PLANNED','OBSERVED','IMPORTED','MANUAL')),
    source_reference TEXT NOT NULL,
    source_fingerprint TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK(revision>0),
    lifecycle TEXT NOT NULL CHECK(lifecycle IN ('ACTIVE','ARCHIVED')),
    valid_from_ms INTEGER,
    valid_to_ms INTEGER,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    CHECK(valid_from_ms IS NULL OR valid_from_ms>=0),
    CHECK(valid_to_ms IS NULL OR valid_to_ms>=0),
    CHECK(valid_from_ms IS NULL OR valid_to_ms IS NULL OR valid_from_ms<valid_to_ms),
    UNIQUE(device_id,network_domain_id,membership_role,source_kind,source_reference,revision,source_fingerprint)
);
CREATE INDEX IF NOT EXISTS idx_device_domain_memberships_domain ON device_domain_memberships(network_domain_id,lifecycle,updated_ms);
CREATE INDEX IF NOT EXISTS idx_device_domain_memberships_device ON device_domain_memberships(device_id,lifecycle,updated_ms);

CREATE TABLE IF NOT EXISTS device_addresses(
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL REFERENCES device_inventory(id) ON DELETE RESTRICT,
    network_domain_id TEXT REFERENCES network_domains(id) ON DELETE RESTRICT,
    address_family TEXT NOT NULL CHECK(address_family IN ('IPV4','IPV6')),
    normalized_address TEXT NOT NULL,
    prefix_length INTEGER NOT NULL CHECK((address_family='IPV4' AND prefix_length BETWEEN 0 AND 32) OR (address_family='IPV6' AND prefix_length BETWEEN 0 AND 128)),
    gateway_address TEXT,
    vrf_name TEXT NOT NULL DEFAULT '',
    address_role TEXT NOT NULL CHECK(address_role IN ('MANAGEMENT','SERVICE','OUT_OF_BAND','LOOPBACK','OTHER')),
    interface_reference TEXT NOT NULL DEFAULT '',
    source_kind TEXT NOT NULL CHECK(source_kind IN ('PLANNED','OBSERVED','IMPORTED','MANUAL')),
    source_reference TEXT NOT NULL,
    source_fingerprint TEXT NOT NULL,
    observed_at_ms INTEGER,
    revision INTEGER NOT NULL CHECK(revision>0),
    domain_resolution_state TEXT NOT NULL CHECK(domain_resolution_state IN ('RESOLVED','PENDING_DOMAIN','CONFLICT')),
    conflict_state TEXT NOT NULL CHECK(conflict_state IN ('NONE','SOURCE_CONFLICT','ADDRESS_ALREADY_CLAIMED','NETWORK_DOMAIN_NOT_RESOLVED','REVIEW_REQUIRED','HISTORICAL_REUSE_ALLOWED')),
    lifecycle TEXT NOT NULL CHECK(lifecycle IN ('ACTIVE','ARCHIVED')),
    valid_from_ms INTEGER,
    valid_to_ms INTEGER,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    CHECK(observed_at_ms IS NULL OR observed_at_ms>=0),
    CHECK(valid_from_ms IS NULL OR valid_from_ms>=0),
    CHECK(valid_to_ms IS NULL OR valid_to_ms>=0),
    CHECK(valid_from_ms IS NULL OR valid_to_ms IS NULL OR valid_from_ms<valid_to_ms),
    CHECK((domain_resolution_state='RESOLVED' AND network_domain_id IS NOT NULL) OR (domain_resolution_state<>'RESOLVED' AND network_domain_id IS NULL)),
    UNIQUE(device_id,source_kind,source_reference,revision,source_fingerprint)
);
CREATE INDEX IF NOT EXISTS idx_device_addresses_device ON device_addresses(device_id,lifecycle,updated_ms);
CREATE INDEX IF NOT EXISTS idx_device_addresses_domain_address ON device_addresses(network_domain_id,address_family,normalized_address,lifecycle);
CREATE INDEX IF NOT EXISTS idx_device_addresses_source ON device_addresses(source_kind,source_reference,revision);

-- One claim row is retained for every source fact. There is deliberately no
-- global or prefix-based UNIQUE(address) constraint: overlap is a scoped rule.
CREATE TABLE IF NOT EXISTS device_address_claims(
    id TEXT PRIMARY KEY,
    device_address_id TEXT NOT NULL UNIQUE REFERENCES device_addresses(id) ON DELETE CASCADE,
    device_id TEXT NOT NULL REFERENCES device_inventory(id) ON DELETE RESTRICT,
    network_domain_id TEXT REFERENCES network_domains(id) ON DELETE RESTRICT,
    address_family TEXT NOT NULL CHECK(address_family IN ('IPV4','IPV6')),
    normalized_address TEXT NOT NULL,
    valid_from_ms INTEGER,
    valid_to_ms INTEGER,
    lifecycle TEXT NOT NULL CHECK(lifecycle IN ('ACTIVE','ARCHIVED')),
    claim_state TEXT NOT NULL CHECK(claim_state IN ('ACTIVE','PENDING_DOMAIN','CONFLICT','HISTORICAL')),
    conflict_code TEXT NOT NULL CHECK(conflict_code IN ('NONE','ADDRESS_ALREADY_CLAIMED','NETWORK_DOMAIN_NOT_RESOLVED','SOURCE_CONFLICT','REVIEW_REQUIRED','HISTORICAL_REUSE_ALLOWED')),
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    CHECK(valid_from_ms IS NULL OR valid_from_ms>=0),
    CHECK(valid_to_ms IS NULL OR valid_to_ms>=0),
    CHECK(valid_from_ms IS NULL OR valid_to_ms IS NULL OR valid_from_ms<valid_to_ms),
    CHECK((claim_state='ACTIVE' AND network_domain_id IS NOT NULL) OR claim_state<>'ACTIVE')
);
CREATE INDEX IF NOT EXISTS idx_device_address_claims_scope ON device_address_claims(network_domain_id,address_family,normalized_address,claim_state);
CREATE INDEX IF NOT EXISTS idx_device_address_claims_device ON device_address_claims(device_id,claim_state,updated_ms);

CREATE TABLE IF NOT EXISTS legacy_address_resolution(
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL REFERENCES device_inventory(id) ON DELETE RESTRICT,
    device_address_id TEXT NOT NULL UNIQUE REFERENCES device_addresses(id) ON DELETE RESTRICT,
    terminal_profile_id TEXT,
    management_ip TEXT NOT NULL,
    normalized_address TEXT NOT NULL,
    source_reference TEXT NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('LEGACY_ADDRESS_PENDING_DOMAIN','LEGACY_ADDRESS_RESOLVED','LEGACY_ADDRESS_CONFLICT')),
    network_domain_id TEXT REFERENCES network_domains(id) ON DELETE RESTRICT,
    conflict_detail TEXT NOT NULL DEFAULT '',
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    CHECK((state='LEGACY_ADDRESS_RESOLVED' AND network_domain_id IS NOT NULL) OR (state<>'LEGACY_ADDRESS_RESOLVED' AND network_domain_id IS NULL)),
    UNIQUE(device_id,source_reference)
);
CREATE INDEX IF NOT EXISTS idx_legacy_address_resolution_state ON legacy_address_resolution(state,updated_ms);
CREATE INDEX IF NOT EXISTS idx_legacy_address_resolution_ip ON legacy_address_resolution(normalized_address,state);

CREATE TABLE IF NOT EXISTS vlan_plans(
    id TEXT PRIMARY KEY,
    network_domain_id TEXT NOT NULL REFERENCES network_domains(id) ON DELETE RESTRICT,
    vlan_id INTEGER NOT NULL CHECK(vlan_id BETWEEN 1 AND 4094),
    name TEXT NOT NULL DEFAULT '',
    purpose TEXT NOT NULL DEFAULT '',
    tagging_mode TEXT NOT NULL CHECK(tagging_mode IN ('ACCESS','TRUNK','HYBRID','QINQ')),
    native_vlan_id INTEGER CHECK(native_vlan_id IS NULL OR native_vlan_id BETWEEN 1 AND 4094),
    pvid INTEGER CHECK(pvid IS NULL OR pvid BETWEEN 1 AND 4094),
    qinq_outer_id INTEGER CHECK(qinq_outer_id IS NULL OR qinq_outer_id BETWEEN 1 AND 4094),
    qinq_inner_id INTEGER CHECK(qinq_inner_id IS NULL OR qinq_inner_id BETWEEN 1 AND 4094),
    scope_resolution TEXT NOT NULL CHECK(scope_resolution IN ('RESOLVED','UNRESOLVED','CONFLICT')),
    source_kind TEXT NOT NULL CHECK(source_kind IN ('PLANNED','OBSERVED','IMPORTED','MANUAL')),
    source_reference TEXT NOT NULL,
    source_fingerprint TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK(revision>0),
    lifecycle TEXT NOT NULL CHECK(lifecycle IN ('ACTIVE','ARCHIVED')),
    valid_from_ms INTEGER,
    valid_to_ms INTEGER,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    CHECK(valid_from_ms IS NULL OR valid_from_ms>=0),
    CHECK(valid_to_ms IS NULL OR valid_to_ms>=0),
    CHECK(valid_from_ms IS NULL OR valid_to_ms IS NULL OR valid_from_ms<valid_to_ms),
    CHECK((tagging_mode='ACCESS' AND pvid IS NOT NULL AND pvid=vlan_id AND (native_vlan_id IS NULL OR native_vlan_id=vlan_id) AND qinq_outer_id IS NULL AND qinq_inner_id IS NULL) OR
          (tagging_mode IN ('TRUNK','HYBRID') AND (native_vlan_id IS NULL OR pvid IS NULL OR native_vlan_id=pvid) AND qinq_outer_id IS NULL AND qinq_inner_id IS NULL) OR
           (tagging_mode='QINQ' AND qinq_outer_id IS NOT NULL AND qinq_outer_id=vlan_id AND qinq_inner_id IS NOT NULL AND native_vlan_id IS NULL AND pvid IS NULL)),
    UNIQUE(network_domain_id,source_kind,source_reference,revision,source_fingerprint)
);
CREATE INDEX IF NOT EXISTS idx_vlan_plans_scope ON vlan_plans(network_domain_id,vlan_id,lifecycle,valid_from_ms,valid_to_ms);
CREATE INDEX IF NOT EXISTS idx_vlan_plans_source ON vlan_plans(source_kind,source_reference,revision);

CREATE TABLE IF NOT EXISTS vlan_assignments(
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL REFERENCES device_inventory(id) ON DELETE RESTRICT,
    network_domain_id TEXT NOT NULL REFERENCES network_domains(id) ON DELETE RESTRICT,
    vlan_plan_id TEXT REFERENCES vlan_plans(id) ON DELETE RESTRICT,
    interface_reference TEXT NOT NULL DEFAULT '',
    vlan_id INTEGER NOT NULL CHECK(vlan_id BETWEEN 1 AND 4094),
    assignment_role TEXT NOT NULL CHECK(assignment_role IN ('ACCESS','TRUNK','HYBRID','QINQ','OTHER')),
    tagging_mode TEXT NOT NULL CHECK(tagging_mode IN ('ACCESS','TRUNK','HYBRID','QINQ')),
    native_vlan_id INTEGER CHECK(native_vlan_id IS NULL OR native_vlan_id BETWEEN 1 AND 4094),
    pvid INTEGER CHECK(pvid IS NULL OR pvid BETWEEN 1 AND 4094),
    qinq_outer_id INTEGER CHECK(qinq_outer_id IS NULL OR qinq_outer_id BETWEEN 1 AND 4094),
    qinq_inner_id INTEGER CHECK(qinq_inner_id IS NULL OR qinq_inner_id BETWEEN 1 AND 4094),
    source_kind TEXT NOT NULL CHECK(source_kind IN ('PLANNED','OBSERVED','IMPORTED','MANUAL')),
    source_reference TEXT NOT NULL,
    source_fingerprint TEXT NOT NULL,
    observed_at_ms INTEGER,
    revision INTEGER NOT NULL CHECK(revision>0),
    lifecycle TEXT NOT NULL CHECK(lifecycle IN ('ACTIVE','ARCHIVED')),
    valid_from_ms INTEGER,
    valid_to_ms INTEGER,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    CHECK(observed_at_ms IS NULL OR observed_at_ms>=0),
    CHECK(valid_from_ms IS NULL OR valid_from_ms>=0),
    CHECK(valid_to_ms IS NULL OR valid_to_ms>=0),
    CHECK(valid_from_ms IS NULL OR valid_to_ms IS NULL OR valid_from_ms<valid_to_ms),
    CHECK((tagging_mode='ACCESS' AND pvid IS NOT NULL AND pvid=vlan_id AND (native_vlan_id IS NULL OR native_vlan_id=vlan_id) AND qinq_outer_id IS NULL AND qinq_inner_id IS NULL) OR
          (tagging_mode IN ('TRUNK','HYBRID') AND (native_vlan_id IS NULL OR pvid IS NULL OR native_vlan_id=pvid) AND qinq_outer_id IS NULL AND qinq_inner_id IS NULL) OR
          (tagging_mode='QINQ' AND qinq_outer_id IS NOT NULL AND qinq_outer_id=vlan_id AND qinq_inner_id IS NOT NULL AND native_vlan_id IS NULL AND pvid IS NULL)),
    UNIQUE(device_id,network_domain_id,interface_reference,assignment_role,source_kind,source_reference,revision,source_fingerprint)
);
CREATE INDEX IF NOT EXISTS idx_vlan_assignments_device ON vlan_assignments(device_id,network_domain_id,interface_reference,lifecycle);
CREATE INDEX IF NOT EXISTS idx_vlan_assignments_scope ON vlan_assignments(network_domain_id,vlan_id,lifecycle,valid_from_ms,valid_to_ms);

