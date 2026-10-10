-- Notification service (G2-M18, ADR 0021, REQ-NOT-001..007). Every table is environment-scoped with FORCE RLS.
--
-- kernel.notifications stays the in-app inbox (the in_app channel writes it); it gains read state and links.
ALTER TABLE kernel.notifications ADD COLUMN read_at timestamptz;
ALTER TABLE kernel.notifications ADD COLUMN delivery_id uuid;
ALTER TABLE kernel.notifications ADD COLUMN severity text;
ALTER TABLE kernel.notifications ADD COLUMN entity_id text;
ALTER TABLE kernel.notifications ADD COLUMN record_id uuid;
CREATE INDEX notifications_inbox ON kernel.notifications (tenant_id, environment_id, recipient, created_at DESC);

-- Channel instances are environment settings, never promoted between environments (ADR 0021). Credentials are
-- secret references (names), never values; sandbox instances start in capture mode.
CREATE TABLE kernel.notification_channels (
    tenant_id        uuid        NOT NULL,
    environment_id   uuid        NOT NULL,
    id               uuid        NOT NULL,
    key              text        NOT NULL CHECK (key ~ '^[a-z][a-z0-9_]{1,40}$'),
    type             text        NOT NULL CHECK (type IN ('email', 'sms')),
    label            text        NOT NULL,
    mode             text        NOT NULL CHECK (mode IN ('live', 'capture', 'disabled')),
    settings         jsonb       NOT NULL DEFAULT '{}',
    secret_refs      jsonb       NOT NULL DEFAULT '{}',
    rate_per_minute  integer     NOT NULL DEFAULT 60 CHECK (rate_per_minute BETWEEN 1 AND 10000),
    version          integer     NOT NULL DEFAULT 1,
    updated_by       uuid,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id),
    UNIQUE (tenant_id, environment_id, key)
);

-- One row per recipient and channel for an event and route. The idempotency key (event, person or address, channel)
-- deduplicates across routes and layers; the rendered text is stored so retries and replays send the same content.
CREATE TABLE kernel.notification_deliveries (
    tenant_id            uuid        NOT NULL,
    environment_id       uuid        NOT NULL,
    id                   uuid        NOT NULL,
    event_id             uuid        NOT NULL,
    event_type           text        NOT NULL,
    subscription_id      text        NOT NULL,
    route_id             text        NOT NULL,
    template_id          text        NOT NULL,
    profile_id           text        NOT NULL,
    channel_type         text        NOT NULL CHECK (channel_type IN ('in_app', 'email', 'sms')),
    channel_key          text,
    recipient_principal  uuid,
    recipient_address    text,
    external             boolean     NOT NULL DEFAULT false,
    mandatory            boolean     NOT NULL DEFAULT false,
    severity             text,
    entity_id            text,
    record_id            uuid,
    status               text        NOT NULL CHECK (status IN ('queued', 'sending', 'sent', 'failed', 'ambiguous', 'captured', 'suppressed')),
    status_reason        text,
    attempts             integer     NOT NULL DEFAULT 0,
    idempotency_key      text        NOT NULL,
    provider_message_id  text,
    subject              text        NOT NULL,
    body                 text        NOT NULL,
    config_revision      integer     NOT NULL,
    replay_of            uuid,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    sent_at              timestamptz,
    PRIMARY KEY (tenant_id, environment_id, id),
    UNIQUE (tenant_id, environment_id, idempotency_key)
);
CREATE INDEX notification_deliveries_event ON kernel.notification_deliveries (tenant_id, environment_id, event_id);
CREATE INDEX notification_deliveries_status ON kernel.notification_deliveries (tenant_id, environment_id, status, created_at DESC);
CREATE INDEX notification_deliveries_rate ON kernel.notification_deliveries (tenant_id, environment_id, channel_key, sent_at) WHERE sent_at IS NOT NULL;

-- Every send attempt, append-only: the delivery history people inspect (not audit; ADR 0017).
CREATE TABLE kernel.notification_delivery_attempts (
    tenant_id        uuid        NOT NULL,
    environment_id   uuid        NOT NULL,
    id               uuid        NOT NULL,
    delivery_id      uuid        NOT NULL,
    attempt          integer     NOT NULL,
    outcome          text        NOT NULL CHECK (outcome IN ('sent', 'transient', 'permanent', 'unknown', 'captured', 'suppressed')),
    code             text,
    provider_message_id text,
    at               timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, id)
);
CREATE INDEX notification_delivery_attempts_delivery ON kernel.notification_delivery_attempts (tenant_id, environment_id, delivery_id);

-- People's preferences (the user layer): mute a route or a channel; mandatory routes ignore mutes.
CREATE TABLE kernel.notification_preferences (
    tenant_id        uuid        NOT NULL,
    environment_id   uuid        NOT NULL,
    principal_id     uuid        NOT NULL,
    route_id         text        NOT NULL,
    channel_type     text        NOT NULL CHECK (channel_type IN ('*', 'in_app', 'email', 'sms')),
    muted            boolean     NOT NULL DEFAULT true,
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, environment_id, principal_id, route_id, channel_type)
);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['notification_channels', 'notification_deliveries', 'notification_delivery_attempts', 'notification_preferences'] LOOP
        EXECUTE format('ALTER TABLE kernel.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE kernel.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY tenant_environment_isolation ON kernel.%I
            USING (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())
            WITH CHECK (tenant_id = kernel.current_tenant() AND environment_id = kernel.current_environment())', t);
    END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE ON kernel.notification_channels, kernel.notification_deliveries TO esmbler_app;
GRANT SELECT, INSERT ON kernel.notification_delivery_attempts TO esmbler_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON kernel.notification_preferences TO esmbler_app;
