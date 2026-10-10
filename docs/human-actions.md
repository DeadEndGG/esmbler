---
title: Human actions
status: living
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-AISAFE-005, REQ-IAM-003, REQ-BIZ-003, REQ-BIZ-006, REQ-MKT-002, REQ-OPS-006, REQ-ADM-002]
---

# Human actions

Things only a human can provide (execution brief, section 1). The platform keeps working against a local adapter or fake until each is done. Status values: `open`, `done`, `not needed yet`.

| Action | Why | When needed (gate) | What the platform does meanwhile | Status |
|---|---|---|---|---|
| Provide an Anthropic API key (`ANTHROPIC_API_KEY`) | Real-model builder runs and the AI evaluation suite with Claude | Gate 1 (evaluation evidence) | Scripted planner drives the same MCP tools for local dev and Playwright ([ADR 0013](adr/0013-ai-assistant-model-adapter.md)); evaluation results marked "scripted only" | open |
| Confirm product targets and pilot assumptions | Experience targets and pilot scale are proposed, not approved | Before Gate 2 pilot | Targets measured and reported as "proposed" ([assumptions](decisions/assumptions.md) ASM-001…ASM-022) | open |
| Provide production JWT signing key / KMS | Tokens must not be signed with the development key | Before any non-local deployment | Development key from `appsettings.Development.json`; production startup refuses the dev key ([ADR 0011](adr/0011-identity-and-development-authentication.md)) | open |
| Choose cloud account and region for hosting | Pooled hosting in one provider and region | Before Gate 2 pilot | Docker Compose locally; Kubernetes manifests provided but not deployed | open |
| Provide DNS names and TLS certificates | Public HTTPS for web, API, MCP | Before Gate 2 pilot | Local HTTP/self-signed certificates in Compose | open |
| Provide email domain and inbound/outbound provider | Email intake and notifications | Gate 2 | In-app fake notification outbox (Gate 1); local SMTP fake and inbound fixture files (Gate 2) | not needed yet |
| Provide object storage bucket | Attachments and exports | Gate 2 | Local filesystem / MinIO-compatible fake behind the storage adapter | not needed yet |
| Register OIDC/SAML identity provider(s) | Per-tenant SSO, SCIM, MFA | Gate 2 | Local password login ([ADR 0011](adr/0011-identity-and-development-authentication.md)) | not needed yet |
| Provide payment provider sandbox credentials | Marketplace billing and payouts against a sandbox | Gate 5 | Fake payment adapter | not needed yet |
| Provide payment provider live credentials | Real billing | After Gate 5, before launch | Sandbox only | not needed yet |
