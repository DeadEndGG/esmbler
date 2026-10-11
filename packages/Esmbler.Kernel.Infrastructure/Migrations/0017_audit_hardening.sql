-- Audit hardening (G2-M19 review, ADR 0022): retention never removes entries it cannot prove untouched, workspaces are
-- sealed fairly and in a fixed lock order, one workspace's problem never stops another's purge, and the retention floors
-- equal the defaults whatever the caller passes.

SET ROLE esmbler_definer;

-- Seals committed, unsealed audit rows into each workspace's chain. Workspaces with the oldest unsealed rows go first
-- (no starvation), in tenant order within a round (one lock order for every sealer).
CREATE OR REPLACE FUNCTION kernel.seal_audit(p_batch integer)
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
    FOR t IN
        SELECT w.tenant_id FROM (
            SELECT a.tenant_id, min(a.id) AS oldest FROM kernel.audit_log a WHERE a.chain_seq IS NULL GROUP BY a.tenant_id ORDER BY 2 LIMIT 100
        ) w ORDER BY w.tenant_id
    LOOP
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

-- Verifies the current workspace's chain (tenant from the session context), up to p_limit links per call. A break is
-- recorded once with its position and counts as a verification (so it is not re-walked every hour).
CREATE OR REPLACE FUNCTION kernel.verify_audit_chain(p_limit integer)
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
    lim integer := GREATEST(1, LEAST(p_limit, 100000));
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
    FOR r IN SELECT * FROM kernel.audit_log a WHERE a.tenant_id = t AND a.chain_seq > expected_seq AND a.chain_seq <= head.last_seq ORDER BY a.chain_seq LIMIT lim LOOP
        IF r.chain_seq <> expected_seq + 1 OR r.prev_hash IS DISTINCT FROM expected OR r.hash IS DISTINCT FROM kernel.audit_link(expected, r) THEN
            broken := CASE WHEN r.chain_seq <> expected_seq + 1 THEN expected_seq + 1 ELSE r.chain_seq END;
            EXIT;
        END IF;
        expected_seq := r.chain_seq;
        expected := r.hash;
        n := n + 1;
    END LOOP;
    IF broken IS NULL AND n < lim AND (expected_seq <> head.last_seq OR expected IS DISTINCT FROM head.last_hash) THEN
        broken := expected_seq + 1;
    END IF;
    IF broken IS NOT NULL THEN
        UPDATE kernel.audit_chain_heads h
        SET broken_at_seq = broken, broken_detected_at = CASE WHEN h.broken_at_seq = broken THEN h.broken_detected_at ELSE now() END,
            verified_at = now(), verify_cursor = NULL, verify_cursor_hash = NULL
        WHERE h.tenant_id = t;
        RETURN QUERY SELECT n, expected_seq, broken, true;
    ELSIF expected_seq = head.last_seq THEN
        UPDATE kernel.audit_chain_heads h SET verified_through_seq = expected_seq, verified_at = now(), verify_cursor = NULL, verify_cursor_hash = NULL WHERE h.tenant_id = t;
        RETURN QUERY SELECT n, expected_seq, NULL::bigint, true;
    ELSE
        UPDATE kernel.audit_chain_heads h SET verify_cursor = expected_seq, verify_cursor_hash = expected WHERE h.tenant_id = t;
        RETURN QUERY SELECT n, expected_seq, NULL::bigint, false;
    END IF;
END $$;

-- Retention (worker). Audit entries go only as a prefix that was verified (never past verified_through_seq, never at or
-- after a known break) and whose links are re-checked here before deletion: an entry changed to look old is reported as a
-- break and kept, never silently purged. Each workspace runs in its own sub-transaction, so one workspace's problem never
-- stops another's retention. The floors equal the defaults whatever the caller passes.
CREATE OR REPLACE FUNCTION kernel.purge_retention(p_delivery_days integer, p_inbox_read_days integer, p_inbox_unread_days integer, p_alert_days integer, p_batch integer)
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
    r kernel.audit_log;
    expected bytea;
    expected_seq bigint;
    bad bigint;
BEGIN
    IF NOT pg_try_advisory_xact_lock(hashtext('kernel.purge_retention')) THEN
        RETURN;
    END IF;
    FOR t IN SELECT tn.id FROM kernel.tenants tn ORDER BY tn.id LOOP
        BEGIN
            IF EXISTS (SELECT 1 FROM kernel.legal_holds lh WHERE lh.tenant_id = t AND lh.released_at IS NULL) THEN
                CONTINUE;
            END IF;

            SELECT GREATEST(coalesce((SELECT p.retention_days FROM kernel.audit_policies p WHERE p.tenant_id = t), 2555), 365) INTO keep_days;
            SELECT * INTO head FROM kernel.audit_chain_heads h WHERE h.tenant_id = t FOR UPDATE SKIP LOCKED;
            IF FOUND THEN
                SELECT LEAST(
                    head.anchor_seq + batch,
                    head.verified_through_seq,
                    coalesce(head.broken_at_seq - 1, head.last_seq),
                    coalesce((SELECT min(a.chain_seq) - 1 FROM kernel.audit_log a WHERE a.tenant_id = t AND a.chain_seq > head.anchor_seq AND a.at >= now() - make_interval(days => keep_days)), head.last_seq))
                INTO through;
                IF through > head.anchor_seq THEN
                    -- Re-check every link that is about to go.
                    expected := head.anchor_hash;
                    expected_seq := head.anchor_seq;
                    bad := NULL;
                    FOR r IN SELECT * FROM kernel.audit_log a WHERE a.tenant_id = t AND a.chain_seq > head.anchor_seq AND a.chain_seq <= through ORDER BY a.chain_seq LOOP
                        IF r.chain_seq <> expected_seq + 1 OR r.prev_hash IS DISTINCT FROM expected OR r.hash IS DISTINCT FROM kernel.audit_link(expected, r) THEN
                            bad := expected_seq + 1;
                            EXIT;
                        END IF;
                        expected := r.hash;
                        expected_seq := r.chain_seq;
                    END LOOP;
                    IF bad IS NULL AND expected_seq <> through THEN
                        bad := expected_seq + 1;
                    END IF;
                    IF bad IS NOT NULL THEN
                        UPDATE kernel.audit_chain_heads h SET broken_at_seq = bad, broken_detected_at = now() WHERE h.tenant_id = t;
                        INSERT INTO kernel.audit_log (tenant_id, environment_id, channel, operation, target_type, outcome, details)
                        VALUES (t, NULL, 'system', 'audit.chain_broken', 'audit_log', 'failed', jsonb_build_object('brokenAt', bad, 'during', 'retention'));
                    ELSE
                        UPDATE kernel.audit_chain_heads h SET anchor_seq = through, anchor_hash = expected, verify_cursor = NULL, verify_cursor_hash = NULL WHERE h.tenant_id = t;
                        DELETE FROM kernel.audit_log a WHERE a.tenant_id = t AND a.chain_seq IS NOT NULL AND a.chain_seq <= through;
                        GET DIAGNOSTICS n = ROW_COUNT;
                        INSERT INTO kernel.audit_log (tenant_id, environment_id, channel, operation, target_type, outcome, details)
                        VALUES (t, NULL, 'system', 'retention.purged', 'audit_log', 'success', jsonb_build_object('removed', n, 'through_seq', through, 'retention_days', keep_days));
                        tenant_id := t; kind := 'audit'; removed := n; RETURN NEXT;
                    END IF;
                END IF;
            END IF;

            WITH old AS (
                SELECT d.environment_id, d.id FROM kernel.notification_deliveries d
                WHERE d.tenant_id = t AND d.status IN ('sent', 'failed', 'ambiguous', 'captured', 'suppressed')
                  AND d.updated_at < now() - make_interval(days => GREATEST(p_delivery_days, 90))
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

            WITH old AS (
                SELECT i.environment_id, i.id FROM kernel.notifications i
                WHERE i.tenant_id = t AND ((i.read_at IS NOT NULL AND i.read_at < now() - make_interval(days => GREATEST(p_inbox_read_days, 30)))
                    OR (i.read_at IS NULL AND i.created_at < now() - make_interval(days => GREATEST(p_inbox_unread_days, 365))))
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

            WITH old AS (
                SELECT al.environment_id, al.id FROM kernel.alerts al
                WHERE al.tenant_id = t AND al.status = 'resolved' AND al.resolved_at < now() - make_interval(days => GREATEST(p_alert_days, 365))
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
        EXCEPTION WHEN OTHERS THEN
            -- One workspace's problem (a lock, a constraint) is skipped this round; the others continue.
            RAISE WARNING 'retention skipped workspace %: %', t, SQLSTATE;
        END;
    END LOOP;
END $$;

RESET ROLE;
