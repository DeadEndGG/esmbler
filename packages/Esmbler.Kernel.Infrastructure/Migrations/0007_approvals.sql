-- Approvals (G2-M3, ADR 0016, REQ-BIZ-004, REQ-AUTHZ-011). One approval per request that needs one, one step per
-- approver per stage, and out-of-office delegations. Rows are never deleted: decisions are an audit trail.
CREATE TABLE kernel.approvals (
    tenant_id        uuid        NOT NULL,
    environment_id   uuid        NOT NULL,
    id               uuid        NOT NULL,
    record_id        uuid        NOT NULL,
    entity_id        text        NOT NULL,
    policy_id        text        NOT NULL,
    -- The policy as it was when the approval started; approvals in flight never change with later publishes.
    policy_snapshot  jsonb       NOT NULL,
    status           text        NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'rejected', 'cancelled')),
    stage_index      integer     NOT NULL DEFAULT 0,
    requester        uuid,
    outcome_reason   text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    decided_at       timestamptz,
    PRIMARY KEY (tenant_id, environment_id, id)
);
-- At most one approval in flight per record.
CREATE UNIQUE INDEX approvals_one_pending ON kernel.approvals (tenant_id, environment_id, record_id) WHERE status = 'pending';
CREATE INDEX approvals_record ON kernel.approvals (tenant_id, environment_id, record_id, created_at DESC);

CREATE TABLE kernel.approval_steps (
    tenant_id        uuid        NOT NULL,
    environment_id   uuid        NOT NULL,
    id               uuid        NOT NULL,
    approval_id      uuid        NOT NULL,
    record_id        uuid        NOT NULL,
    stage_index      integer     NOT NULL,
    stage_id         text        NOT NULL,
    -- user (a person), group (any member) or role (any holder).
    approver_kind    text        NOT NULL CHECK (approver_kind IN ('user', 'group', 'role')),
    approver_ref     text        NOT NULL,
    status           text        NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'rejected', 'expired', 'cancelled', 'skipped')),
    decided_by       uuid,
    on_behalf_of     uuid,
    comment          text,
    decided_at       timestamptz,
    due_at           timestamptz,
    escalation       boolean     NOT NULL DEFAULT false,
    created_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id),
    FOREIGN KEY (tenant_id, environment_id, approval_id) REFERENCES kernel.approvals (tenant_id, environment_id, id)
);
CREATE INDEX approval_steps_approval ON kernel.approval_steps (tenant_id, environment_id, approval_id, stage_index);
CREATE INDEX approval_steps_pending ON kernel.approval_steps (tenant_id, environment_id, approver_kind, approver_ref) WHERE status = 'pending';

CREATE TABLE kernel.approval_delegations (
    tenant_id        uuid        NOT NULL,
    environment_id   uuid        NOT NULL,
    id               uuid        NOT NULL,
    delegator        uuid        NOT NULL,
    delegate         uuid        NOT NULL CHECK (delegate <> delegator),
    starts_at        timestamptz NOT NULL,
    ends_at          timestamptz NOT NULL CHECK (ends_at > starts_at),
    revoked_at       timestamptz,
    created_by       uuid        NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id)
);
CREATE INDEX approval_delegations_delegate ON kernel.approval_delegations (tenant_id, environment_id, delegate) WHERE revoked_at IS NULL;
CREATE INDEX approval_delegations_delegator ON kernel.approval_delegations (tenant_id, environment_id, delegator) WHERE revoked_at IS NULL;

ALTER TABLE kernel.approvals ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.approvals FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_environment_isolation ON kernel.approvals
    USING (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())
    WITH CHECK (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment());

ALTER TABLE kernel.approval_steps ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.approval_steps FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_environment_isolation ON kernel.approval_steps
    USING (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())
    WITH CHECK (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment());

ALTER TABLE kernel.approval_delegations ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.approval_delegations FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_environment_isolation ON kernel.approval_delegations
    USING (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())
    WITH CHECK (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment());

GRANT SELECT, INSERT, UPDATE ON kernel.approvals, kernel.approval_steps, kernel.approval_delegations TO esmbler_app;
