-- Alerts (G2-M19, ADR 0022, REQ-ALR-001). Every table is environment-scoped with FORCE RLS.

-- One row per alert. While an alert is active (open, acknowledged or suppressed) its rule and key identify it: later
-- matches count as occurrences on the same row (the unique partial index is the deduplication guarantee).
CREATE TABLE kernel.alerts (
    tenant_id         uuid        NOT NULL,
    environment_id    uuid        NOT NULL,
    id                uuid        NOT NULL,
    rule_id           text        NOT NULL,
    dedup_key         text        NOT NULL,
    status            text        NOT NULL CHECK (status IN ('open', 'acknowledged', 'resolved', 'suppressed')),
    severity          text        NOT NULL,
    title             text        NOT NULL,
    entity_id         text,
    record_id         uuid,
    owner_group       uuid,
    owner_group_key   text,
    owner_principal   uuid,
    assignee          uuid,
    level             integer     NOT NULL DEFAULT 1 CHECK (level BETWEEN 1 AND 100),
    occurrences       integer     NOT NULL DEFAULT 1,
    reopened_count    integer     NOT NULL DEFAULT 0,
    first_event_id    uuid        NOT NULL,
    last_event_id     uuid        NOT NULL,
    suppression_id    uuid,
    opened_at         timestamptz NOT NULL,
    last_occurred_at  timestamptz NOT NULL,
    acknowledged_at   timestamptz,
    acknowledged_by   uuid,
    resolved_at       timestamptz,
    resolved_by       uuid,
    resolution        text CHECK (resolution IN ('manual', 'auto')),
    config_revision   integer     NOT NULL,
    version           integer     NOT NULL DEFAULT 1,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id)
);
CREATE UNIQUE INDEX alerts_active_key ON kernel.alerts (tenant_id, environment_id, rule_id, dedup_key) WHERE status IN ('open', 'acknowledged', 'suppressed');
CREATE INDEX alerts_status ON kernel.alerts (tenant_id, environment_id, status, opened_at DESC);
CREATE INDEX alerts_record ON kernel.alerts (tenant_id, environment_id, record_id) WHERE record_id IS NOT NULL;
CREATE INDEX alerts_resolved ON kernel.alerts (tenant_id, environment_id, rule_id, dedup_key, resolved_at DESC) WHERE status = 'resolved';

-- The timeline of an alert, append-only.
CREATE TABLE kernel.alert_activity (
    tenant_id       uuid        NOT NULL,
    environment_id  uuid        NOT NULL,
    id              uuid        NOT NULL,
    alert_id        uuid        NOT NULL,
    kind            text        NOT NULL CHECK (kind IN ('opened', 'occurred', 'acknowledged', 'assigned', 'escalated', 'resolved', 'reopened', 'suppressed', 'unsuppressed')),
    actor           uuid,
    agent           uuid,
    level           integer,
    details         jsonb,
    at              timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id)
);
CREATE INDEX alert_activity_alert ON kernel.alert_activity (tenant_id, environment_id, alert_id, at);

-- Maintenance windows (runtime data, not configuration): alerts raised inside one are recorded but not announced.
CREATE TABLE kernel.alert_suppressions (
    tenant_id       uuid        NOT NULL,
    environment_id  uuid        NOT NULL,
    id              uuid        NOT NULL,
    reason          text        NOT NULL CHECK (length(reason) BETWEEN 1 AND 500),
    rule_id         text,
    entity_id       text,
    record_id       uuid,
    starts_at       timestamptz NOT NULL,
    ends_at         timestamptz NOT NULL CHECK (ends_at > starts_at AND ends_at <= starts_at + interval '14 days'),
    created_by      uuid        NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    cancelled_at    timestamptz,
    cancelled_by    uuid,
    PRIMARY KEY (tenant_id, environment_id, id)
);
CREATE INDEX alert_suppressions_window ON kernel.alert_suppressions (tenant_id, environment_id, ends_at) WHERE cancelled_at IS NULL;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['alerts', 'alert_activity', 'alert_suppressions'] LOOP
        EXECUTE format('ALTER TABLE kernel.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE kernel.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY tenant_environment_isolation ON kernel.%I
            USING (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())
            WITH CHECK (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())', t);
    END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE ON kernel.alerts TO esmbler_app;
GRANT SELECT, INSERT ON kernel.alert_activity TO esmbler_app;
GRANT SELECT, INSERT ON kernel.alert_suppressions TO esmbler_app;
GRANT UPDATE (cancelled_at, cancelled_by) ON kernel.alert_suppressions TO esmbler_app;
