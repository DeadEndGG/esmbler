-- SLA timers (G2-M4, ADR 0018, REQ-BIZ-005, REQ-MIG-004). One row per record, SLA target and cycle (a reopened
-- record that restarts its target gets a new cycle). Each timer keeps a snapshot of the target, goal and business
-- calendar it started with, so later policy or calendar publishes never change a running timer or a recorded breach.
-- Rows are never deleted: they are the history breach reports read.
CREATE TABLE kernel.sla_timers (
    tenant_id          uuid        NOT NULL,
    environment_id     uuid        NOT NULL,
    id                 uuid        NOT NULL,
    record_id          uuid        NOT NULL,
    entity_id          text        NOT NULL,
    policy_id          text        NOT NULL,
    target_id          text        NOT NULL,
    cycle              integer     NOT NULL DEFAULT 0,
    -- Configuration revision the timer started under, so breach reports can group by policy version.
    config_revision    integer,
    snapshot           jsonb       NOT NULL,
    goal_seconds       bigint      NOT NULL CHECK (goal_seconds > 0),
    status             text        NOT NULL CHECK (status IN ('running', 'paused', 'stopped', 'cancelled')),
    started_at         timestamptz NOT NULL,
    elapsed_seconds    bigint      NOT NULL DEFAULT 0,
    segment_started_at timestamptz,
    due_at             timestamptz,
    warn_at            timestamptz,
    warned_at          timestamptz,
    breached_at        timestamptz,
    stopped_at         timestamptz,
    outcome            text        CHECK (outcome IN ('met', 'breached')),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id),
    UNIQUE (tenant_id, environment_id, record_id, policy_id, target_id, cycle)
);
CREATE INDEX sla_timers_record ON kernel.sla_timers (tenant_id, environment_id, record_id);
-- Breach and at-risk reports (dashboards, G2-M9).
CREATE INDEX sla_timers_open ON kernel.sla_timers (tenant_id, environment_id, entity_id, due_at) WHERE status IN ('running', 'paused');
CREATE INDEX sla_timers_breached ON kernel.sla_timers (tenant_id, environment_id, entity_id, breached_at) WHERE breached_at IS NOT NULL;

ALTER TABLE kernel.sla_timers ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.sla_timers FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_environment_isolation ON kernel.sla_timers
    USING (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())
    WITH CHECK (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment());
GRANT SELECT, INSERT, UPDATE ON kernel.sla_timers TO esmbler_app;
