-- Refresh tokens (ADR 0011): opaque, stored hashed, rotated on use, bound to one principal. Visible only to that
-- principal's context. The access token (15 minutes) carries the tenant/environment; the refresh token does not.
CREATE TABLE kernel.refresh_tokens (
    id            uuid PRIMARY KEY,
    principal_id  uuid NOT NULL REFERENCES kernel.principals (id),
    token_hash    text NOT NULL UNIQUE,
    tenant_id     uuid,
    environment_id uuid,
    expires_at    timestamptz NOT NULL,
    revoked_at    timestamptz,
    replaced_by   uuid,
    created_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX refresh_tokens_principal ON kernel.refresh_tokens (principal_id);

ALTER TABLE kernel.refresh_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE kernel.refresh_tokens FORCE ROW LEVEL SECURITY;
CREATE POLICY refresh_tokens_owner ON kernel.refresh_tokens
    USING (principal_id = kernel.current_principal())
    WITH CHECK (principal_id = kernel.current_principal());

-- Redeeming a refresh token happens before the principal is known: look it up by hash only.
CREATE FUNCTION kernel.refresh_lookup(p_hash text)
RETURNS TABLE (id uuid, principal_id uuid, tenant_id uuid, environment_id uuid, expires_at timestamptz, revoked_at timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$ SELECT id, principal_id, tenant_id, environment_id, expires_at, revoked_at FROM kernel.refresh_tokens WHERE token_hash = p_hash $$;
REVOKE ALL ON FUNCTION kernel.refresh_lookup(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.refresh_lookup(text) TO esmbler_app;
GRANT SELECT ON kernel.refresh_tokens TO esmbler_definer;
ALTER FUNCTION kernel.refresh_lookup(text) OWNER TO esmbler_definer;

GRANT SELECT, INSERT, UPDATE ON kernel.refresh_tokens TO esmbler_app;
