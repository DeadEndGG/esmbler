-- Draft change sets (G2-M9, REQ-WS-005, ADR 0019). Steps applied together by one request (for example an AI tool call
-- that adds a field and wires it into a rule, the explorer and its filters) share a batch: they are validated and
-- applied atomically, count as one draft version, and are undone and redone together.
ALTER TABLE kernel.draft_commands ADD COLUMN batch_id uuid;
