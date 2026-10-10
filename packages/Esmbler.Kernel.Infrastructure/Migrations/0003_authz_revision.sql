-- Revocation (REQ-AUTHZ-006, G1-M17): every change to memberships, group membership or role assignments bumps the
-- tenant's authz_revision in the same transaction. Permission caches key on it, so the next request after a
-- revocation commits never reuses facts computed before it.
ALTER TABLE kernel.tenants ADD COLUMN authz_revision bigint NOT NULL DEFAULT 0;

CREATE FUNCTION kernel.bump_authz_revision()
RETURNS trigger LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = kernel, pg_temp
AS $$
BEGIN
    UPDATE kernel.tenants SET authz_revision = authz_revision + 1
    WHERE id = CASE WHEN TG_OP = 'DELETE' THEN OLD.tenant_id ELSE NEW.tenant_id END;
    RETURN NULL;
END $$;
-- EXECUTE stays with PUBLIC: the trigger runs as whichever role performs the DML, and PostgreSQL refuses to call
-- a trigger function outside a trigger.
GRANT UPDATE (authz_revision) ON kernel.tenants TO esmbler_definer;
ALTER FUNCTION kernel.bump_authz_revision() OWNER TO esmbler_definer;

CREATE TRIGGER memberships_authz AFTER INSERT OR UPDATE OR DELETE ON kernel.memberships
    FOR EACH ROW EXECUTE FUNCTION kernel.bump_authz_revision();
CREATE TRIGGER group_members_authz AFTER INSERT OR UPDATE OR DELETE ON kernel.group_members
    FOR EACH ROW EXECUTE FUNCTION kernel.bump_authz_revision();
CREATE TRIGGER role_assignments_authz AFTER INSERT OR UPDATE OR DELETE ON kernel.role_assignments
    FOR EACH ROW EXECUTE FUNCTION kernel.bump_authz_revision();
