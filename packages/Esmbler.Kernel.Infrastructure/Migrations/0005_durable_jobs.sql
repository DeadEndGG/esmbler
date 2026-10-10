-- Durable jobs and timers for Gate 2 (G2-M1, ADR 0005, REQ-EVT-005, REQ-EVT-006, REQ-TEN-006, REQ-ACC-11).

-- Jobs can be scheduled idempotently (one live job per dedup key, e.g. an SLA timer per record) and remember the
-- event that caused them, so automation fan-out can be bounded.
ALTER TABLE kernel.jobs ADD COLUMN dedup_key text;
ALTER TABLE kernel.jobs ADD COLUMN causation_id uuid;
ALTER TABLE kernel.jobs ADD COLUMN depth integer NOT NULL DEFAULT 0;
CREATE UNIQUE INDEX jobs_live_dedup ON kernel.jobs (tenant_id, environment_id, dedup_key)
    WHERE dedup_key IS NOT NULL AND status IN ('pending', 'running');
CREATE INDEX jobs_causation ON kernel.jobs (tenant_id, causation_id) WHERE causation_id IS NOT NULL;
CREATE INDEX jobs_status ON kernel.jobs (tenant_id, environment_id, status);

-- Outbox claims interleave tenants: every tenant's oldest event comes before any tenant's second-oldest, so one
-- tenant's backlog cannot delay another tenant's events (REQ-ACC-11). Same signature as before; replaced as its owner.
SET ROLE esmbler_definer;
CREATE OR REPLACE FUNCTION kernel.claim_outbox(p_worker text, p_batch integer, p_lease_seconds integer)
RETURNS TABLE (id uuid, tenant_id uuid, environment_id uuid, attempts integer)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
BEGIN
    RETURN QUERY
    WITH ranked AS (
        SELECT o.id, row_number() OVER (PARTITION BY o.tenant_id ORDER BY o.available_at, o.created_at) AS rn, o.available_at
        FROM kernel.outbox o
        WHERE o.status = 'pending' AND o.available_at <= now() AND (o.locked_until IS NULL OR o.locked_until < now())
    ), next AS (
        SELECT o.id FROM kernel.outbox o JOIN ranked r ON r.id = o.id
        ORDER BY r.rn, r.available_at
        LIMIT p_batch
        FOR UPDATE OF o SKIP LOCKED
    )
    UPDATE kernel.outbox o SET locked_until = now() + make_interval(secs => p_lease_seconds), locked_by = p_worker, attempts = o.attempts + 1
    FROM next WHERE o.id = next.id
    RETURNING o.id, o.tenant_id, o.environment_id, o.attempts;
END $$;
RESET ROLE;
