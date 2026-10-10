-- Self-service workspace hardening (G2-M9b security review, ADR 0020, threat model T-72).
-- A self-service workspace starts in 'provisioning' and becomes 'active' only when provisioning finished, so a failed
-- run never shows a broken workspace in the switcher. Only active workspaces, and provisioning ones younger than 15
-- minutes (still running), count toward the per-person limit. Only an enabled person can create workspaces, and the
-- function fails closed on missing arguments.
ALTER TABLE kernel.tenants DROP CONSTRAINT tenants_status_check;
ALTER TABLE kernel.tenants ADD CONSTRAINT tenants_status_check CHECK (status IN ('provisioning', 'active', 'suspended', 'offboarding'));
GRANT UPDATE (status) ON kernel.tenants TO esmbler_definer;

-- Same signature as in 0012; replaced as its owner (like 0005 and 0009).
SET ROLE esmbler_definer;
CREATE OR REPLACE FUNCTION kernel.create_workspace(p_id uuid, p_slug text, p_name text, p_creator uuid, p_limit integer)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
BEGIN
    IF p_id IS NULL OR p_creator IS NULL OR p_limit IS NULL OR p_limit < 1 THEN
        RAISE EXCEPTION 'create_workspace: missing argument' USING ERRCODE = '22004';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM kernel.principals WHERE id = p_creator AND kind = 'user' AND NOT disabled) THEN
        RAISE EXCEPTION 'create_workspace: creator is not an enabled person' USING ERRCODE = '42501';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('kernel.create_workspace:' || p_creator::text, 0));
    IF (SELECT count(*) FROM kernel.tenants
        WHERE created_by = p_creator
          AND (status = 'active' OR (status = 'provisioning' AND created_at > now() - interval '15 minutes'))) >= p_limit THEN
        RETURN false;
    END IF;
    INSERT INTO kernel.tenants (id, slug, name, created_by, status) VALUES (p_id, p_slug, p_name, p_creator, 'provisioning');
    RETURN true;
END
$$;
RESET ROLE;

-- Marks a self-service workspace active once its creator's provisioning run finished.
CREATE FUNCTION kernel.activate_workspace(p_id uuid, p_creator uuid)
RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
    WITH updated AS (
        UPDATE kernel.tenants SET status = 'active'
        WHERE id = p_id AND created_by = p_creator AND status = 'provisioning'
        RETURNING 1)
    SELECT EXISTS (SELECT 1 FROM updated)
$$;
REVOKE ALL ON FUNCTION kernel.activate_workspace(uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.activate_workspace(uuid, uuid) TO esmbler_app;
ALTER FUNCTION kernel.activate_workspace(uuid, uuid) OWNER TO esmbler_definer;
