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
| KI-005 | Design lint (stylelint rules for tokens) is not in CI; accessibility scans (axe, light and dark) are. Tailwind utilities expose only token names (ADR 0014), but component style blocks and arbitrary utility values are unchecked | — | Raw values could slip into component styles | Review rule in `.claude/rules/renderer-ui.md` | open (Gate 2) |
| KI-006 | In Docker Compose the API port (5080) is published for local tools while the API trusts `X-Forwarded-For` from the web proxy, so a local caller could spoof its address for rate limiting | Call `http://localhost:5080/api/auth/login` with a forged header | Local development only | Do not publish the API port, or set `Esmbler__Auth__TrustForwardedHeaders=false`, in shared environments | open |
| KI-007 | The Claude planner has not been scored on the evaluation suite | No API key in the build environment | Real-model completion rate unknown; the built-in planner passes 43 of 43 | Provide `ANTHROPIC_API_KEY` ([human action](human-actions.md)) and run `dotnet test tests/Esmbler.IntegrationTests --filter Claude_planner` | open |
| KI-008 | Publish requests do not expire after 24 hours (ASM-027), and idempotency keys are not purged | Leave a publish request pending for a day | Low: approval is still bound to the artifact hash and expected revision, so a stale request cannot publish something else | Publish or discard the draft; keys are small | open (Gate 2, durable jobs) |
| KI-009 | Graph consistency tooling is incomplete: failed grant writes are not retried in the background, nothing schedules `RebuildGraphAsync`, the SQL permission predicate has no conformance test against `CanRead`, and integration tests use the in-memory graph rather than a real OpenFGA store | Fail an OpenFGA write after a group change | A grant can be missing until repaired (fail closed: access is denied, never widened) | Re-run the change, or call `RebuildGraphAsync` from an operator script | open (Gate 2) |
| KI-010 | No per-tenant API rate limit; only sign-in, assistant and MCP limits exist | Script many API calls from one tenant | A noisy tenant can consume shared API capacity | Per-user assistant and MCP limits; reverse-proxy limits in deployment | open (Gate 2, G2-M13) |
| KI-011 | The sign-in page always shows the demo account hint (`alex@acme.example` / seed password), even when demo data is not seeded | Open `/login` on any deployment | Low: the password belongs to seed data only, but production should not advertise it | Remove the hint in non-demo builds; show it only when the server reports demo seeding | open (Gate 2) |
