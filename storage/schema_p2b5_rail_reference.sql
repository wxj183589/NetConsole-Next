-- NetConsole Next P2B5: isolated rail reference data authority.
-- This auxiliary schema is applied only by the explicit isolated migration
-- harness. It never creates or infers device, Site, address or VLAN facts.

CREATE TABLE IF NOT EXISTS p2b5_rail_reference_imports(
    id TEXT PRIMARY KEY,
    actor TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    source_name TEXT NOT NULL,
    source_revision INTEGER NOT NULL CHECK(source_revision > 0),
    source_hash TEXT NOT NULL,
    scope_reference TEXT NOT NULL,
    schema_version TEXT NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('PREVIEWED','SUCCEEDED','CONFLICT','FAILED')),
    request_json TEXT NOT NULL,
    preview_json TEXT NOT NULL,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    expires_ms INTEGER NOT NULL,
    UNIQUE(actor,idempotency_key)
);
CREATE INDEX IF NOT EXISTS idx_p2b5_rail_imports_actor_state
    ON p2b5_rail_reference_imports(actor,state,updated_ms);

CREATE TABLE IF NOT EXISTS p2b5_rail_entities(
    entity_id TEXT PRIMARY KEY,
    entity_type TEXT NOT NULL CHECK(entity_type IN ('LINE','SECTION','STATION','SITE')),
    name TEXT NOT NULL,
    scope_reference TEXT NOT NULL,
    source_name TEXT NOT NULL,
    source_revision INTEGER NOT NULL CHECK(source_revision > 0),
    source_hash TEXT NOT NULL,
    effective_at TEXT NOT NULL,
    captured_at TEXT NOT NULL,
    lifecycle TEXT NOT NULL CHECK(lifecycle IN ('ACTIVE','RETIRED')),
    imported_by TEXT NOT NULL,
    import_id TEXT NOT NULL REFERENCES p2b5_rail_reference_imports(id),
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_p2b5_rail_entities_scope_type
    ON p2b5_rail_entities(scope_reference,entity_type,lifecycle,entity_id);

CREATE TABLE IF NOT EXISTS p2b5_rail_relations(
    relation_kind TEXT NOT NULL CHECK(relation_kind IN ('LINE_SECTION','SECTION_STATION','STATION_SITE','LINE_STATION')),
    parent_id TEXT NOT NULL,
    child_id TEXT NOT NULL,
    scope_reference TEXT NOT NULL,
    source_name TEXT NOT NULL,
    source_revision INTEGER NOT NULL CHECK(source_revision > 0),
    source_hash TEXT NOT NULL,
    import_id TEXT NOT NULL REFERENCES p2b5_rail_reference_imports(id),
    created_ms INTEGER NOT NULL,
    PRIMARY KEY(relation_kind,parent_id,child_id,scope_reference)
);
CREATE INDEX IF NOT EXISTS idx_p2b5_rail_relations_scope
    ON p2b5_rail_relations(scope_reference,relation_kind,parent_id,child_id);
