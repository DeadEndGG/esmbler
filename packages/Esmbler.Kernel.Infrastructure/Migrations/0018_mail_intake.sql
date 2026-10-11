-- Email intake (G2-M5, ADR 0023, REQ-BIZ-003). Mailboxes are environment settings (never promoted, like channel
-- instances); inbound messages are stored once per mailbox and message (deduplication), processed by a job, and kept
-- with a reason when suspended for people to review. Every table is environment-scoped with FORCE RLS.

-- A mailbox points at a mail intake of the published configuration. Webhook mailboxes are reached through a secret
-- token (only its SHA-256 is stored); IMAP mailboxes are polled by the worker with credentials named by secret references.
CREATE TABLE kernel.mail_inboxes (
    tenant_id        uuid        NOT NULL,
    environment_id   uuid        NOT NULL,
    id               uuid        NOT NULL,
    key              text        NOT NULL CHECK (key ~ '^[a-z][a-z0-9_]{1,40}$'),
    label            text        NOT NULL,
    address          text        NOT NULL CHECK (length(address) BETWEEN 3 AND 254),
    intake_id        text        NOT NULL,
    mode             text        NOT NULL CHECK (mode IN ('webhook', 'imap', 'disabled')),
    settings         jsonb       NOT NULL DEFAULT '{}',
    token_hash       bytea       UNIQUE,
    imap_uid_validity bigint,
    imap_last_uid    bigint,
    polled_at        timestamptz,
    poll_error       text,
    version          integer     NOT NULL DEFAULT 1,
    updated_by       uuid,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id),
    UNIQUE (tenant_id, environment_id, key)
);

-- One row per mailbox and message (Message-ID, or a hash of the content when there is none): a provider that delivers
-- twice, or a poll that sees a message again, never makes a second record. The raw message is kept only until it is
-- processed or decided; suspended messages keep it for review.
CREATE TABLE kernel.inbound_messages (
    tenant_id        uuid        NOT NULL,
    environment_id   uuid        NOT NULL,
    id               uuid        NOT NULL,
    inbox_id         uuid        NOT NULL,
    message_key      text        NOT NULL,
    message_id       text,
    from_address     text,
    from_name        text,
    subject          text,
    status           text        NOT NULL CHECK (status IN ('received', 'processed', 'suspended', 'rejected', 'failed')),
    reason           text,
    outcome          text CHECK (outcome IN ('created', 'replied', 'reopened', 'follow_up')),
    record_id        uuid,
    entity_id        text,
    activity_id      uuid,
    raw              bytea,
    details          jsonb       NOT NULL DEFAULT '{}',
    size_bytes       integer     NOT NULL DEFAULT 0,
    decided_by       uuid,
    received_at      timestamptz NOT NULL DEFAULT now(),
    processed_at     timestamptz,
    PRIMARY KEY (tenant_id, environment_id, id),
    UNIQUE (tenant_id, environment_id, inbox_id, message_key)
);
CREATE INDEX inbound_messages_status ON kernel.inbound_messages (tenant_id, environment_id, status, received_at DESC);
CREATE INDEX inbound_messages_sender ON kernel.inbound_messages (tenant_id, environment_id, inbox_id, lower(from_address), received_at DESC);
CREATE INDEX inbound_messages_message_id ON kernel.inbound_messages (tenant_id, environment_id, message_id) WHERE message_id IS NOT NULL AND record_id IS NOT NULL;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['mail_inboxes', 'inbound_messages'] LOOP
        EXECUTE format('ALTER TABLE kernel.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE kernel.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY tenant_environment_isolation ON kernel.%I
            USING (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())
            WITH CHECK (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())', t);
    END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE ON kernel.mail_inboxes, kernel.inbound_messages TO esmbler_app;

-- Replies that came by email say so in the conversation.
ALTER TABLE kernel.activity ADD COLUMN source text CHECK (source IN ('email'));

-- Outbound email threading: the Message-ID of each email delivery (opaque, random), so a reply's In-Reply-To or
-- References finds the delivery's record. Once deliveries are purged (90 days), the record number in the subject is
-- the fallback.
ALTER TABLE kernel.notification_deliveries ADD COLUMN message_id text;
CREATE INDEX notification_deliveries_message_id ON kernel.notification_deliveries (tenant_id, environment_id, message_id) WHERE message_id IS NOT NULL;

-- The definer functions below read mailboxes and purge old messages; nothing else.
GRANT SELECT ON kernel.mail_inboxes TO esmbler_definer;
GRANT SELECT, UPDATE (raw), DELETE ON kernel.inbound_messages TO esmbler_definer;

SET ROLE esmbler_definer;

-- The webhook endpoint has no session: the token alone names one enabled webhook mailbox of an active workspace.
CREATE FUNCTION kernel.mail_inbox_by_token(p_token_hash bytea)
RETURNS TABLE (tenant_id uuid, environment_id uuid, inbox_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
    SELECT i.tenant_id, i.environment_id, i.id FROM kernel.mail_inboxes i
    JOIN kernel.tenants t ON t.id = i.tenant_id AND t.status = 'active'
    WHERE p_token_hash IS NOT NULL AND length(p_token_hash) = 32 AND i.token_hash = p_token_hash AND i.mode = 'webhook'
$$;

-- IMAP mailboxes the worker should poll now (not polled within p_seconds), least recently polled first.
CREATE FUNCTION kernel.mail_inboxes_due(p_seconds integer, p_batch integer)
RETURNS TABLE (tenant_id uuid, environment_id uuid, inbox_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
    SELECT i.tenant_id, i.environment_id, i.id FROM kernel.mail_inboxes i
    JOIN kernel.tenants t ON t.id = i.tenant_id AND t.status = 'active'
    WHERE i.mode = 'imap' AND (i.polled_at IS NULL OR i.polled_at < now() - make_interval(secs => GREATEST(p_seconds, 30)))
    ORDER BY i.polled_at NULLS FIRST LIMIT GREATEST(1, LEAST(p_batch, 200))
$$;

-- Inbound mail retention (worker): processed and rejected messages lose their raw content after a day and go after
-- p_days (at least 30); suspended ones nobody decided go after p_days too. Never during a legal hold; audited.
CREATE FUNCTION kernel.purge_inbound_mail(p_days integer, p_batch integer)
RETURNS TABLE (tenant_id uuid, removed integer)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
DECLARE
    t uuid;
    batch integer := GREATEST(1, LEAST(p_batch, 10000));
    n integer;
BEGIN
    IF NOT pg_try_advisory_xact_lock(hashtext('kernel.purge_inbound_mail')) THEN
        RETURN;
    END IF;
    FOR t IN SELECT tn.id FROM kernel.tenants tn ORDER BY tn.id LOOP
        BEGIN
            IF EXISTS (SELECT 1 FROM kernel.legal_holds lh WHERE lh.tenant_id = t AND lh.released_at IS NULL) THEN
                CONTINUE;
            END IF;

            UPDATE kernel.inbound_messages m SET raw = NULL
            WHERE m.tenant_id = t AND m.raw IS NOT NULL AND m.status IN ('processed', 'rejected') AND m.processed_at < now() - interval '1 day';

            WITH old AS (
                SELECT m.environment_id, m.id FROM kernel.inbound_messages m
                WHERE m.tenant_id = t AND m.status IN ('processed', 'rejected', 'suspended', 'failed')
                  AND m.received_at < now() - make_interval(days => GREATEST(p_days, 30))
                LIMIT batch
            ), gone AS (
                DELETE FROM kernel.inbound_messages m USING old WHERE m.tenant_id = t AND m.environment_id = old.environment_id AND m.id = old.id RETURNING 1
            )
            SELECT count(*) INTO n FROM gone;
            IF n > 0 THEN
                INSERT INTO kernel.audit_log (tenant_id, environment_id, channel, operation, target_type, outcome, details)
                VALUES (t, NULL, 'system', 'retention.purged', 'inbound_message', 'success', jsonb_build_object('removed', n));
                tenant_id := t; removed := n; RETURN NEXT;
            END IF;
        EXCEPTION WHEN OTHERS THEN
            RAISE WARNING 'inbound mail retention skipped workspace %: %', t, SQLSTATE;
        END;
    END LOOP;
END $$;

RESET ROLE;

REVOKE ALL ON FUNCTION kernel.mail_inbox_by_token(bytea), kernel.mail_inboxes_due(integer, integer), kernel.purge_inbound_mail(integer, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.mail_inbox_by_token(bytea), kernel.mail_inboxes_due(integer, integer), kernel.purge_inbound_mail(integer, integer) TO esmbler_app;
