-- Kernel schema (ADR 0002, ADR 0010). Applied by esmbler_migrator, the owner. The runtime role esmbler_app
-- gets DML only; every tenant table has FORCE ROW LEVEL SECURITY. Tenant context is transaction-local
-- (set_config(..., true)); a missing context yields NULL and therefore no rows (fail closed).

CREATE SCHEMA IF NOT EXISTS kernel;
GRANT USAGE ON SCHEMA kernel TO esmbler_app;

CREATE FUNCTION kernel.current_tenant() RETURNS uuid LANGUAGE sql STABLE
AS $$ SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid $$;
CREATE FUNCTION kernel.current_environment() RETURNS uuid LANGUAGE sql STABLE
AS $$ SELECT NULLIF(current_setting('app.environment_id', true), '')::uuid $$;
CREATE FUNCTION kernel.current_principal() RETURNS uuid LANGUAGE sql STABLE
AS $$ SELECT NULLIF(current_setting('app.principal_id', true), '')::uuid $$;

-- ---------------------------------------------------------------- tenants and principals (global)
CREATE TABLE kernel.tenants (
    id          uuid PRIMARY KEY,
    slug        text NOT NULL UNIQUE CHECK (slug ~ '^[a-z][a-z0-9-]{1,40}$'),
    name        text NOT NULL,
    status      text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'suspended', 'offboarding')),
    -- OpenFGA store holding this tenant's relationship facts (identity is tenant-wide).
    authz_store_id  text,
    authz_model_id  text,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE kernel.environments (
    id              uuid PRIMARY KEY,
    tenant_id       uuid NOT NULL REFERENCES kernel.tenants (id),
    key             text NOT NULL CHECK (key ~ '^[a-z][a-z0-9-]{1,30}$'),
    name            text NOT NULL,
    kind            text NOT NULL CHECK (kind IN ('production', 'sandbox')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, key)
);

CREATE TABLE kernel.principals (
    id             uuid PRIMARY KEY,
    kind           text NOT NULL CHECK (kind IN ('user', 'agent', 'service', 'module')),
    email          text UNIQUE,
    display_name   text NOT NULL,
    password_hash  text,
    acts_for       uuid REFERENCES kernel.principals (id),
    agent_tenant   uuid REFERENCES kernel.tenants (id),
    disabled       boolean NOT NULL DEFAULT false,
    created_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE kernel.memberships (
    tenant_id     uuid NOT NULL REFERENCES kernel.tenants (id),
    principal_id  uuid NOT NULL REFERENCES kernel.principals (id),
    title         text,
    created_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, principal_id)
);

-- Tenants and environments are visible only for the current tenant or to members (for the tenant switcher).
ALTER TABLE kernel.tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.tenants FORCE ROW LEVEL SECURITY;
CREATE POLICY tenants_visible ON kernel.tenants
    USING (id = kernel.current_tenant()
           OR EXISTS (SELECT 1 FROM kernel.memberships m WHERE m.tenant_id = tenants.id AND m.principal_id = kernel.current_principal()));

ALTER TABLE kernel.environments ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.environments FORCE ROW LEVEL SECURITY;
CREATE POLICY environments_visible ON kernel.environments
    USING (tenant_id = kernel.current_tenant()
           OR EXISTS (SELECT 1 FROM kernel.memberships m WHERE m.tenant_id = environments.tenant_id AND m.principal_id = kernel.current_principal()));

ALTER TABLE kernel.memberships ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.memberships FORCE ROW LEVEL SECURITY;
CREATE POLICY memberships_visible ON kernel.memberships
    USING (tenant_id = kernel.current_tenant() OR principal_id = kernel.current_principal())
    WITH CHECK (tenant_id = kernel.current_tenant());

-- Principals: yourself, members of the current tenant, and agents of the current tenant.
ALTER TABLE kernel.principals ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.principals FORCE ROW LEVEL SECURITY;
CREATE POLICY principals_visible ON kernel.principals
    USING (id = kernel.current_principal()
           OR agent_tenant = kernel.current_tenant()
           OR EXISTS (SELECT 1 FROM kernel.memberships m WHERE m.principal_id = principals.id AND m.tenant_id = kernel.current_tenant()));
CREATE POLICY principals_insert ON kernel.principals FOR INSERT WITH CHECK (true);
CREATE POLICY principals_update ON kernel.principals FOR UPDATE
    USING (id = kernel.current_principal() OR agent_tenant = kernel.current_tenant()
           OR EXISTS (SELECT 1 FROM kernel.memberships m WHERE m.principal_id = principals.id AND m.tenant_id = kernel.current_tenant()));

-- Login needs the password hash before any context exists: a narrow, privileged lookup (owner rights).
CREATE FUNCTION kernel.login_lookup(p_email text)
RETURNS TABLE (id uuid, password_hash text, disabled boolean, display_name text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$ SELECT id, password_hash, disabled, display_name FROM kernel.principals WHERE kind = 'user' AND lower(email) = lower(p_email) $$;
REVOKE ALL ON FUNCTION kernel.login_lookup(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.login_lookup(text) TO esmbler_app;

-- Provisioning a new tenant happens before a tenant context exists for it.
CREATE FUNCTION kernel.provision_tenant(p_id uuid, p_slug text, p_name text)
RETURNS void LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$ INSERT INTO kernel.tenants (id, slug, name) VALUES (p_id, p_slug, p_name) $$;
REVOKE ALL ON FUNCTION kernel.provision_tenant(uuid, text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.provision_tenant(uuid, text, text) TO esmbler_app;

CREATE FUNCTION kernel.find_tenant_by_slug(p_slug text)
RETURNS uuid LANGUAGE sql STABLE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$ SELECT id FROM kernel.tenants WHERE slug = p_slug $$;
REVOKE ALL ON FUNCTION kernel.find_tenant_by_slug(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.find_tenant_by_slug(text) TO esmbler_app;

-- ---------------------------------------------------------------- identity relations (system of record; OpenFGA is a derived index)
CREATE TABLE kernel.groups (
    tenant_id    uuid NOT NULL REFERENCES kernel.tenants (id),
    id           uuid NOT NULL,
    key          text NOT NULL CHECK (key ~ '^[a-z][a-z0-9_]{1,40}$'),
    name         text NOT NULL,
    description  text,
    created_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, key)
);

CREATE TABLE kernel.group_members (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     uuid NOT NULL,
    group_id      uuid NOT NULL,
    principal_id  uuid REFERENCES kernel.principals (id),
    member_group  uuid,
    created_at    timestamptz NOT NULL DEFAULT now(),
    CHECK ((principal_id IS NULL) <> (member_group IS NULL)),
    FOREIGN KEY (tenant_id, group_id) REFERENCES kernel.groups (tenant_id, id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX group_members_unique ON kernel.group_members (tenant_id, group_id, coalesce(principal_id, member_group));

CREATE TABLE kernel.role_assignments (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     uuid NOT NULL REFERENCES kernel.tenants (id),
    role_id       text NOT NULL,
    principal_id  uuid REFERENCES kernel.principals (id),
    group_id      uuid,
    created_at    timestamptz NOT NULL DEFAULT now(),
    created_by    uuid,
    CHECK ((principal_id IS NULL) <> (group_id IS NULL))
);
CREATE UNIQUE INDEX role_assignments_unique ON kernel.role_assignments (tenant_id, role_id, coalesce(principal_id, group_id));

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['groups', 'group_members', 'role_assignments'] LOOP
        EXECUTE format('ALTER TABLE kernel.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE kernel.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY tenant_isolation ON kernel.%I USING (tenant_id = kernel.current_tenant()) WITH CHECK (tenant_id = kernel.current_tenant())', t);
    END LOOP;
END $$;

-- ---------------------------------------------------------------- configuration
CREATE TABLE kernel.installations (
    tenant_id       uuid NOT NULL,
    environment_id  uuid NOT NULL REFERENCES kernel.environments (id),
    module_id       text NOT NULL,
    version         text NOT NULL,
    update_policy   text NOT NULL DEFAULT 'manual' CHECK (update_policy IN ('manual', 'auto_minor')),
    installed_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, module_id)
);

CREATE TABLE kernel.config_artifacts (
    tenant_id       uuid NOT NULL,
    environment_id  uuid NOT NULL REFERENCES kernel.environments (id),
    revision        integer NOT NULL CHECK (revision > 0),
    content_hash    text NOT NULL,
    manifest        jsonb NOT NULL,
    config          jsonb NOT NULL,
    overlay         jsonb NOT NULL,
    status          text NOT NULL CHECK (status IN ('active', 'superseded', 'failed', 'rolled_back')),
    source          text NOT NULL CHECK (source IN ('install', 'publish', 'upgrade', 'rollback')),
    created_by      uuid,
    approved_by     uuid,
    created_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, revision)
);

CREATE TABLE kernel.active_config (
    tenant_id          uuid NOT NULL,
    environment_id     uuid NOT NULL REFERENCES kernel.environments (id),
    revision           integer NOT NULL,
    previous_revision  integer,
    activated_at       timestamptz NOT NULL DEFAULT now(),
    activated_by       uuid,
    PRIMARY KEY (tenant_id, environment_id)
);

CREATE TABLE kernel.drafts (
    tenant_id       uuid NOT NULL,
    environment_id  uuid NOT NULL REFERENCES kernel.environments (id),
    id              uuid NOT NULL,
    base_revision   integer NOT NULL,
    version         integer NOT NULL DEFAULT 0,
    status          text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'published', 'discarded')),
    created_by      uuid NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id)
);
-- One builder at a time: at most one open draft per tenant environment (Gate 1 constraint).
CREATE UNIQUE INDEX drafts_one_open ON kernel.drafts (tenant_id, environment_id) WHERE status = 'open';

CREATE TABLE kernel.draft_commands (
    tenant_id       uuid NOT NULL,
    environment_id  uuid NOT NULL,
    draft_id        uuid NOT NULL,
    seq             integer NOT NULL,
    op              jsonb NOT NULL,
    summary         text NOT NULL,
    targets         text[] NOT NULL DEFAULT '{}',
    actor           uuid NOT NULL,
    agent           uuid,
    source          text NOT NULL CHECK (source IN ('ui', 'mcp', 'api', 'assistant')),
    undone          boolean NOT NULL DEFAULT false,
    created_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, draft_id, seq),
    FOREIGN KEY (tenant_id, environment_id, draft_id) REFERENCES kernel.drafts (tenant_id, environment_id, id)
);

CREATE TABLE kernel.publish_requests (
    tenant_id       uuid NOT NULL,
    environment_id  uuid NOT NULL,
    id              uuid NOT NULL,
    draft_id        uuid NOT NULL,
    draft_version   integer NOT NULL,
    artifact_hash   text NOT NULL,
    summary         text NOT NULL,
    requested_by    uuid NOT NULL,
    requested_agent uuid,
    status          text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'rejected', 'stale')),
    decided_by      uuid,
    decided_at      timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id)
);

-- ---------------------------------------------------------------- records
CREATE TABLE kernel.records (
    tenant_id        uuid NOT NULL,
    environment_id   uuid NOT NULL,
    id               uuid NOT NULL,
    entity_id        text NOT NULL,
    number           bigint,
    revision         integer NOT NULL DEFAULT 1,
    data             jsonb NOT NULL,
    config_revision  integer NOT NULL,
    search           tsvector,
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       uuid NOT NULL,
    updated_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       uuid NOT NULL,
    deleted_at       timestamptz,
    PRIMARY KEY (tenant_id, environment_id, id)
);
CREATE INDEX records_entity_updated ON kernel.records (tenant_id, environment_id, entity_id, updated_at DESC) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX records_entity_number ON kernel.records (tenant_id, environment_id, entity_id, number);
CREATE INDEX records_data ON kernel.records USING gin (data jsonb_path_ops);
CREATE INDEX records_search ON kernel.records USING gin (search);

CREATE TABLE kernel.record_sequences (
    tenant_id       uuid NOT NULL,
    environment_id  uuid NOT NULL,
    entity_id       text NOT NULL,
    next_value      bigint NOT NULL,
    PRIMARY KEY (tenant_id, environment_id, entity_id)
);

CREATE TABLE kernel.activity (
    tenant_id       uuid NOT NULL,
    environment_id  uuid NOT NULL,
    id              uuid NOT NULL,
    record_id       uuid NOT NULL,
    kind            text NOT NULL CHECK (kind IN ('public_reply', 'work_note', 'system')),
    body            text NOT NULL,
    author          uuid,
    agent           uuid,
    created_at      timestamptz NOT NULL DEFAULT now(),
    redacted_at     timestamptz,
    PRIMARY KEY (tenant_id, environment_id, id)
);
CREATE INDEX activity_record ON kernel.activity (tenant_id, environment_id, record_id, created_at);

CREATE TABLE kernel.idempotency_keys (
    tenant_id       uuid NOT NULL,
    environment_id  uuid NOT NULL,
    key             text NOT NULL,
    principal_id    uuid NOT NULL,
    request_hash    text NOT NULL,
    response        jsonb NOT NULL,
    status_code     integer NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, key)
);

-- ---------------------------------------------------------------- audit (append-only for the runtime role)
CREATE TABLE kernel.audit_log (
    tenant_id        uuid NOT NULL,
    environment_id   uuid,
    id               bigint GENERATED ALWAYS AS IDENTITY,
    at               timestamptz NOT NULL DEFAULT now(),
    actor            uuid,
    agent            uuid,
    channel          text NOT NULL,
    operation        text NOT NULL,
    target_type      text NOT NULL,
    target_id        text,
    config_revision  integer,
    outcome          text NOT NULL CHECK (outcome IN ('success', 'denied', 'failed')),
    correlation_id   uuid,
    details          jsonb NOT NULL DEFAULT '{}',
    PRIMARY KEY (tenant_id, id)
);
CREATE INDEX audit_target ON kernel.audit_log (tenant_id, environment_id, target_type, target_id, at DESC);

-- ---------------------------------------------------------------- events, jobs, notifications
CREATE TABLE kernel.outbox (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    environment_id   uuid NOT NULL,
    type             text NOT NULL,
    record_id        uuid,
    record_revision  integer,
    event            jsonb NOT NULL,
    status           text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'dispatched', 'dead')),
    attempts         integer NOT NULL DEFAULT 0,
    available_at     timestamptz NOT NULL DEFAULT now(),
    locked_until     timestamptz,
    locked_by        text,
    last_error       text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    dispatched_at    timestamptz
);
CREATE INDEX outbox_pending ON kernel.outbox (available_at) WHERE status = 'pending';

CREATE TABLE kernel.inbox (
    consumer         text NOT NULL,
    event_id         uuid NOT NULL,
    tenant_id        uuid NOT NULL,
    environment_id   uuid NOT NULL,
    processed_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (consumer, event_id)
);

CREATE TABLE kernel.record_projections (
    tenant_id        uuid NOT NULL,
    environment_id   uuid NOT NULL,
    consumer         text NOT NULL,
    record_id        uuid NOT NULL,
    record_revision  integer NOT NULL,
    state            jsonb NOT NULL,
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, consumer, record_id)
);

CREATE TABLE kernel.jobs (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL,
    environment_id   uuid NOT NULL,
    kind             text NOT NULL,
    payload          jsonb NOT NULL,
    status           text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'running', 'succeeded', 'failed', 'dead', 'cancelled')),
    attempts         integer NOT NULL DEFAULT 0,
    max_attempts     integer NOT NULL DEFAULT 5,
    available_at     timestamptz NOT NULL DEFAULT now(),
    locked_until     timestamptz,
    locked_by        text,
    checkpoint       jsonb,
    last_error       text,
    created_by       uuid,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX jobs_pending ON kernel.jobs (available_at) WHERE status IN ('pending', 'running');

CREATE TABLE kernel.migration_runs (
    tenant_id        uuid NOT NULL,
    environment_id   uuid NOT NULL,
    id               uuid NOT NULL,
    upgrade_id       uuid NOT NULL,
    migration_id     text NOT NULL,
    status           text NOT NULL CHECK (status IN ('pending', 'running', 'completed', 'failed')),
    processed        integer NOT NULL DEFAULT 0,
    converted        integer NOT NULL DEFAULT 0,
    invalid          integer NOT NULL DEFAULT 0,
    checkpoint       uuid,
    report           jsonb NOT NULL DEFAULT '[]',
    last_error       text,
    started_at       timestamptz NOT NULL DEFAULT now(),
    finished_at      timestamptz,
    PRIMARY KEY (tenant_id, environment_id, id)
);

CREATE TABLE kernel.upgrades (
    tenant_id        uuid NOT NULL,
    environment_id   uuid NOT NULL,
    id               uuid NOT NULL,
    module_id        text NOT NULL,
    from_version     text NOT NULL,
    to_version       text NOT NULL,
    status           text NOT NULL CHECK (status IN ('planned', 'migrating', 'failed', 'activated', 'blocked')),
    report           jsonb NOT NULL,
    created_by       uuid NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id)
);

CREATE TABLE kernel.notifications (
    tenant_id        uuid NOT NULL,
    environment_id   uuid NOT NULL,
    id               uuid NOT NULL,
    template_id      text NOT NULL,
    recipient        uuid,
    recipient_email  text,
    subject          text NOT NULL,
    body             text NOT NULL,
    event_id         uuid NOT NULL,
    channel          text NOT NULL DEFAULT 'in_app_fake',
    created_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id),
    UNIQUE (tenant_id, environment_id, event_id, template_id, recipient)
);

-- Tenant + environment isolation for every environment-scoped table.
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['installations', 'config_artifacts', 'active_config', 'drafts', 'draft_commands', 'publish_requests',
                             'records', 'record_sequences', 'activity', 'idempotency_keys', 'outbox', 'inbox', 'record_projections',
                             'jobs', 'migration_runs', 'upgrades', 'notifications'] LOOP
        EXECUTE format('ALTER TABLE kernel.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE kernel.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY tenant_environment_isolation ON kernel.%I USING (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment()) WITH CHECK (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())', t);
    END LOOP;
END $$;

-- Audit: tenant-wide entries have no environment; environment entries must match the current environment.
ALTER TABLE kernel.audit_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.audit_log FORCE ROW LEVEL SECURITY;
CREATE POLICY audit_isolation ON kernel.audit_log
    USING (tenant_id = kernel.current_tenant() AND (environment_id IS NULL OR environment_id = kernel.current_environment()))
    WITH CHECK (tenant_id = kernel.current_tenant() AND (environment_id IS NULL OR environment_id = kernel.current_environment()));

-- Worker claim functions: the only cross-tenant reads. They lock and return a batch; handlers then process each
-- item inside its own tenant-context transaction.
CREATE FUNCTION kernel.claim_outbox(p_worker text, p_batch integer, p_lease_seconds integer)
RETURNS TABLE (id uuid, tenant_id uuid, environment_id uuid, attempts integer)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
BEGIN
    RETURN QUERY
    WITH next AS (
        SELECT o.id FROM kernel.outbox o
        WHERE o.status = 'pending' AND o.available_at <= now() AND (o.locked_until IS NULL OR o.locked_until < now())
        ORDER BY o.available_at, o.created_at
        LIMIT p_batch
        FOR UPDATE SKIP LOCKED
    )
    UPDATE kernel.outbox o SET locked_until = now() + make_interval(secs => p_lease_seconds), locked_by = p_worker, attempts = o.attempts + 1
    FROM next WHERE o.id = next.id
    RETURNING o.id, o.tenant_id, o.environment_id, o.attempts;
END $$;
REVOKE ALL ON FUNCTION kernel.claim_outbox(text, integer, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.claim_outbox(text, integer, integer) TO esmbler_app;

-- Jobs are claimed fairly: at most one job per tenant per claim round, oldest first. Running jobs whose lease
-- expired (worker crashed) are reclaimed and resume from their checkpoint.
CREATE FUNCTION kernel.claim_jobs(p_worker text, p_batch integer, p_lease_seconds integer)
RETURNS TABLE (id uuid, tenant_id uuid, environment_id uuid, kind text, attempts integer)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
BEGIN
    RETURN QUERY
    WITH candidates AS (
        SELECT DISTINCT ON (j.tenant_id) j.id
        FROM kernel.jobs j
        WHERE ((j.status = 'pending' AND j.available_at <= now()) OR (j.status = 'running' AND j.locked_until < now()))
          AND NOT EXISTS (SELECT 1 FROM kernel.jobs r WHERE r.tenant_id = j.tenant_id AND r.status = 'running' AND r.locked_until > now() AND r.id <> j.id)
        ORDER BY j.tenant_id, j.available_at
    ), next AS (
        SELECT j.id FROM kernel.jobs j JOIN candidates c ON c.id = j.id
        ORDER BY j.available_at LIMIT p_batch
        FOR UPDATE OF j SKIP LOCKED
    )
    UPDATE kernel.jobs j SET status = 'running', locked_until = now() + make_interval(secs => p_lease_seconds), locked_by = p_worker,
                             attempts = j.attempts + 1, updated_at = now()
    FROM next WHERE j.id = next.id
    RETURNING j.id, j.tenant_id, j.environment_id, j.kind, j.attempts;
END $$;
REVOKE ALL ON FUNCTION kernel.claim_jobs(text, integer, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.claim_jobs(text, integer, integer) TO esmbler_app;

-- Grants: DML only, no TRUNCATE/REFERENCES/TRIGGER. Audit is insert/select only (append-only).
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA kernel TO esmbler_app;
REVOKE UPDATE, DELETE ON kernel.audit_log FROM esmbler_app;
REVOKE DELETE ON kernel.tenants, kernel.principals, kernel.config_artifacts FROM esmbler_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA kernel TO esmbler_app;

-- Privileged functions run as esmbler_definer, which bypasses RLS but has only the grants below.
-- CREATE is required to own objects in the schema; the role cannot log in.
GRANT USAGE, CREATE ON SCHEMA kernel TO esmbler_definer;
GRANT SELECT ON kernel.principals TO esmbler_definer;
GRANT SELECT, INSERT ON kernel.tenants TO esmbler_definer;
GRANT SELECT, UPDATE ON kernel.outbox, kernel.jobs TO esmbler_definer;
ALTER FUNCTION kernel.login_lookup(text) OWNER TO esmbler_definer;
ALTER FUNCTION kernel.provision_tenant(uuid, text, text) OWNER TO esmbler_definer;
ALTER FUNCTION kernel.find_tenant_by_slug(text) OWNER TO esmbler_definer;
ALTER FUNCTION kernel.claim_outbox(text, integer, integer) OWNER TO esmbler_definer;
ALTER FUNCTION kernel.claim_jobs(text, integer, integer) OWNER TO esmbler_definer;
