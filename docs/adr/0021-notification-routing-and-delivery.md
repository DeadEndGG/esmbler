---
title: "ADR 0021: Notification routing and delivery"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-11
requirements: [REQ-NOT-001, REQ-NOT-002, REQ-NOT-003, REQ-NOT-004, REQ-NOT-005, REQ-NOT-006, REQ-NOT-007, REQ-EVT-009, REQ-AUTHZ-008, REQ-AISAFE-001, REQ-TEN-001, REQ-ALR-001]
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
| `SubscriptionSpec` | Which events matter | `Event` (an event type ID), optional `Entity`, optional `When` condition over `event` (payload) and `record`, `Target` (`notify` for routes; since G2-M19 also `alert` for alert rules, [ADR 0022](0022-alerts-and-audit-policy.md)), `Enabled` |
| `NotificationRouteSpec` | Who hears about a subscription's events, how and with which text | `Subscription`, `Profile`, `Template`, `Recipients` (1 to 50 rules), `Mandatory`, `Enabled` |
| `NotificationProfileSpec` | How a route delivers | `Channels` in order (`in_app`, `email`, `sms`), `Delivery` (`all`, or `fallback`: the first channel the recipient can be reached on), `MinSeverity`, `Enabled` |
| `NotificationTemplateSpec` | The text | `Subject`, `Body`, per-channel `Variants`, `Entity` whose fields placeholders read, `ExternalFields`, `Enabled` |

Recipient rules (`RecipientSpec`) are `field` (a user, group, principals or email field of the record), `role`, `group` (key), `user` (principal ID), `approvers` (the pending approvers of an approval event), `alert_owner` (since G2-M19: the owner group, owner and assignee of the alert a `platform.alert.*` event is about, [ADR 0022](0022-alerts-and-audit-policy.md)) and `address` (an external email address or `+` phone number). People must be enabled members of the workspace and of kind `user`; an address that belongs to a member resolves to that person.

Templates are plain text with the placeholders `{{record.<field>}}`, `{{record.number}}`, `{{event.<field>}}`, `{{recipient.name}}` and `{{link}}`. The validator refuses HTML, unknown placeholders and text over 4,000 characters; rendering caps subjects at 200 characters and SMS bodies at 480.

Whether someone is told, and what the text says, must not reveal a value the recipient could not read. The validator therefore refuses:

- a subscription condition that reads a record field any permission set hides (`config.notification_hidden_field`);
- a subscription condition that reads `event.*` values of a `restricted` event type, and a route whose template (subject, body or any variant) renders `{{event.<field>}}` for a restricted event (`config.restricted_event_text`). Restricted payloads may carry hidden fields (ADR 0017); `{{record.<field>}}` placeholders, which render per recipient, are the way to show record data.

The platform module adds three built-in profiles to every configuration (`PlatformModule.NotificationProfiles`): `core.platform.profile.in_app`, `core.platform.profile.in_app_email` and `core.platform.profile.urgent` (in-app, email and SMS for events of severity `high` or above). Tenants add their own; built-ins can be disabled, not removed.

Overlay ops (`OverlayOps.cs`): `subscription.add|update|remove`, `notification_template.add|update|remove`, `notification_profile.add|update|remove` and `notification_route.add|update|remove`. Tenants add objects with `tenant__` IDs and may update module objects within limits: a module subscription's condition, a module route's recipients and its mandatory flag cannot change, and a mandatory module route cannot be turned off (`overlay.base_immutable`, `overlay.mandatory`). Only tenant objects that nothing uses can be removed (`overlay.in_use`); module objects are disabled instead. Module-layer objects use IDs in their module's namespace, and a module's routes may use only the module's own subscriptions, templates and profiles or those of `core.platform`, not those of other modules, declared dependencies included (`config.foreign_notification_element`, the data boundary of [ADR 0012](0012-repository-layout-and-module-boundaries.md)).

### Layers and mandatory routes

The layers are module routes, tenant routes and people's preferences on top. Preferences (`kernel.notification_preferences`) mute a route, or one channel of a route, for one person.

Resolution processes the routes of a subscription with mandatory routes first, then tenant routes before module routes. The first route to claim a recipient and channel for an event wins that delivery, so a tenant route overrides a module route for the same person and channel. Each subscription resolves in its own job, so a route of another subscription for the same event may have claimed the key first: when that delivery is `suppressed` with reason `muted`, a mandatory route takes the row over (route, template, text and status are replaced), so a person reached by a mandatory route always gets it. Any other existing delivery for the key is left alone.

A mandatory route cannot be muted (the preference API returns `409 mandatory_route` and resolution ignores mutes). Nothing it relies on can be switched off. The validator raises `config.mandatory_unreachable` when a mandatory, enabled route's subscription, template or profile is disabled, or when its profile does not include `in_app`; the bell is always one of a mandatory route's channels.

### Channel types and channel instances

A channel type is a code adapter behind the public SDK contract `INotificationChannel` (`packages/Esmbler.Sdk/Notifications/INotificationChannel.cs`). It sends one rendered message and reports `sent`, `transient`, `permanent` or `unknown`, and says whether the provider deduplicates on the idempotency key (`SupportsIdempotency`). The contract is public so that the shape is stable, but adapters are platform-only for now: modules cannot contribute channels through `IModuleDefinition`, and `Esmbler.Hosting` registers them. The first adapters are `SmtpEmailChannel` (MailKit; no provider idempotency) and `HttpSmsChannel` (a Twilio-style HTTPS API with an idempotency header). `in_app` is built in: it writes the person's inbox row in the database. MailKit and MimeKit are confined to `Esmbler.Kernel.Infrastructure` by an architecture test.

A channel instance is an environment setting, not configuration (`kernel.notification_channels`, migration 0014). It is never part of an artifact and never promoted between environments. It has a key, a type (`email` or `sms`), a mode, settings, secret references and a rate per minute (default 60):

- **Modes.** `live` sends; `capture` keeps the rendered message as a `captured` delivery and sends nothing; `disabled` suppresses. The admin form proposes `capture`. Going `live` needs a host on the operator's allow-list (`host_not_allowed` otherwise), and in a non-production environment it also needs `permissions.admin`. An email or SMS delivery with no instance of its type is `suppressed` with `channel_not_configured`; `in_app` always works.
- **Secret references.** Settings hold no secrets: keys that look like secrets (`password`, `secret`, `token`, `apikey`, `credential`) are refused unless they end in `ref`. Credentials are named in `secretRefs` and resolved at send time by `ISecretStore`, per workspace and environment (`ISecretStore.For(slug, environmentKey)`). The development and single-host adapter `EnvironmentSecretStore` reads `ESMBLER_SECRET__<WORKSPACE_SLUG>__<ENVIRONMENT_KEY>__<NAME>` (slug and environment key upper-cased, hyphens as underscores; for example `ESMBLER_SECRET__ACME__PROD__SMTP_PASSWORD`). Names match `^[A-Z][A-Z0-9_]{1,40}$`, contain no `__` and do not end with `_`, so a workspace reads only its own secrets, a sandbox never reads production's, and no name reaches another workspace's variables or other process variables. The settings that name secrets (`usernameRef`, `passwordRef`, `tokenRef`) must name one of the instance's `secretRefs` (`invalid_secret_ref`), so what the page shows is what the adapter reads. Without those settings the adapters use the default names `SMTP_USERNAME`, `SMTP_PASSWORD` and `SMS_AUTH_TOKEN`, and adapters only ever read names the instance lists under `secretRefs` (the deliverer hands them a declared-names-only accessor), so the page shows every secret a channel uses. The channel views return each secret name with whether it is configured, never its value. A vault adapter replaces it in production ([human actions](../human-actions.md), G2-M12).
- **Authentication.** The SMTP adapter authenticates unless the instance setting `auth` is `none` (a relay that needs no login); when credentials are missing it never falls back to an unauthenticated send, and the delivery fails permanently with `credentials_missing`.
- **Host allow-lists.** `Esmbler:Notifications:AllowedEmailHosts` (empty by default: email can only be captured) and `AllowedSmsHosts` (default `api.twilio.com`) are operator settings. The adapters check the host again on every send, and the SMS `HttpClient` (`notifications.sms`) does not follow redirects, so the allow-listed host is the only one it talks to.

### Rendering and what recipients see

A message is rendered once per recipient and channel when the event is resolved, and the delivery row stores the subject and body. Retries and replays send exactly that text, even if the template or the record changes later. Who receives it is checked again before every send (see Deliver): a person who has left, been disabled or lost access to the record in between is not sent the stored text.

`NotificationRenderer` renders under the recipient's own permissions. A person is skipped when they cannot read the record; each `{{record.<field>}}` renders empty when the field is hidden from them. `{{event.<field>}}` renders empty for everyone when the event type is `restricted`, and an event whose sensitivity is missing counts as restricted (ASM-053). External recipients see only the template's `ExternalFields`, `{{record.number}}` only when the entity's auto-number field is one of them, and `{{event.<field>}}` values only for events whose sensitivity is `public`.

External recipients are `address` rules that do not belong to a member and, for validation, every `field` recipient over an email field of the record (its value can be any address). The validator refuses routes that send non-public events to external recipients (`config.external_redaction`), and `ConfigCompiler.PermissionDiff` counts email-field recipients as external.

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
- **Resolve.** `NotifyResolveJobHandler` builds the deliveries. The idempotency key is `{event}:{principal or lower-cased address}:{channel}` and is unique per environment, so two routes, layers or retried jobs cannot deliver the same event to the same person on the same channel twice. Muted recipients get a `suppressed` row (reason `muted`), so the history shows why nothing went out; a mandatory route of another subscription replaces such a row (see Layers). Each route's targets are cut to `MaxRecipientsPerEvent` (default 200) before any permission check, so a huge group cannot make resolution run thousands of checks, and at most that many deliveries are created per event across its routes; when either limit is reached, resolution writes the audit entry `notification.recipients_capped` (outcome `failed`) and keeps what it created.
- **Deliver.** `NotifyDeliverJobHandler` handles one delivery. The statuses are `queued`, `sending`, `sent`, `failed`, `ambiguous`, `captured` and `suppressed`; every attempt is appended to `kernel.notification_delivery_attempts`.
  - Before sending to a person it checks them again: an enabled user, still a member, who can read the record under the published configuration with fresh relationship facts. Otherwise the delivery becomes `suppressed` with `no_longer_authorized` and nothing is sent. Email goes to the person's current address, not the one at resolve time.
  - A transient result is retried with the job runner's backoff. When retries run out, a `queued` delivery becomes `failed` (`retries_exhausted`); one caught in `sending` becomes `ambiguous` (`interrupted`), because the provider may already have it.
  - An unknown result is retried only when the adapter supports provider idempotency. Otherwise the delivery is `ambiguous`, and so is a delivery found in `sending` after a crash. An ambiguous outcome is never retried blindly.
  - The rate per minute of an instance is decided under a transaction advisory lock per workspace, environment and instance, and counts messages sent in the last minute plus those being sent, so parallel deliverers cannot overshoot it. Over the rate, the job is rescheduled 20 seconds later without spending an attempt.
  - Adapter exceptions become `unknown` (`adapter_error`) and are never logged, because they can carry provider responses.
- **Replay** (audited `notification.replay`). Failed and ambiguous deliveries are requeued with their own idempotency key. Replaying an `ambiguous` delivery on a channel without provider idempotency (SMTP) needs `force` (`ambiguous_delivery` otherwise), since it can deliver the message twice. A sent, captured or suppressed delivery is copied to a new delivery (`replay_of`, key `{key}:replay:{n}`) only with `force`, so nobody is told twice by accident. A delivery `suppressed` because the person muted it (`muted`) or lost access (`no_longer_authorized`) is never replayed, even with `force` (`not_replayable`): those are decisions, not failures.
- **Test send** (audited `notification.test_send`). Sends a fixed message to the caller's own email through an instance, following its mode. SMS test sends are refused because people have no phone numbers on file yet.
- **Preview** (`NotificationService.PreviewAsync`, MCP `preview_notification`). Resolves a route of the working configuration (the open draft, if any) for an optional record and renders it, without sending or writing anything. Access is judged on the published configuration with the caller's real context, so an agent keeps its scopes:
  - Rendering with a record needs the caller to read it (404 otherwise); MCP `preview_notification` refuses a `record` without `records:read` (`scope_missing`), which the default builder delegation does not have.
  - Recipients are checked against the published configuration as the delivery job checks them. Recipient rules that name a field hidden from the caller are left out (with a note), so a preview never reveals a hidden value.
  - A recipient who would not be sent the message shows "not sent" with no text and no reason, so a preview reveals neither someone's access nor their mutes. Per-channel mutes remove that channel from the recipient's list.
  - Text shows only fields both the recipient and the caller may see.
  - Notes say when no record was chosen, when the subscription's condition does not match the record (with sample event values), when the profile's minimum severity filters the event type out, and when more than 20 recipients exist (at most 20 are shown).
  - Known limits: it renders the variant of the recipient's first channel only and applies neither cross-route deduplication nor the recipient cap ([KI-026](../known-issues.md)).

### Inbox, preferences and administration

- `kernel.notifications` stays the in-app inbox and gains `read_at`, `delivery_id`, `severity`, `entity_id` and `record_id` (migration 0014). REST `/api/me/notifications` (list, `unread-count`, `{id}/read`, `read-all`) and `/api/me/notification-preferences` (list and set mutes) act only on the signed-in person; AI agents have no inbox and are refused.
- REST `/api/admin/notifications` (`channels`, `channels/{key}`, `channels/{key}/test`, `deliveries`, `deliveries/{id}/replay`, `preview`).
- Web: `/notifications` (inbox and "What I receive") and `/admin/notifications` (channels, deliveries with replay, route preview). The channel form edits `from`, `host`, `port` and `accountSid` and keeps every other setting of the instance (`usernameRef`, `auth`, `security` and so on) when saving. The navigation shows an unread badge, refreshed every 60 seconds while the page is visible.
- Routes, templates, profiles and subscriptions are edited in drafts: through the AI builder and MCP tools, and published through the normal review.

### Authorization

- The new platform action `notifications.manage` covers channel instances, delivery inspection, replay and test sends. `permissions.admin` implies it, and `EffectivePermissions` strips it from AI agents.
- Delivery history (status, reason, attempts, masked recipients) is readable with `notifications.manage` or `audit.read`. The delivered subject and body are returned only to permission administrators (`permissions.admin`), who can see every field anyway: text was rendered for the recipient, who may see more than the caller. `notifications.manage` alone, audit readers and agents get status only; the recipient reads the text in their inbox (ASM-080).
- Configuration rights (`config.edit`) allow previews and the channel summary (type, mode and readiness only, no settings, hosts or secret names).
- Agents edit notification configuration in drafts through MCP (`notify_when`, `add_subscription`, `add_notification_template`, `update_notification_template`, `add_notification_profile`, `add_notification_route`, `update_notification_route`, and the read-only `list_notification_setup`, `preview_notification`, `list_notification_deliveries`).
- Making a route mandatory or optional, adding external `address` recipients to a route and changing a template's `ExternalFields` are permission changes (`AffectsPermissions`, listed by `ConfigCompiler.PermissionDiff`). They need `permissions.admin` to apply, undo and publish, and agents are refused. A `field` recipient over an email field is external too, but the op alone cannot tell the field's type, so it is caught at publish by `PermissionDiff` rather than when applied to a draft.
- A mandatory route can be silenced, and a route with external recipients widened, through what it relies on. For those routes `PermissionDiff` compares a reach signature before and after: the route's recipients and enabled flag; its subscription's event, entity, condition and enabled flag; its template's text, variants, external fields and enabled flag; its profile's channels, delivery, minimum severity and enabled flag; and the rules and workflow transitions that emit the event (IDs, enabled flags, rule conditions). Any difference is a permission change, so publishing it needs `permissions.admin`.
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
- Bad: rendering at resolve time means text reflects the record at the event, not at the moment of sending (intended, but it can be minutes old under rate limits). The recipient's access is checked again at send time, so stale text never reaches someone who lost access.
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

Done since: retention of deliveries, attempts and inbox rows in G2-M19 ([ADR 0022](0022-alerts-and-audit-policy.md); [KI-024](../known-issues.md) fixed): final-state deliveries and their attempts go 90 days after their last update, inbox items 30 days after being read and unread ones after 365 days, never during a legal hold.

- **Team layer** between tenant and personal preferences (REQ-NOT-004).
- **Quiet hours, schedules, business-calendar delivery windows** and fallback to the next channel after a failure; `fallback` currently means the first channel the person can be reached on (REQ-NOT-003).
- **Approvals onto routes:** approval notifications are still inbox rows written by `ApprovalService`.
- **Migrating legacy templates to routes:** Gate 1 templates with their own trigger still go through `NotificationConsumer`.
- **Live push:** the web app polls the unread count every 60 seconds.
- **Admin Center editing of routes, templates, profiles and subscriptions** (REQ-NOT-007): done in drafts through the AI builder and MCP; the Admin Center manages channel instances, deliveries and previews only.
- Phone numbers on people (SMS reaches only `address` recipients today, [KI-028](../known-issues.md)), a production vault adapter (G2-M12) and other subscription targets (automation, webhooks; the `alert` target shipped in G2-M19, [ADR 0022](0022-alerts-and-audit-policy.md)).
- Provider callbacks for delivered and read, a per-recipient SMS limit, and honoring a provider's `Retry-After` (REQ-NOT-005).
- **Builder and registry:** the builder's Notifications tab and registry `spec_object` elements for subscriptions, profiles and routes; `spec_object.notification_template.json` still describes legacy trigger templates ([KI-030](../known-issues.md)).

## Security notes

- **Exfiltration and SSRF.** Only allow-listed provider hosts are used live, adapters re-check them on every send, and the SMS client does not follow redirects. External recipients (addresses and email fields) get public events and allow-listed fields only. Adding external recipients or external fields, or widening an external route through its subscription, template, profile or emitters, is a permission change agents cannot make. Live mode in a sandbox needs `permissions.admin`. Threat model [T-73](../security/threat-model.md#authentication-web-and-availability).
- **Secrets.** Only secret names are stored, values come from the secret store at send time per workspace and environment (a sandbox never reads production credentials), secret-like settings are refused, `*Ref` settings must name declared secrets, SMTP never sends unauthenticated because credentials are missing, and audit records secret names only. Adapter exceptions are not logged. Threat model [T-74](../security/threat-model.md#secrets).
- **Hidden values.** Conditions cannot read hidden fields or restricted event values, restricted event values never become text, the recipient is re-checked before each send, delivery text is shown only to permission administrators, previews hide recipients from hidden fields and the reasons someone is not sent a message, and mutes and lost access are never replayed. Threat model [T-76](../security/threat-model.md#authentication-web-and-availability).
- **Flooding.** At most 200 targets per route (cut before permission checks) and 200 deliveries per event (audited when capped), 50 recipient rules per route, a rate per minute per instance decided under a lock, deduplication by key, and test sends under the per-person `workspaces` rate limit. Mandatory routes are a permission change, and so is silencing one through what it relies on. Threat model [T-75](../security/threat-model.md#authentication-web-and-availability).
- **Isolation.** All new tables have FORCE RLS on tenant and environment (the isolation test checks every `kernel` table); jobs run in the event's tenant and environment.

## More information

- Related: [ADR 0017](0017-event-audit-alert-notification-services.md) (the service design), [ADR 0003](0003-configuration-artifact-and-overlay-format.md) (overlay ops), [ADR 0005](0005-job-and-workflow-execution.md) (jobs), [ADR 0010](0010-database-migrations-and-roles.md) (migration 0014), [ADR 0016](0016-approvals.md) (approvers), [design](../architecture/events-audit-alerts-notifications.md).
- Assumptions: ASM-076 to ASM-094 in [assumptions](../decisions/assumptions.md).
- Verification:
  - Unit: `Configuration.NotificationSpecTests` (`A_subscription_route_and_template_compile_with_the_built_in_profiles`, `Subscriptions_need_a_known_event_and_a_valid_condition`, `Templates_are_plain_text_with_known_placeholders`, `Routes_check_their_recipients_and_never_send_internal_events_outside`, `Mandatory_routes_and_external_recipients_are_permission_changes`, `Mandatory_routes_cannot_be_turned_off_or_removed_and_used_objects_stay`, `Conditions_and_text_never_carry_hidden_values`, `Email_fields_are_external_and_mandatory_or_external_routes_cannot_change_quietly`, `Modules_keep_notification_elements_in_their_namespace_and_route_only_through_their_own`, `Tickets_routes_escalations_and_makes_the_p1_alert_mandatory`); architecture `Only_infrastructure_talks_to_mail_servers`.
  - Integration: `Notifications.NotificationTests` (`A_resolved_ticket_reaches_the_requester_in_the_app_with_only_fields_they_may_read`, `Sandbox_style_capture_keeps_the_email_and_mutes_and_mandatory_routes_decide_what_is_sent`, `Two_routes_to_the_same_person_and_channel_deliver_once_and_replays_are_explicit`, `Channel_settings_hold_no_secrets_and_live_sends_need_allowed_hosts_and_the_right`, `Delivery_states_follow_the_provider_transient_retries_and_unknown_is_ambiguous`, `Deliveries_check_the_recipient_again_and_never_override_a_mute_or_reach_other_workspaces`, `A_mandatory_route_reaches_a_person_who_muted_another_route_for_the_same_event`, `Previews_resolve_and_render_without_sending_or_writing`, `Upgrading_tickets_adds_a_mandatory_p1_alert_and_keeps_the_workspace_routes`), `Mcp.McpServerTests.Agent_sets_up_a_notification_but_external_recipients_stay_with_people` (including `preview_notification` with a record refused without `records:read`), `Isolation.DatabaseIsolationTests`.
  - Not automated yet: one event through two profiles and three channels with an SMS outage, and expected-value conflict tests for the 12 notification overlay ops ([KI-027](../known-issues.md)).
  - Playwright: `notifications.spec.ts` `@REQ-NOT-004` (P1 alert in the inbox, mandatory route cannot be muted, email preference kept, delivery history, preview, axe audits).
