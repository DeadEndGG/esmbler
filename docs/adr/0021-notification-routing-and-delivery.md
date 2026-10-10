---
title: "ADR 0021: Notification routing and delivery"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-NOT-001, REQ-NOT-002, REQ-NOT-003, REQ-NOT-004, REQ-NOT-005, REQ-NOT-006, REQ-NOT-007, REQ-EVT-009, REQ-AUTHZ-008, REQ-AISAFE-001, REQ-TEN-001]
---

# ADR 0021: Notification routing and delivery

## Context and problem statement

[ADR 0017](0017-event-audit-alert-notification-services.md) chose the shape of the notification service: subscriptions fed by the event platform, pluggable channel types with per-environment instances, templates rendered per recipient, profiles, layered routes with mandatory routes, and durable per-recipient delivery with inspection, replay and preview. G2-M17 built the event platform (envelope v2, the event type registry, per-consumer delivery). G2-M18 builds the notification service on top of it.

Before G2-M18, `NotificationConsumer` turned two kinds of record events (`record.transitioned:<state>`, `record.updated:<field>`) into in-app rows that nobody could read, and `ApprovalService` wrote approval rows directly. Nothing was configurable beyond the template text, nothing left the platform, and no one could see whether a message arrived.

This ADR settles the details ADR 0017 left open:

- How do subscriptions, routes, profiles and templates fit together as configuration?
- Which layers exist, and what does "mandatory" protect?
- Where do channel settings and credentials live?
- When is a message rendered, and what may each recipient see?
- What are the delivery states, and how are duplicates and unknown outcomes handled?
- Who may do what, including AI agents?

## Decision drivers

- Configuration stays validated specs with typed overlay ops, drafts, publish and MCP tools (REQ-NOT-007); upgrades keep tenant changes.
- A recipient never reads a field they could not read in the app; people outside the workspace see only what an administrator allowed (REQ-NOT-002).
- Sandboxes and previews never send by accident (REQ-NOT-006).
- One failing provider never blocks another channel, the event pipeline or the business transaction (REQ-NOT-005).
- Duplicates are prevented where possible and admitted where not: a send with an unknown outcome is reported, not repeated silently.
- Access decisions (who must be told, what leaves the workspace) are permission changes that AI agents cannot make (REQ-AUTHZ-008, REQ-AISAFE-001).
- Credentials never appear in configuration, audit, logs or MCP output.

## Considered options

1. **Four spec objects (subscription, route, profile, template) composed in layers; channel instances as environment settings with secret references; render once per recipient when the event is resolved; one delivery row and job per recipient and channel with an idempotency key.**
2. Routes that embed their channels and text (one object per notification).
3. Render at send time, in the delivery job.
4. Channel instances as configuration, promoted with the rest of the artifact.

## Decision outcome

**Chosen option 1.**

### Configuration model

All four objects are public SDK specs (`packages/Esmbler.Sdk/Specs/Notifications.cs`, `Specs/Logic.cs`), carried in module releases and the tenant overlay, validated by `ConfigValidator.ValidateNotifications` and changed through overlay ops.

| Object | Says | Key properties |
| --- | --- | --- |
| `SubscriptionSpec` | Which events matter | `Event` (an event type ID), optional `Entity`, optional `When` condition over `event` (payload) and `record`, `Target` (`notify` only for now), `Enabled` |
| `NotificationRouteSpec` | Who hears about a subscription's events, how and with which text | `Subscription`, `Profile`, `Template`, `Recipients` (1 to 50 rules), `Mandatory`, `Enabled` |
| `NotificationProfileSpec` | How a route delivers | `Channels` in order (`in_app`, `email`, `sms`), `Delivery` (`all`, or `fallback`: the first channel the recipient can be reached on), `MinSeverity`, `Enabled` |
| `NotificationTemplateSpec` | The text | `Subject`, `Body`, per-channel `Variants`, `Entity` whose fields placeholders read, `ExternalFields`, `Enabled` |

Recipient rules (`RecipientSpec`) are `field` (a user, group, principals or email field of the record), `role`, `group` (key), `user` (principal ID), `approvers` (the pending approvers of an approval event) and `address` (an external email address or `+` phone number). People must be enabled members of the workspace and of kind `user`; an address that belongs to a member resolves to that person.

Templates are plain text with the placeholders `{{record.<field>}}`, `{{record.number}}`, `{{event.<field>}}`, `{{recipient.name}}` and `{{link}}`. The validator refuses HTML, unknown placeholders and text over 4,000 characters; rendering caps subjects at 200 characters and SMS bodies at 480.

The platform module adds three built-in profiles to every configuration (`PlatformModule.NotificationProfiles`): `core.platform.profile.in_app`, `core.platform.profile.in_app_email` and `core.platform.profile.urgent` (in-app, email and SMS for events of severity `high` or above). Tenants add their own; built-ins can be disabled, not removed.

Overlay ops (`OverlayOps.cs`): `subscription.add|update|remove`, `notification_template.add|update|remove`, `notification_profile.add|update|remove` and `notification_route.add|update|remove`. Tenants add objects with `tenant__` IDs and may update module objects within limits: a module subscription's condition, a module route's recipients and its mandatory flag cannot change, and a mandatory module route cannot be turned off (`overlay.base_immutable`, `overlay.mandatory`). Only tenant objects that nothing uses can be removed (`overlay.in_use`); module objects are disabled instead. Module-layer objects use IDs in their module's namespace, and a module's routes may use only the module's own subscriptions, templates and profiles or the platform's (`config.foreign_notification_element`, the data boundary of [ADR 0012](0012-repository-layout-and-module-boundaries.md)).

### Layers and mandatory routes

The layers are module routes, tenant routes and people's preferences on top. Preferences (`kernel.notification_preferences`) mute a route, or one channel of a route, for one person.

Resolution processes the routes of a subscription with mandatory routes first, then tenant routes before module routes. The first route to claim a recipient and channel for an event wins that delivery, so a person reached by a mandatory route always gets it, and a tenant route overrides a module route for the same person and channel.

A mandatory route cannot be muted (the preference API returns `409 mandatory_route` and resolution ignores mutes). Nothing it relies on can be switched off. The validator raises `config.mandatory_unreachable` when a mandatory, enabled route's subscription, template or profile is disabled, or when its profile does not include `in_app`; the bell is always one of a mandatory route's channels.

### Channel types and channel instances

A channel type is a code adapter behind the public SDK contract `INotificationChannel` (`packages/Esmbler.Sdk/Notifications/INotificationChannel.cs`). It sends one rendered message and reports `sent`, `transient`, `permanent` or `unknown`, and says whether the provider deduplicates on the idempotency key (`SupportsIdempotency`). The contract is public so that the shape is stable, but adapters are platform-only for now: modules cannot contribute channels through `IModuleDefinition`, and `Esmbler.Hosting` registers them. The first adapters are `SmtpEmailChannel` (MailKit; no provider idempotency) and `HttpSmsChannel` (a Twilio-style HTTPS API with an idempotency header). `in_app` is built in: it writes the person's inbox row in the database. MailKit and MimeKit are confined to `Esmbler.Kernel.Infrastructure` by an architecture test.

A channel instance is an environment setting, not configuration (`kernel.notification_channels`, migration 0014). It is never part of an artifact and never promoted between environments. It has a key, a type (`email` or `sms`), a mode, settings, secret references and a rate per minute (default 60):

- **Modes.** `live` sends; `capture` keeps the rendered message as a `captured` delivery and sends nothing; `disabled` suppresses. The admin form proposes `capture`. Going `live` needs a host on the operator's allow-list (`host_not_allowed` otherwise), and in a non-production environment it also needs `permissions.admin`. An email or SMS delivery with no instance of its type is `suppressed` with `channel_not_configured`; `in_app` always works.
- **Secret references.** Settings hold no secrets: keys that look like secrets (`password`, `secret`, `token`, `apikey`, `credential`) are refused unless they end in `ref`. Credentials are named in `secretRefs` and resolved at send time by `ISecretStore`. The development and single-host adapter `EnvironmentSecretStore` reads `ESMBLER_SECRET__<WORKSPACE_SLUG>__<NAME>` (the slug upper-cased, hyphens as underscores; names match `^[A-Z][A-Z0-9_]{1,40}$`), so a workspace can only ever read its own secrets and never other process variables. The channel views return each secret name with whether it is configured, never its value. A vault adapter replaces it in production ([human actions](../human-actions.md), G2-M12).
- **Host allow-lists.** `Esmbler:Notifications:AllowedEmailHosts` (empty by default: email can only be captured) and `AllowedSmsHosts` (default `api.twilio.com`) are operator settings. The adapters check the host again on every send.

### Rendering and what recipients see

A message is rendered once per recipient and channel when the event is resolved, and the delivery row stores the subject and body. Retries and replays send exactly what was decided, even if the template, the record or the recipient's access changes later.

`NotificationRenderer` renders under the recipient's own permissions. A person is skipped when they cannot read the record; each `{{record.<field>}}` renders empty when the field is hidden from them. External recipients (`address` rules that are not members) see only the template's `ExternalFields`, and `{{event.<field>}}` values only for events whose sensitivity is `public`. The validator refuses routes that send non-public events to external addresses (`config.external_redaction`).

### Resolution and delivery

```mermaid
sequenceDiagram
    participant W as Record write or platform service
    participant O as Outbox (kernel.outbox)
    participant R as SubscriptionRouter (consumer)
    participant J as notify.resolve job
    participant D as notify.deliver job
    participant C as Channel adapter (SMTP, SMS)
    participant DB as kernel.notification_deliveries
    W->>O: event (same transaction as the change)
    O->>R: dispatch (own transaction, inbox dedup)
    R->>R: enabled subscriptions for the type, entity, When
    R->>J: schedule, dedup notify:{event}:{subscription}
    J->>J: routes (mandatory, tenant, module), profile, severity, recipients
    J->>J: per recipient: may read the record? mutes? reachable channels
    J->>DB: insert delivery (idempotency key event:who:channel, rendered text)
    J->>D: schedule notify.deliver per queued delivery
    D->>DB: lock row, check status and instance mode
    alt in_app
        D->>DB: inbox row, status sent
    else capture or disabled
        D->>DB: status captured or suppressed
    else live
        D->>DB: status sending, attempts + 1
        D->>C: SendAsync(message, settings, secrets, idempotency key)
        C-->>D: sent, transient, permanent or unknown
        D->>DB: sent, queued (retry), failed or ambiguous; attempt row
    end
```

- **Router.** `SubscriptionRouter` is one event consumer (ADR 0017). It never sends; per matching subscription it schedules one durable `notify.resolve` job. A condition that fails at run time never notifies.
- **Resolve.** `NotifyResolveJobHandler` builds the deliveries. The idempotency key is `{event}:{principal or lower-cased address}:{channel}` and is unique per environment, so two routes, layers or retried jobs cannot deliver the same event to the same person on the same channel twice. Muted recipients get a `suppressed` row (reason `muted`), so the history shows why nothing went out. At most `MaxRecipientsPerEvent` (default 200) deliveries are created per event; beyond that resolution stops and writes the audit entry `notification.recipients_capped`.
- **Deliver.** `NotifyDeliverJobHandler` handles one delivery. The statuses are `queued`, `sending`, `sent`, `failed`, `ambiguous`, `captured` and `suppressed`; every attempt is appended to `kernel.notification_delivery_attempts`.
  - A transient result is retried with the job runner's backoff. When retries run out, the delivery becomes `failed` (`retries_exhausted`).
  - An unknown result is retried only when the adapter supports provider idempotency. Otherwise the delivery is `ambiguous`, and so is a delivery found in `sending` after a crash. An ambiguous outcome is never retried blindly.
  - Over an instance's rate per minute, the job is rescheduled 20 seconds later without spending an attempt.
  - Adapter exceptions become `unknown` (`adapter_error`) and are never logged, because they can carry provider responses.
- **Replay** (audited `notification.replay`). Failed and ambiguous deliveries are requeued with their own idempotency key. A sent, captured or suppressed delivery is copied to a new delivery (`replay_of`, key `{key}:replay:{n}`) only with `force`, so nobody is told twice by accident.
- **Test send** (audited `notification.test_send`). Sends a fixed message to the caller's own email through an instance, following its mode. SMS test sends are refused because people have no phone numbers on file yet.
- **Preview** (`NotificationService.PreviewAsync`, MCP `preview_notification`). Resolves a route of the working configuration (the open draft, if any) for an optional record and renders it, without sending or writing anything. Text shows only fields both the recipient and the previewing person may see, so a preview cannot reveal more than the viewer can read. At most 20 recipients are shown.

### Inbox, preferences and administration

- `kernel.notifications` stays the in-app inbox and gains `read_at`, `delivery_id`, `severity`, `entity_id` and `record_id` (migration 0014). REST `/api/me/notifications` (list, `unread-count`, `{id}/read`, `read-all`) and `/api/me/notification-preferences` (list and set mutes) act only on the signed-in person; AI agents have no inbox and are refused.
- REST `/api/admin/notifications` (`channels`, `channels/{key}`, `channels/{key}/test`, `deliveries`, `deliveries/{id}/replay`, `preview`).
- Web: `/notifications` (inbox and "What I receive") and `/admin/notifications` (channels, deliveries with replay, route preview). The navigation shows an unread badge, refreshed every 60 seconds while the page is visible.
- Routes, templates, profiles and subscriptions are edited in drafts: through the AI builder and MCP tools, and published through the normal review.

### Authorization

- The new platform action `notifications.manage` covers channel instances, delivery inspection with message text, replay and test sends. `permissions.admin` implies it, and `EffectivePermissions` strips it from AI agents.
- Delivery history without text (status, reason, attempts, masked recipients) is also readable with `audit.read`. Message text is visible only to people who manage notifications and, in their inbox, to the recipient.
- Configuration rights (`config.edit`) allow previews and the channel summary (type, mode and readiness only, no settings, hosts or secret names).
- Agents edit notification configuration in drafts through MCP (`notify_when`, `add_subscription`, `add_notification_template`, `update_notification_template`, `add_notification_profile`, `add_notification_route`, `update_notification_route`, and the read-only `list_notification_setup`, `preview_notification`, `list_notification_deliveries`).
- Making a route mandatory or optional, adding external recipients to a route and changing a template's `ExternalFields` are permission changes (`AffectsPermissions`, listed by `ConfigCompiler.PermissionDiff`). They need `permissions.admin` to apply, undo and publish, and agents are refused.
- `list_notification_deliveries` never returns message text or full addresses.

### Legacy templates and approvals

Templates that carry their own `Event` trigger (Gate 1 releases and Tickets' resolved, assigned, fulfilled, task-assigned and declined notifications) keep going through `NotificationConsumer` and produce the same inbox rows as before. Routes refuse such templates (`config.bad_template`). Approval notifications are still written directly by `ApprovalService` (ASM-049).

To make this possible, `NotificationTemplateSpec.Event` and `Entity` became nullable (`string?`), and the template gained `Label`, `Variants`, `ExternalFields` and `Enabled`. This is a deliberate SDK contract change. It is JSON and binary compatible: existing releases deserialize unchanged, and compiled modules keep working. For C# authors it is source-breaking nullability: code that reads `template.Event` or `template.Entity` as non-null gets a nullable warning and must handle null (route templates have no trigger; templates over event fields only have no entity). Third-party module authors should treat a null `Event` as "used by a route" (ASM-086).

### Tickets 1.7.0

Tickets 1.7.0 declares `core.tickets.sub.escalated` and `core.tickets.sub.p1_raised` with their templates and two routes to the assignee and the assignment group:

- `core.tickets.route.escalated` uses the in-app and email profile.
- `core.tickets.route.p1_raised` is mandatory and uses the urgent profile.

The release is additive with no data migrations; workspace routes are kept on upgrade.

### Consequences

- Good: who hears about what is configuration that the AI builder can change, review and undo, while access decisions stay with people.
- Good: content is decided once, under the recipient's permissions, and every retry or replay sends the same text; the history shows every attempt and why something was not sent.
- Good: sandboxes and previews cannot send by accident, and credentials never enter configuration, audit or MCP output.
- Good: a provider outage only affects its own deliveries; in-app and other channels are unaffected.
- Bad: email over SMTP has no provider idempotency, so a crash mid-send leaves an `ambiguous` delivery that a person must decide to replay.
- Bad: rendering at resolve time means text reflects the record at the event, not at the moment of sending (intended, but it can be minutes old under rate limits).
- Bad: two inbox writers remain until legacy templates and approvals move onto routes.
- Neutral: the bell refreshes every 60 seconds; there is no live push yet.

## Pros and cons of the options

| Option | Pros | Cons |
| --- | --- | --- |
| 1. Subscriptions, routes, profiles, templates; instances as settings; render at resolve | Reusable profiles and templates; clear precedence; dedup across routes; stable content for retries and replays; environments cannot leak credentials | Four objects to understand (MCP `notify_when` creates three of them in one change) |
| 2. One object per notification | Simple for one-off cases | Channels and text duplicated across routes; no shared "urgent" profile; mandatory rules harder to protect |
| 3. Render at send time | Text is as fresh as possible | Retries and replays could send different text or leak fields a recipient lost access to in between; previews would not match |
| 4. Instances as configuration | One place for everything | Production credentials or live mode would be promoted into sandboxes (or the reverse); breaks environment safety (REQ-NOT-006) |

## Deferred

- **Team layer** between tenant and personal preferences (REQ-NOT-004).
- **Quiet hours, schedules, business-calendar delivery windows** and fallback to the next channel after a failure; `fallback` currently means the first channel the person can be reached on (REQ-NOT-003).
- **Approvals onto routes:** approval notifications are still inbox rows written by `ApprovalService`.
- **Migrating legacy templates to routes:** Gate 1 templates with their own trigger still go through `NotificationConsumer`.
- **Live push:** the web app polls the unread count every 60 seconds.
- **Admin Center editing of routes, templates, profiles and subscriptions** (REQ-NOT-007): done in drafts through the AI builder and MCP; the Admin Center manages channel instances, deliveries and previews only.
- Phone numbers on people (SMS reaches only `address` recipients today), a production vault adapter (G2-M12) and other subscription targets (alerts in G2-M19, automation, webhooks).

## Security notes

- **Exfiltration and SSRF.** Only allow-listed provider hosts are used live, and adapters re-check them on every send. External recipients get public events and allow-listed fields only. Adding external recipients or external fields is a permission change agents cannot make. Live mode in a sandbox needs `permissions.admin`. Threat model [T-73](../security/threat-model.md#authentication-web-and-availability).
- **Secrets.** Only secret names are stored, values come from the secret store at send time, secret-like settings are refused, and audit records secret names only. Adapter exceptions are not logged. Threat model [T-74](../security/threat-model.md#secrets).
- **Flooding.** At most 200 deliveries per event (audited when capped), 50 recipient rules per route, a rate per minute per instance, deduplication by key, and test sends under the per-person `workspaces` rate limit. Mandatory routes are a permission change. Threat model [T-75](../security/threat-model.md#authentication-web-and-availability).
- **Isolation.** All new tables have FORCE RLS on tenant and environment (the isolation test checks every `kernel` table); jobs run in the event's tenant and environment.

## More information

- Related: [ADR 0017](0017-event-audit-alert-notification-services.md) (the service design), [ADR 0003](0003-configuration-artifact-and-overlay-format.md) (overlay ops), [ADR 0005](0005-job-and-workflow-execution.md) (jobs), [ADR 0010](0010-database-migrations-and-roles.md) (migration 0014), [ADR 0016](0016-approvals.md) (approvers), [design](../architecture/events-audit-alerts-notifications.md).
- Assumptions: ASM-076 to ASM-088 in [assumptions](../decisions/assumptions.md).
- Verification:
  - Unit: `Configuration.NotificationSpecTests` (`A_subscription_route_and_template_compile_with_the_built_in_profiles`, `Subscriptions_need_a_known_event_and_a_valid_condition`, `Templates_are_plain_text_with_known_placeholders`, `Routes_check_their_recipients_and_never_send_internal_events_outside`, `Mandatory_routes_and_external_recipients_are_permission_changes`, `Mandatory_routes_cannot_be_turned_off_or_removed_and_used_objects_stay`, `Modules_keep_notification_elements_in_their_namespace_and_route_only_through_their_own`, `Tickets_routes_escalations_and_makes_the_p1_alert_mandatory`); architecture `Only_infrastructure_talks_to_mail_servers`.
  - Integration: `Notifications.NotificationTests` (`A_resolved_ticket_reaches_the_requester_in_the_app_with_only_fields_they_may_read`, `Sandbox_style_capture_keeps_the_email_and_mutes_and_mandatory_routes_decide_what_is_sent`, `Two_routes_to_the_same_person_and_channel_deliver_once_and_replays_are_explicit`, `Channel_settings_hold_no_secrets_and_live_sends_need_allowed_hosts_and_the_right`, `Delivery_states_follow_the_provider_transient_retries_and_unknown_is_ambiguous`, `Previews_resolve_and_render_without_sending_or_writing`, `Upgrading_tickets_adds_a_mandatory_p1_alert_and_keeps_the_workspace_routes`), `Mcp.McpServerTests.Agent_sets_up_a_notification_but_external_recipients_stay_with_people`, `Isolation.DatabaseIsolationTests`.
  - Playwright: `notifications.spec.ts` `@REQ-NOT-004` (P1 alert in the inbox, mandatory route cannot be muted, email preference kept, delivery history, preview, axe audits).
