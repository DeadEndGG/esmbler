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
| Provide an OpenAI API key (`OPENAI_API_KEY`), optionally Azure OpenAI endpoint and key | Run the AI builder on OpenAI models and score them on the evaluation suite (G2-M20) | G2-M20 | Anthropic and the scripted planner only; OpenAI client tested against a fake ([AI providers and subscriptions](research/ai-providers-and-subscriptions.md)) | not needed yet |
| Decide whether to apply for OpenAI's "Sign in with ChatGPT" partner program | Using a person's ChatGPT plan inside a hosted Esmbler needs OpenAI's acceptance; billing a Claude subscription from another product is not allowed | G2-M20 (decision), Gate 3 (integration) | API keys per tenant; ChatGPT and Claude can still drive Esmbler as connectors once G2-M20 ships | open |
| Provide a public HTTPS host and test accounts (ChatGPT Business, Claude Pro or Team) for connector testing | ChatGPT and Claude only connect to public MCP servers with OAuth | G2-M20 (phase f) | MCP server tested locally with the agent-token flow and an OAuth conformance test client | not needed yet |
| Confirm product targets and pilot assumptions | Experience targets and pilot scale are proposed, not approved | Before Gate 2 pilot | Targets measured and reported as "proposed" ([assumptions](decisions/assumptions.md) ASM-001…ASM-022) | open |
| Provide production JWT signing key / KMS | Tokens must not be signed with the development key | Before any non-local deployment | Development-only default key from `deploy/docker-compose.yml` (`ESMBLER_SIGNING_KEY`); production startup refuses the dev key ([ADR 0011](adr/0011-identity-and-development-authentication.md)) | open |
| Choose cloud account and region for hosting | Pooled hosting in one provider and region | Before Gate 2 pilot | Docker Compose locally; Kubernetes manifests provided but not deployed | open |
| Provide DNS names and TLS certificates | Public HTTPS for web, API, MCP | Before Gate 2 pilot | Local HTTP/self-signed certificates in Compose | open |
| Provide email domain and inbound/outbound provider | Email intake and notifications | Gate 2 | In-app fake notification outbox (Gate 1); local SMTP fake and inbound fixture files (Gate 2) | not needed yet |
| Provide object storage bucket | Attachments and exports | Gate 2 | Local filesystem / MinIO-compatible fake behind the storage adapter | not needed yet |
| Register OIDC/SAML identity provider(s) | Per-tenant SSO, SCIM, MFA | Gate 2 | Local password login ([ADR 0011](adr/0011-identity-and-development-authentication.md)) | not needed yet |
| Provide payment provider sandbox credentials | Marketplace billing and payouts against a sandbox | Gate 5 | Fake payment adapter | not needed yet |
| Provide payment provider live credentials | Real billing | After Gate 5, before launch | Sandbox only | not needed yet |

## Session-environment note (2026-10-10)

| Action | Why | When needed | Meanwhile | Status |
| --- | --- | --- | --- | --- |
| Push the gate tags (`git push origin --tags`) from a normal clone | The build session's git proxy accepts branch pushes but drops tag pushes, so `gate-N-complete` tags exist only in the build container's local repository | Before testing gate by gate from tags | Each gate's commit is named in `docs/progress.md` and its test guide; create the tag on that commit (`git tag gate-0-complete <sha>`) | Open |
