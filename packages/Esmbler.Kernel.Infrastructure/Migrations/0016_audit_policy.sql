-- Audit policy (G2-M19, ADR 0022, REQ-AUD-001): a per-workspace hash chain for tamper evidence, retention with legal
-- holds, and retention of notification deliveries, the inbox and resolved alerts (KI-024).
--
-- The chain is sealed asynchronously by kernel.seal_audit, never inside business transactions: a chain head locked by
-- every audited write would serialize each workspace and could deadlock with record locks. Chain order is seal order.

ALTER TABLE kernel.audit_log ADD COLUMN chain_seq bigint;
ALTER TABLE kernel.audit_log ADD COLUMN prev_hash bytea;
ALTER TABLE kernel.audit_log ADD COLUMN hash bytea;
CREATE UNIQUE INDEX audit_chain ON kernel.audit_log (tenant_id, chain_seq) WHERE chain_seq IS NOT NULL;
CREATE INDEX audit_unsealed ON kernel.audit_log (tenant_id, id) WHERE chain_seq IS NULL;
CREATE INDEX audit_search ON kernel.audit_log (tenant_id, environment_id, at DESC);

-- The runtime role writes audit rows but can never preset chain columns; it has no UPDATE or DELETE (0001).
REVOKE INSERT ON kernel.audit_log FROM esmbler_app;
GRANT INSERT (tenant_id, environment_id, at, actor, agent, channel, operation, target_type, target_id, config_revision, outcome, correlation_id, details) ON kernel.audit_log TO esmbler_app;

-- Sealed rows are immutable for everyone, the sealer included: an update may only set the chain columns, once.
CREATE FUNCTION kernel.audit_log_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = kernel, pg_temp
AS $$
BEGIN
    IF OLD.chain_seq IS NOT NULL
       OR (to_jsonb(NEW) - 'chain_seq' - 'prev_hash' - 'hash') IS DISTINCT FROM (to_jsonb(OLD) - 'chain_seq' - 'prev_hash' - 'hash') THEN
        RAISE EXCEPTION 'audit rows are immutable' USING ERRCODE = '42501';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER audit_log_guard BEFORE UPDATE ON kernel.audit_log FOR EACH ROW EXECUTE FUNCTION kernel.audit_log_guard();

-- What a chain link covers: every column of the row except the chain columns, in a fixed order, timestamps in UTC with
-- microseconds. Exports carry this text so the chain can be verified offline.
CREATE FUNCTION kernel.audit_canonical(r kernel.audit_log) RETURNS text
LANGUAGE sql STABLE SET search_path = kernel, pg_temp
AS $$
    SELECT jsonb_build_array(r.tenant_id, r.environment_id, r.id, to_char(r.at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
        r.actor, r.agent, r.channel, r.operation, r.target_type, r.target_id, r.config_revision, r.outcome, r.correlation_id, r.details)::text
$$;

CREATE FUNCTION kernel.audit_link(p_prev bytea, r kernel.audit_log) RETURNS bytea
LANGUAGE sql STABLE SET search_path = kernel, pg_temp
AS $$ SELECT sha256(convert_to(encode(p_prev, 'hex') || kernel.audit_canonical(r), 'UTF8')) $$;

CREATE FUNCTION kernel.audit_genesis(p_tenant uuid) RETURNS bytea
LANGUAGE sql IMMUTABLE SET search_path = kernel, pg_temp
AS $$ SELECT sha256(convert_to('esmbler-audit-genesis:' || p_tenant::text, 'UTF8')) $$;

-- One head per workspace: where the chain ends, where it starts after retention (the anchor), and verification state.
CREATE TABLE kernel.audit_chain_heads (
    tenant_id             uuid        PRIMARY KEY,
    last_seq              bigint      NOT NULL DEFAULT 0,
    last_hash             bytea       NOT NULL,
    anchor_seq            bigint      NOT NULL DEFAULT 0,
    anchor_hash           bytea       NOT NULL,
    sealed_at             timestamptz,
    verify_cursor         bigint,
    verify_cursor_hash    bytea,
    verified_through_seq  bigint      NOT NULL DEFAULT 0,
    verified_at           timestamptz,
    broken_at_seq         bigint,
    broken_detected_at    timestamptz
);

CREATE TABLE kernel.audit_policies (
    tenant_id       uuid        PRIMARY KEY,
    retention_days  integer     NOT NULL DEFAULT 2555 CHECK (retention_days BETWEEN 365 AND 3650),
    updated_by      uuid,
    updated_at      timestamptz NOT NULL DEFAULT now()
);

-- A legal hold pauses every purge of the workspace (audit, deliveries, inbox, alerts) until released.
CREATE TABLE kernel.legal_holds (
    tenant_id    uuid        NOT NULL,
    id           uuid        NOT NULL,
    reason       text        NOT NULL CHECK (length(reason) BETWEEN 1 AND 500),
    scope        text        NOT NULL DEFAULT 'all' CHECK (scope = 'all'),
    created_by   uuid        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    released_by  uuid,
    released_at  timestamptz,
    PRIMARY KEY (tenant_id, id)
);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['audit_chain_heads', 'audit_policies', 'legal_holds'] LOOP
        EXECUTE format('ALTER TABLE kernel.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE kernel.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY tenant_isolation ON kernel.%I
            USING (tenant_id = kernel.current_tenant()) WITH CHECK (tenant_id = kernel.current_tenant())', t);
    END LOOP;
END $$;

GRANT SELECT ON kernel.audit_chain_heads TO esmbler_app;
GRANT SELECT, INSERT, UPDATE ON kernel.audit_policies TO esmbler_app;
GRANT SELECT, INSERT ON kernel.legal_holds TO esmbler_app;
GRANT UPDATE (released_by, released_at) ON kernel.legal_holds TO esmbler_app;

GRANT SELECT, INSERT, DELETE ON kernel.audit_log TO esmbler_definer;
GRANT UPDATE (chain_seq, prev_hash, hash) ON kernel.audit_log TO esmbler_definer;
GRANT SELECT, INSERT, UPDATE ON kernel.audit_chain_heads TO esmbler_definer;
GRANT SELECT ON kernel.audit_policies, kernel.legal_holds TO esmbler_definer;
GRANT SELECT, DELETE ON kernel.notification_deliveries, kernel.notification_delivery_attempts, kernel.notifications, kernel.alerts, kernel.alert_activity TO esmbler_definer;
GRANT EXECUTE ON FUNCTION kernel.audit_canonical(kernel.audit_log), kernel.audit_link(bytea, kernel.audit_log), kernel.audit_genesis(uuid) TO esmbler_definer, esmbler_app;

SET ROLE esmbler_definer;

-- Seals committed, unsealed audit rows into each workspace's chain (worker; no tenant argument). A workspace whose
-- chain another sealer holds is skipped this round.
CREATE FUNCTION kernel.seal_audit(p_batch integer)
RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
DECLARE
    t uuid;
    r kernel.audit_log;
    head kernel.audit_chain_heads;
    prev bytea;
    seq bigint;
    sealed integer := 0;
BEGIN
    FOR t IN SELECT DISTINCT a.tenant_id FROM kernel.audit_log a WHERE a.chain_seq IS NULL LIMIT 100 LOOP
        IF NOT pg_try_advisory_xact_lock(hashtextextended('audit_chain:' || t::text, 0)) THEN
            CONTINUE;
        END IF;
        INSERT INTO kernel.audit_chain_heads (tenant_id, last_hash, anchor_hash) VALUES (t, kernel.audit_genesis(t), kernel.audit_genesis(t))
            ON CONFLICT (tenant_id) DO NOTHING;
        SELECT * INTO head FROM kernel.audit_chain_heads h WHERE h.tenant_id = t FOR UPDATE;
        prev := head.last_hash;
        seq := head.last_seq;
        FOR r IN SELECT * FROM kernel.audit_log a WHERE a.tenant_id = t AND a.chain_seq IS NULL ORDER BY a.id LIMIT GREATEST(1, LEAST(p_batch, 10000)) LOOP
            seq := seq + 1;
            UPDATE kernel.audit_log a SET chain_seq = seq, prev_hash = prev, hash = kernel.audit_link(prev, r) WHERE a.tenant_id = t AND a.id = r.id;
            prev := kernel.audit_link(prev, r);
            sealed := sealed + 1;
        END LOOP;
        UPDATE kernel.audit_chain_heads h SET last_seq = seq, last_hash = prev, sealed_at = now() WHERE h.tenant_id = t;
    END LOOP;
    RETURN sealed;
END $$;

-- Verifies the current workspace's chain (tenant from the session context, never an argument), up to p_limit links per
-- call, continuing where the last call stopped. Returns counts and positions only, never row contents.
CREATE FUNCTION kernel.verify_audit_chain(p_limit integer)
RETURNS TABLE (checked integer, verified_through bigint, broken_at bigint, complete boolean)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
DECLARE
    t uuid := kernel.current_tenant();
    head kernel.audit_chain_heads;
    r kernel.audit_log;
    expected_seq bigint;
    expected bytea;
    n integer := 0;
    broken bigint;
BEGIN
    IF t IS NULL THEN
        RAISE EXCEPTION 'no workspace context' USING ERRCODE = '42501';
    END IF;
    SELECT * INTO head FROM kernel.audit_chain_heads h WHERE h.tenant_id = t FOR UPDATE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 0, 0::bigint, NULL::bigint, true;
        RETURN;
    END IF;
    expected_seq := coalesce(head.verify_cursor, head.anchor_seq);
    expected := coalesce(head.verify_cursor_hash, head.anchor_hash);
    FOR r IN SELECT * FROM kernel.audit_log a WHERE a.tenant_id = t AND a.chain_seq > expected_seq AND a.chain_seq <= head.last_seq ORDER BY a.chain_seq LIMIT GREATEST(1, LEAST(p_limit, 100000)) LOOP
        IF r.chain_seq <> expected_seq + 1 OR r.prev_hash IS DISTINCT FROM expected OR r.hash IS DISTINCT FROM kernel.audit_link(expected, r) THEN
            broken := CASE WHEN r.chain_seq <> expected_seq + 1 THEN expected_seq + 1 ELSE r.chain_seq END;
            EXIT;
        END IF;
        expected_seq := r.chain_seq;
        expected := r.hash;
        n := n + 1;
    END LOOP;
    IF broken IS NULL AND n < GREATEST(1, LEAST(p_limit, 100000)) AND (expected_seq <> head.last_seq OR expected IS DISTINCT FROM head.last_hash) THEN
        broken := expected_seq + 1; -- rows missing at the end of the chain
    END IF;
    IF broken IS NOT NULL THEN
        UPDATE kernel.audit_chain_heads h SET broken_at_seq = broken, broken_detected_at = now(), verify_cursor = NULL, verify_cursor_hash = NULL WHERE h.tenant_id = t;
        RETURN QUERY SELECT n, expected_seq, broken, true;
    ELSIF expected_seq = head.last_seq THEN
        UPDATE kernel.audit_chain_heads h SET verified_through_seq = expected_seq, verified_at = now(), verify_cursor = NULL, verify_cursor_hash = NULL WHERE h.tenant_id = t;
        RETURN QUERY SELECT n, expected_seq, NULL::bigint, true;
    ELSE
        UPDATE kernel.audit_chain_heads h SET verify_cursor = expected_seq, verify_cursor_hash = expected WHERE h.tenant_id = t;
        RETURN QUERY SELECT n, expected_seq, NULL::bigint, false;
    END IF;
END $$;

-- Workspaces whose chain was not verified in the last day (worker).
CREATE FUNCTION kernel.audit_verification_due(p_batch integer)
RETURNS SETOF uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
    SELECT h.tenant_id FROM kernel.audit_chain_heads h
    WHERE h.last_seq > h.anchor_seq AND (h.verified_at IS NULL OR h.verified_at < now() - interval '1 day' OR h.verify_cursor IS NOT NULL)
    ORDER BY h.verified_at NULLS FIRST LIMIT GREATEST(1, LEAST(p_batch, 1000))
$$;

-- Retention (worker): removes what each workspace's policy lets go, never during a legal hold, never below the floors
-- whatever the caller passes, at most p_batch rows per workspace and kind per call. Audit rows go only as a sealed prefix
-- of the chain, and the anchor moves with them so the rest still verifies. Every purge is audited in the same
-- transaction.
CREATE FUNCTION kernel.purge_retention(p_delivery_days integer, p_inbox_read_days integer, p_inbox_unread_days integer, p_alert_days integer, p_batch integer)
RETURNS TABLE (tenant_id uuid, kind text, removed integer)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
DECLARE
    t uuid;
    batch integer := GREATEST(1, LEAST(p_batch, 10000));
    n integer;
    keep_days integer;
    head kernel.audit_chain_heads;
    through bigint;
BEGIN
    IF NOT pg_try_advisory_xact_lock(hashtext('kernel.purge_retention')) THEN
        RETURN;
    END IF;
    FOR t IN SELECT tn.id FROM kernel.tenants tn LOOP
        IF EXISTS (SELECT 1 FROM kernel.legal_holds lh WHERE lh.tenant_id = t AND lh.released_at IS NULL) THEN
            CONTINUE;
        END IF;

        -- Audit: the sealed prefix older than the policy (at least one year).
        SELECT GREATEST(coalesce((SELECT p.retention_days FROM kernel.audit_policies p WHERE p.tenant_id = t), 2555), 365) INTO keep_days;
        SELECT * INTO head FROM kernel.audit_chain_heads h WHERE h.tenant_id = t FOR UPDATE;
        IF FOUND THEN
            SELECT LEAST(
                head.anchor_seq + batch,
                coalesce((SELECT min(a.chain_seq) - 1 FROM kernel.audit_log a WHERE a.tenant_id = t AND a.chain_seq > head.anchor_seq AND a.at >= now() - make_interval(days => keep_days)), head.last_seq))
            INTO through;
            IF through > head.anchor_seq THEN
                UPDATE kernel.audit_chain_heads h
                SET anchor_seq = through, anchor_hash = (SELECT a.hash FROM kernel.audit_log a WHERE a.tenant_id = t AND a.chain_seq = through),
                    verify_cursor = NULL, verify_cursor_hash = NULL
                WHERE h.tenant_id = t;
                DELETE FROM kernel.audit_log a WHERE a.tenant_id = t AND a.chain_seq IS NOT NULL AND a.chain_seq <= through;
                GET DIAGNOSTICS n = ROW_COUNT;
                INSERT INTO kernel.audit_log (tenant_id, environment_id, channel, operation, target_type, target_id, outcome, details)
                VALUES (t, NULL, 'system', 'retention.purged', 'audit_log', NULL, 'success', jsonb_build_object('removed', n, 'through_seq', through, 'retention_days', keep_days));
                tenant_id := t; kind := 'audit'; removed := n; RETURN NEXT;
            END IF;
        END IF;

        -- Notification deliveries in a final state, and their attempts.
        WITH old AS (
            SELECT d.environment_id, d.id FROM kernel.notification_deliveries d
            WHERE d.tenant_id = t AND d.status IN ('sent', 'failed', 'ambiguous', 'captured', 'suppressed')
              AND d.updated_at < now() - make_interval(days => GREATEST(p_delivery_days, 30))
            LIMIT batch
        ), attempts AS (
            DELETE FROM kernel.notification_delivery_attempts x USING old WHERE x.tenant_id = t AND x.environment_id = old.environment_id AND x.delivery_id = old.id RETURNING 1
        ), gone AS (
            DELETE FROM kernel.notification_deliveries d USING old WHERE d.tenant_id = t AND d.environment_id = old.environment_id AND d.id = old.id RETURNING 1
        )
        SELECT count(*) INTO n FROM gone;
        IF n > 0 THEN
            INSERT INTO kernel.audit_log (tenant_id, environment_id, channel, operation, target_type, outcome, details)
            VALUES (t, NULL, 'system', 'retention.purged', 'notification_delivery', 'success', jsonb_build_object('removed', n));
            tenant_id := t; kind := 'deliveries'; removed := n; RETURN NEXT;
        END IF;

        -- Inbox: read items after a while, unread ones after a long while.
        WITH old AS (
            SELECT i.environment_id, i.id FROM kernel.notifications i
            WHERE i.tenant_id = t AND ((i.read_at IS NOT NULL AND i.read_at < now() - make_interval(days => GREATEST(p_inbox_read_days, 7)))
                OR (i.read_at IS NULL AND i.created_at < now() - make_interval(days => GREATEST(p_inbox_unread_days, 90))))
            LIMIT batch
        ), gone AS (
            DELETE FROM kernel.notifications i USING old WHERE i.tenant_id = t AND i.environment_id = old.environment_id AND i.id = old.id RETURNING 1
        )
        SELECT count(*) INTO n FROM gone;
        IF n > 0 THEN
            INSERT INTO kernel.audit_log (tenant_id, environment_id, channel, operation, target_type, outcome, details)
            VALUES (t, NULL, 'system', 'retention.purged', 'notification', 'success', jsonb_build_object('removed', n));
            tenant_id := t; kind := 'inbox'; removed := n; RETURN NEXT;
        END IF;

        -- Resolved alerts and their timelines.
        WITH old AS (
            SELECT al.environment_id, al.id FROM kernel.alerts al
            WHERE al.tenant_id = t AND al.status = 'resolved' AND al.resolved_at < now() - make_interval(days => GREATEST(p_alert_days, 90))
            LIMIT batch
        ), activity AS (
            DELETE FROM kernel.alert_activity x USING old WHERE x.tenant_id = t AND x.environment_id = old.environment_id AND x.alert_id = old.id RETURNING 1
        ), gone AS (
            DELETE FROM kernel.alerts al USING old WHERE al.tenant_id = t AND al.environment_id = old.environment_id AND al.id = old.id RETURNING 1
        )
        SELECT count(*) INTO n FROM gone;
        IF n > 0 THEN
            INSERT INTO kernel.audit_log (tenant_id, environment_id, channel, operation, target_type, outcome, details)
            VALUES (t, NULL, 'system', 'retention.purged', 'alert', 'success', jsonb_build_object('removed', n));
            tenant_id := t; kind := 'alerts'; removed := n; RETURN NEXT;
        END IF;
    END LOOP;
END $$;

RESET ROLE;

REVOKE ALL ON FUNCTION kernel.seal_audit(integer), kernel.verify_audit_chain(integer), kernel.audit_verification_due(integer),
    kernel.purge_retention(integer, integer, integer, integer, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.seal_audit(integer), kernel.verify_audit_chain(integer), kernel.audit_verification_due(integer),
    kernel.purge_retention(integer, integer, integer, integer, integer) TO esmbler_app;
