-- Guided workspace creation (G2-M9b, REQ-WS-001). People who are signed in may create workspaces from a starter, up to
-- a per-person limit. The limit is checked and the tenant row inserted in one privileged function, under a per-person
-- advisory lock, so parallel requests cannot exceed it. created_by records who created a workspace (null for
-- workspaces provisioned by operators and demo seeding).
ALTER TABLE kernel.tenants ADD COLUMN created_by uuid;

CREATE FUNCTION kernel.create_workspace(p_id uuid, p_slug text, p_name text, p_creator uuid, p_limit integer)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended('kernel.create_workspace:' || p_creator::text, 0));
    IF (SELECT count(*) FROM kernel.tenants WHERE created_by = p_creator) >= p_limit THEN
        RETURN false;
    END IF;
    INSERT INTO kernel.tenants (id, slug, name, created_by) VALUES (p_id, p_slug, p_name, p_creator);
    RETURN true;
END
$$;
REVOKE ALL ON FUNCTION kernel.create_workspace(uuid, text, text, uuid, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION kernel.create_workspace(uuid, text, text, uuid, integer) TO esmbler_app;
ALTER FUNCTION kernel.create_workspace(uuid, text, text, uuid, integer) OWNER TO esmbler_definer;
