-- Separation of duties snapshot (G2-M3 review, ADR 0016, REQ-AUTHZ-011): everyone the request was by or for when its
-- approval started (creator and the binding's requester fields). They never decide, even if the fields change later.
ALTER TABLE kernel.approvals ADD COLUMN requesters jsonb NOT NULL DEFAULT '[]'::jsonb;
