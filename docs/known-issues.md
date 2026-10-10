---
title: Known issues
status: living
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: []
---

# Known issues

Failures that could not be fixed after a reasonable effort (execution brief, section 1). Each is isolated, has reproduction steps, and is revisited before its gate closes. Status values: `open`, `workaround`, `fixed`.

| ID | Issue | Repro | Impact | Workaround | Status |
|---|---|---|---|---|---|
| KI-001 | A builder whose rights are revoked keeps receiving draft-change notices (IDs and versions only) until the live connection reconnects | Remove a builder's role while their browser watches a draft | Low: the notices carry no data, and reloading the draft is refused under the new rights | Reconnect evicts the subscription; re-check on each notice planned for Gate 2 | open |
| KI-002 | The per-tenant daily AI spend budget from ADR 0013 is not enforced; only per-turn bounds apply | Send many assistant messages | Cost exposure when a model key is configured | Per-person message rate limit (30/min) and per-turn tool, request and time limits | open (Gate 2) |
| KI-003 | Schema-change classification (REQ-MIG-001) is partial: upgrade reports list base changes, overlay outcomes and declarative migrations, but changes are not labeled expand/backfill/contract | Preview the 1.1.0 upgrade | Builders see what changes, not its migration phase | The report's migrations section shows backfills and conversions | open (Gate 2) |
| KI-004 | The live page highlights elements from chat steps, but selecting an element does not highlight its step in the chat, and removed elements disappear without a fade | Open the builder, hover a step, then click an element | Minor: the chat-to-page link works one way | Hover steps to see their elements | open (Gate 2) |
| KI-005 | Design lint (stylelint rules for tokens) is not in CI; accessibility scans (axe) are | — | Raw values could slip into component styles | Review rule in `.claude/rules/renderer-ui.md` | open (Gate 2) |
| KI-006 | In Docker Compose the API port (5080) is published for local tools while the API trusts `X-Forwarded-For` from the web proxy, so a local caller could spoof its address for rate limiting | Call `http://localhost:5080/api/auth/login` with a forged header | Local development only | Do not publish the API port, or set `Esmbler__Auth__TrustForwardedHeaders=false`, in shared environments | open |
| KI-007 | The Claude planner has not been scored on the evaluation suite | No API key in the build environment | Real-model completion rate unknown; the built-in planner passes 43 of 43 | Provide `ANTHROPIC_API_KEY` ([human action](human-actions.md)) and run `dotnet test tests/Esmbler.IntegrationTests --filter Claude_planner` | open |
