-- Event platform v2 (G2-M17, ADR 0017, REQ-EVT-009). Each consumer of an event now runs in its own transaction: a
-- failing consumer records its failure here, retries and dead-letters on its own, and never holds back the other
-- consumers of the same event. Rows are per tenant and environment like everything else.
CREATE TABLE kernel.consumer_failures (
    tenant_id        uuid        NOT NULL,
    environment_id   uuid        NOT NULL,
    id               uuid        NOT NULL,
    consumer         text        NOT NULL,
    event_id         uuid        NOT NULL,
    event_type       text        NOT NULL,
    attempts         integer     NOT NULL DEFAULT 0,
    dead             boolean     NOT NULL DEFAULT false,
    last_error       text,
    first_failed_at  timestamptz NOT NULL DEFAULT now(),
    last_failed_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id),
    UNIQUE (consumer, event_id)
);
CREATE INDEX consumer_failures_dead ON kernel.consumer_failures (tenant_id, environment_id) WHERE dead;

ALTER TABLE kernel.consumer_failures ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.consumer_failures FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_environment_isolation ON kernel.consumer_failures
    USING (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())
    WITH CHECK (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment());
GRANT SELECT, INSERT, UPDATE, DELETE ON kernel.consumer_failures TO esmbler_app;

-- Outbox retention: dispatched events are kept for a replay window, then removed with their inbox marks and consumer
-- failures. Runs as the definer (the app role has no DELETE on the outbox); only fully dispatched events older than
-- the window go (never one a consumer dead-lettered), and only the marks of those events, so a pending or dead event
-- never loses its duplicate protection.
GRANT DELETE ON kernel.outbox, kernel.inbox, kernel.consumer_failures TO esmbler_definer;
GRANT SELECT ON kernel.inbox, kernel.consumer_failures TO esmbler_definer;
SET ROLE esmbler_definer;
CREATE FUNCTION kernel.purge_outbox(p_days integer)
RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
DECLARE removed integer;
BEGIN
    -- One purge at a time across workers; the replay window never drops below seven days whoever calls it; at most
    -- 10,000 events per call, so a large backlog is removed over several runs instead of one long statement.
    IF NOT pg_try_advisory_xact_lock(hashtext('kernel.purge_outbox')) THEN
        RETURN 0;
    END IF;
    WITH gone AS (
        DELETE FROM kernel.outbox o
        WHERE o.id IN (
          SELECT c.id FROM kernel.outbox c
          WHERE c.status = 'dispatched' AND c.dispatched_at < now() - make_interval(days => GREATEST(p_days, 7))
          -- An event with a dead consumer stays until an operator replays it: the dead letter must not expire.
            AND NOT EXISTS (SELECT 1 FROM kernel.consumer_failures f WHERE f.event_id = c.id AND f.dead)
          LIMIT 10000)
        RETURNING o.id
    ), marks AS (
        DELETE FROM kernel.inbox i USING gone WHERE i.event_id = gone.id RETURNING 1
    ), failures AS (
        DELETE FROM kernel.consumer_failures f USING gone WHERE f.event_id = gone.id RETURNING 1
    )
    SELECT count(*) INTO removed FROM gone;
    RETURN removed;
END $$;
RESET ROLE;
REVOKE ALL ON FUNCTION kernel.purge_outbox(integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.purge_outbox(integer) TO esmbler_app;
