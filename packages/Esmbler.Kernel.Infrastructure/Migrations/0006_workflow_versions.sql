-- Versioned workflow definitions (G2-M2, ADR 0015, REQ-MIG-003). Each published change to a workflow's state field,
-- initial state, states or transitions registers a new version; records pin the version they run on. Rows are
-- append-only: old versions stay as long as records may pin them.
CREATE TABLE kernel.workflow_definitions (
    tenant_id        uuid        NOT NULL,
    environment_id   uuid        NOT NULL,
    workflow_id      text        NOT NULL,
    version          integer     NOT NULL CHECK (version > 0),
    content_hash     text        NOT NULL,
    definition       jsonb       NOT NULL,
    config_revision  integer     NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, workflow_id, version),
    -- A definition that reverts to earlier content (rollback) reuses that version instead of minting a new one.
    UNIQUE (tenant_id, environment_id, workflow_id, content_hash)
);

ALTER TABLE kernel.workflow_definitions ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.workflow_definitions FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_environment_isolation ON kernel.workflow_definitions
    USING (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())
    WITH CHECK (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment());
GRANT SELECT, INSERT ON kernel.workflow_definitions TO esmbler_app;

-- The workflow definition version a record runs on. NULL only for records created before this migration; the first
-- activation after it pins them to the definition they were running.
ALTER TABLE kernel.records ADD COLUMN workflow_version integer;
CREATE INDEX records_workflow_version ON kernel.records (tenant_id, environment_id, entity_id, workflow_version) WHERE deleted_at IS NULL;
