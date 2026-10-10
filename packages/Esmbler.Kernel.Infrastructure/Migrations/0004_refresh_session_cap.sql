-- Refresh-token families carry the time their session started, so rotation can enforce an absolute session
-- lifetime (security review M5). Existing tokens count from their own creation.
ALTER TABLE kernel.refresh_tokens ADD COLUMN session_started_at timestamptz;
UPDATE kernel.refresh_tokens SET session_started_at = created_at WHERE session_started_at IS NULL;
ALTER TABLE kernel.refresh_tokens ALTER COLUMN session_started_at SET NOT NULL;
ALTER TABLE kernel.refresh_tokens ALTER COLUMN session_started_at SET DEFAULT now();
