-- AI providers per workspace (G2-M20, ADR 0024). Settings and usage are workspace-wide (one budget across
-- environments), with FORCE RLS on the tenant. Keys are never stored here: a setting names a model from the operator's
-- catalog, whose credential the host resolves from its own configuration.

CREATE TABLE kernel.ai_settings (
    tenant_id            uuid          NOT NULL PRIMARY KEY,
    model_id             text          CHECK (model_id ~ '^[a-z0-9][a-z0-9._-]{0,80}$'),
    effort               text          CHECK (effort IN ('low', 'medium', 'high', 'max')),
    daily_budget_usd     numeric(10,2) NOT NULL DEFAULT 20 CHECK (daily_budget_usd BETWEEN 0 AND 100000),
    allow_records_read   boolean       NOT NULL DEFAULT false,
    external_assistants  text          NOT NULL DEFAULT 'off' CHECK (external_assistants IN ('off', 'non_production', 'all')),
    version              integer       NOT NULL DEFAULT 1,
    updated_by           uuid,
    updated_at           timestamptz   NOT NULL DEFAULT now()
);

-- One row per model request: what it used and what it cost (estimated from the catalog's prices). Append-only.
CREATE TABLE kernel.ai_usage (
    tenant_id        uuid          NOT NULL,
    id               uuid          NOT NULL,
    environment_id   uuid,
    principal_id     uuid,
    conversation_id  text,
    provider         text          NOT NULL,
    model            text          NOT NULL,
    input_tokens     integer       NOT NULL DEFAULT 0,
    output_tokens    integer       NOT NULL DEFAULT 0,
    cached_tokens    integer       NOT NULL DEFAULT 0,
    cost_usd         numeric(12,6) NOT NULL DEFAULT 0,
    at               timestamptz   NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, id)
);
CREATE INDEX ai_usage_day ON kernel.ai_usage (tenant_id, at DESC);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['ai_settings', 'ai_usage'] LOOP
        EXECUTE format('ALTER TABLE kernel.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE kernel.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY tenant_isolation ON kernel.%I
            USING (tenant_id = kernel.current_tenant()) WITH CHECK (tenant_id = kernel.current_tenant())', t);
    END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE ON kernel.ai_settings TO esmbler_app;
GRANT SELECT, INSERT ON kernel.ai_usage TO esmbler_app;
