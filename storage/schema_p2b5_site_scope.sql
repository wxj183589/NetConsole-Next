-- NetConsole Next P2B5: explicit user-to-Site claims.
-- This auxiliary schema is never registered by Catalog.Initialize. It is created
-- only by the isolated P2B5 migration harness after an explicit test gate.
CREATE TABLE IF NOT EXISTS web_user_site_memberships(
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL REFERENCES web_users(id) ON DELETE CASCADE,
    site_reference TEXT NOT NULL,
    claim_state TEXT NOT NULL CHECK(claim_state IN ('ACTIVE','CONFLICT','REVOKED')),
    source_kind TEXT NOT NULL CHECK(source_kind IN ('DIRECT','GROUP','EXTERNAL')),
    source_reference TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK(revision>0),
    lifecycle TEXT NOT NULL CHECK(lifecycle IN ('ACTIVE','ARCHIVED')),
    valid_from_ms INTEGER,
    valid_to_ms INTEGER,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    CHECK(TRIM(site_reference)<>''),
    CHECK(valid_from_ms IS NULL OR valid_from_ms>=0),
    CHECK(valid_to_ms IS NULL OR valid_to_ms>=0),
    CHECK(valid_from_ms IS NULL OR valid_to_ms IS NULL OR valid_from_ms<valid_to_ms),
    UNIQUE(user_id,site_reference,source_kind,source_reference,revision)
);
CREATE INDEX IF NOT EXISTS idx_web_user_site_memberships_current
    ON web_user_site_memberships(user_id,lifecycle,claim_state,valid_from_ms,valid_to_ms,site_reference);
