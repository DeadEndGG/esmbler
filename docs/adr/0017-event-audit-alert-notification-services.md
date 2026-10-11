---
title: "ADR 0017: Event, audit, alert and notification services"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-11
requirements: [REQ-EVT-008, REQ-EVT-009, REQ-AUD-001, REQ-ALR-001, REQ-NOT-001, REQ-NOT-002, REQ-NOT-003, REQ-NOT-004, REQ-NOT-005, REQ-NOT-006, REQ-NOT-007]
---

# ADR 0017: Event, audit, alert and notification services

## Context and problem statement

The product owner asked (2026-10-10) for events, audit, alerting and notifications to become one configurable shared service: modules publish standardized events without knowing their consumers; audit, alerts and notifications stay separate with their own storage, lifecycle, retention and permissions; channels are pluggable with per-tenant channel instances; tenants build reusable notification profiles and routing rules with explicit inheritance; delivery is reliable and tenant-isolated; everything is managed through the Admin Center and MCP with the versioned configuration and publish process, and can be previewed without sending.

Today the platform has a transactional outbox with an at-least-once dispatcher and per-consumer inbox, durable jobs and timers, append-only audit written in the business transaction, and a minimal notification consumer that stores in-app messages nobody can read yet. The detailed inventory, schemas, interfaces, end-to-end example and phases are in [Events, audit, alerts and notifications](../architecture/events-audit-alerts-notifications.md).

## Decision drivers

- Publishers must not know subscribers; adding or removing a subscriber is configuration.
- Required audit never depends on subscribers or delivery.
- A failing channel never blocks another channel, audit, or the business transaction.
- Tenant/environment isolation, permission-aware content and environment safety (no live sends from sandboxes or previews).
- Reuse what works: outbox, inbox deduplication, durable jobs, governors, configuration drafts and publish, MCP.

## Considered options

1. **Extend the existing outbox into an event platform with a registry and configurable subscriptions; audit stays synchronous; alerts and notifications become separate services fed by subscriptions; deliveries run as durable jobs.**
2. Adopt an external message broker (Kafka, RabbitMQ) and a third-party notification service.
3. Keep code consumers only and add notification features inside `NotificationConsumer`.

## Decision outcome

**Chosen option 1.**

- **Envelope v2 and event type registry.** Additive envelope fields (source, subject, payload schema reference, trace context, severity, sensitivity); `config.published` becomes a normal envelope. Event types are public spec objects with versioned payload schemas; declarative modules publish domain events through `emits` on transitions and an `emit_event` rule action; platform services publish `platform.*` events.
- **Subscriptions.** A public spec object matches events (type, module, entity, condition) and targets a service: notify, alert, automation, webhook or package handler. One subscription router consumer schedules a durable job per match. Code consumers remain only for platform invariants (projections, fulfillment, approvals).
- **Dispatcher.** Per-consumer delivery state replaces one transaction for all consumers, so a failing consumer retries and dead-letters alone; replay by consumer; outbox retention window.
- **Audit.** Stays synchronous in the acting service's transaction. Adds retention policy with legal hold, export, and a per-tenant hash chain for tamper evidence. Notification delivery history is not audit.
- **Alerts.** A separate service with rules, dedup keys, acknowledgment, escalation timers, auto-resolve and suppression. Alerts emit `platform.alert.*` events; they never send messages themselves.
- **Notifications.**
  - Channel types are code adapters behind a public `INotificationChannel` contract. Channel instances are per-environment settings with vault-referenced credentials, rate limits and a capture or test mode, never promoted between environments.
  - Templates have per-channel variants and are rendered per recipient under their permissions, with an explicit allowlist for external recipients.
  - Profiles hold multiple channels and recipients, filters, schedules, quiet hours, and all-or-fallback delivery.
  - Routes select one or more profiles and record recipients. They come in module, tenant and team layers, with user preferences on top: mandatory routes cannot be muted, and deduplication is on person or address plus channel.
  - Deliveries are one durable job per recipient and channel, with states, retries, rate limits, provider idempotency, ambiguous outcomes, inspection and audited replay.
- **Administration.** Profiles, routes, templates, subscriptions and alert rules are spec objects with overlay ops, drafts, publish and MCP tools. Permission-relevant changes (external recipients, mandatory flags, required routes) need permission administration. Preview resolves everything without sending.

### Consequences

- Good: publishers stay simple; tenants and the AI builder configure who hears about what; every surface is reviewable and reversible.
- Good: failure isolation and retries come from the job runner already in production use.
- Good: audit remains complete even when notifications are misconfigured or providers are down.
- Bad: more rows (one job per match and per delivery) and more moving parts; governors and retention bound the cost.
- Bad: providers without idempotency keys can deliver twice after an ambiguous failure; the platform surfaces this instead of claiming exactly-once.
- Neutral: an external broker can replace the outbox transport later without changing the envelope, registry or subscriptions.

## Pros and cons of the options

| Option | Pros | Cons |
| --- | --- | --- |
| 1. Extend the outbox, configurable subscriptions, job-based delivery | Reuses proven transactional guarantees and jobs; configuration-driven; AI-editable; no new infrastructure | More tables and jobs; the platform owns provider adapters |
| 2. External broker and notification SaaS | Scale and features off the shelf | Breaks the single-transaction guarantee without an outbox anyway; tenant isolation, permissions and environment safety become integration problems; data leaves the platform |
| 3. Grow `NotificationConsumer` | Smallest change | Couples publishers, routing and channels; one failure blocks all; no alerts; nothing configurable or previewable |

## Implementation status

- **G2-M17 (done): event platform v2.** Envelope v2 with source, type version, emitter, severity, sensitivity, trace context and recorded-at, read alongside v1 (ASM-052); the event type registry (`EventTypeSpec`, `event_type.add` and `.remove`, built-in platform types per ASM-051, generated [AsyncAPI reference](../reference/asyncapi.json)); `emits` on transitions and the edge-triggered `emit_event` rule action with type checks and hidden-field protection (ASM-053); approval platform events; `config.published` as an envelope; per-consumer delivery state with independent retries, dead letters and audited replay per consumer; outbox retention. Tickets 1.4.0 publishes `core.tickets.ticket.escalated`, `.priority_raised` and `.resolved`. MCP: `list_event_types`, `add_event_type`, `emit_event` in `add_rule`, `publish_events` in `update_transition`. Verification: `Events.EventPlatformTests`, `Configuration.EventTypeTests`, `Events.EventEnvelopeContractTests`, `Mcp.McpServerTests.Agent_declares_an_event_and_publishes_it_from_a_rule_and_a_transition`.
- **G2-M18 (done): notification service.** Decided in detail and built per [ADR 0021](0021-notification-routing-and-delivery.md): subscriptions with the `notify` target and the subscription router; routes, profiles and per-channel templates as configuration with module and tenant layers, personal mutes and mandatory routes; channel instances as environment settings (live, capture, disabled) with secret references and host allow-lists; rendering once per recipient under their permissions; delivery rows with idempotency keys, states, retries, rate limits, ambiguous outcomes, inspection, forced replay and test sends; previews; the in-app inbox; MCP tools; Tickets 1.7.0. Profiles carry channels and a severity filter, while recipients live on routes. Deferred (see ADR 0021): the team layer, schedules and quiet hours, approvals and legacy templates onto routes, live push, and editing routes and templates in the Admin Center.
- **G2-M19 (done): alerts and audit policy.** Decided in detail and built per [ADR 0022](0022-alerts-and-audit-policy.md). Alerts: alert rules as spec objects raised and resolved through subscriptions with the `alert` target, deduplicated by key on one active alert, with acknowledgment, durable escalation timers, event-driven auto-resolve, a reopen cool-down and maintenance windows. Alerts publish `platform.alert.*` events, and routes reach owners through the `alert_owner` recipient, which replaces the `escalation[].notify` lists of the design sketch. Other parts: the `alerts.respond` and `alerts.manage` actions, the Alerts page and badge, MCP tools, and Tickets 1.8.0 SLA alerts. Audit: a per-workspace hash chain sealed asynchronously by the worker (chain order is seal order) with a guard trigger and column-level grants; daily and on-demand verification; retention by policy with workspace-wide legal holds and a moving anchor; an audited, rate-limited export in CSV or JSON Lines; and retention for deliveries, the inbox and resolved alerts. Deferred (see ADR 0022): holds per record, external anchoring of chain heads, a chain-broken event, recurring windows and on-call schedules, and Admin Center editing of alert rules.
- **Moved:** the subscriptions spec object and router to G2-M18 (their first target is notifications); SLA platform events to G2-M4; replay of one consumer from a point in time is not built.

## More information

- Design, schemas, example and phases: [Events, audit, alerts and notifications](../architecture/events-audit-alerts-notifications.md).
- Notification routing and delivery in detail: [ADR 0021](0021-notification-routing-and-delivery.md); alerts and audit policy in detail: [ADR 0022](0022-alerts-and-audit-policy.md).
- Related: [ADR 0005](0005-job-and-workflow-execution.md) (outbox and jobs), [ADR 0003](0003-configuration-artifact-and-overlay-format.md) (overlay ops), [ADR 0016](0016-approvals.md) (permission-change rules).
- Plan: milestones G2-M17 (event platform v2), G2-M18 (notification service), G2-M19 (alerts and audit policy) in [progress](../progress.md).
