-- OAuth for external MCP clients (G2-M20, ADR 0025). A connection is one person's consent for one client (ChatGPT,
-- Claude) in one workspace environment, with its own agent principal. Codes and refresh tokens are stored hashed and
-- bound to their connection. Every table has FORCE RLS on the tenant; the token endpoint, which has no session, finds the
-- workspace of a code or refresh token only through the two definer lookups below.

CREATE TABLE kernel.mcp_connections (
    tenant_id        uuid        NOT NULL,
    id               uuid        NOT NULL,
    environment_id   uuid        NOT NULL,
    principal_id     uuid        NOT NULL,
    agent_id         uuid        NOT NULL,
    client_id        text        NOT NULL CHECK (length(client_id) BETWEEN 8 AND 500),
    client_host      text        NOT NULL,
    client_name      text        NOT NULL,
    scopes           text        NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    expires_at       timestamptz NOT NULL,
    last_used_at     timestamptz,
    revoked_at       timestamptz,
    revoked_by       uuid,
    PRIMARY KEY (tenant_id, id)
);
CREATE INDEX mcp_connections_person ON kernel.mcp_connections (tenant_id, principal_id, created_at DESC);

CREATE TABLE kernel.oauth_codes (
    tenant_id        uuid        NOT NULL,
    id               uuid        NOT NULL,
    connection_id    uuid        NOT NULL,
    code_hash        bytea       NOT NULL UNIQUE,
    request_id       text        NOT NULL UNIQUE,
    code_challenge   text        NOT NULL,
    redirect_uri     text        NOT NULL,
    client_id        text        NOT NULL,
    scopes           text        NOT NULL,
    expires_at       timestamptz NOT NULL,
    used_at          timestamptz,
    PRIMARY KEY (tenant_id, id)
);

CREATE TABLE kernel.oauth_refresh_tokens (
    tenant_id        uuid        NOT NULL,
    id               uuid        NOT NULL,
    connection_id    uuid        NOT NULL,
    token_hash       bytea       NOT NULL UNIQUE,
    created_at       timestamptz NOT NULL DEFAULT now(),
    expires_at       timestamptz NOT NULL,
    revoked_at       timestamptz,
    PRIMARY KEY (tenant_id, id)
);
CREATE INDEX oauth_refresh_connection ON kernel.oauth_refresh_tokens (tenant_id, connection_id);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['mcp_connections', 'oauth_codes', 'oauth_refresh_tokens'] LOOP
        EXECUTE format('ALTER TABLE kernel.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE kernel.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY tenant_isolation ON kernel.%I
            USING (tenant_id = kernel.current_tenant()) WITH CHECK (tenant_id = kernel.current_tenant())', t);
    END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE ON kernel.mcp_connections, kernel.oauth_codes, kernel.oauth_refresh_tokens TO esmbler_app;
GRANT SELECT ON kernel.mcp_connections, kernel.oauth_codes, kernel.oauth_refresh_tokens TO esmbler_definer;

SET ROLE esmbler_definer;

-- The workspace and environment of an authorization code (by its SHA-256), nothing else.
CREATE FUNCTION kernel.oauth_code_lookup(p_hash bytea)
RETURNS TABLE (tenant_id uuid, environment_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
    SELECT c.tenant_id, m.environment_id FROM kernel.oauth_codes c
    JOIN kernel.mcp_connections m ON m.tenant_id = c.tenant_id AND m.id = c.connection_id
    JOIN kernel.tenants t ON t.id = c.tenant_id AND t.status = 'active'
    WHERE p_hash IS NOT NULL AND length(p_hash) = 32 AND c.code_hash = p_hash
$$;

-- The workspace and environment of a refresh token (by its SHA-256), nothing else.
CREATE FUNCTION kernel.oauth_refresh_lookup(p_hash bytea)
RETURNS TABLE (tenant_id uuid, environment_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
    SELECT r.tenant_id, m.environment_id FROM kernel.oauth_refresh_tokens r
    JOIN kernel.mcp_connections m ON m.tenant_id = r.tenant_id AND m.id = r.connection_id
    JOIN kernel.tenants t ON t.id = r.tenant_id AND t.status = 'active'
    WHERE p_hash IS NOT NULL AND length(p_hash) = 32 AND r.token_hash = p_hash
$$;

RESET ROLE;

REVOKE ALL ON FUNCTION kernel.oauth_code_lookup(bytea), kernel.oauth_refresh_lookup(bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.oauth_code_lookup(bytea), kernel.oauth_refresh_lookup(bytea) TO esmbler_app;
