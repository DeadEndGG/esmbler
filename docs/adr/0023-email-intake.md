---
title: "ADR 0023: Email intake"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-11
requirements: [REQ-BIZ-003, REQ-EVT-007, REQ-ACC-13, REQ-BIZ-002, REQ-NOT-002, REQ-NOT-005, REQ-AUTHZ-001, REQ-AISAFE-001, REQ-TEN-001]
---

# ADR 0023: Email intake

## Context and problem statement

The master spec asks for email intake: "threading and sender verification policy; retries/deduplication; unknown senders; auto-reply/loop prevention; attachment processing; prevention of internal-note disclosure" (REQ-BIZ-003). Acceptance scenario 13 adds "email loops/duplicate intake, public-versus-private notes" (REQ-ACC-13). The Zendesk research ([Zendesk ticketing](../research/zendesk-ticketing.md)) proposed header threading with a per-ticket fallback token, a suspended queue with reason codes, RFC 3834 loop prevention, CCs versus followers, and private replies from people who are not the requester.

G2-M18 built outbound email: the SMTP channel adapter (MailKit), channel instances as environment settings, secret references, host allow-lists, and per-recipient deliveries rendered under each recipient's permissions ([ADR 0021](0021-notification-routing-and-delivery.md)). Before G2-M5 nothing received mail, and outbound email carried no threading headers and no reply text, so a requester could neither open a ticket by email nor answer one.

This ADR settles:

- How does a module say what an email turns into, and where do mailboxes and their credentials live?
- How does a message arrive (provider webhook, IMAP polling) and how is it deduplicated?
- Who does the platform act as when it writes a record from an email, and how is a spoofed or automatic message kept out?
- How does a reply find its record, and what happens when the record is resolved or closed?
- What does outbound email carry so that replies thread, auto-responders stay quiet and internal notes never leave?
- What is kept, for how long, and what is left for later (attachments)?

## Decision drivers

- An email never gives anyone more than the sender's own rights, and the platform never writes as someone the mail server did not vouch for (REQ-AUTHZ-001, REQ-BIZ-003).
- Internal notes never reach email; replies by email are public replies of their author (REQ-BIZ-002, REQ-ACC-13).
- A provider that delivers twice, a poll that sees a message again, or a retried job never creates a second record or reply (REQ-EVT-007).
- Mail loops and auto-responder storms stop on both sides: the platform does not answer automatic mail, and its own mail tells auto-responders not to answer (RFC 3834).
- What an email turns into is validated configuration that modules ship (dogfooding); credentials and addresses are environment settings, never artifacts.
- Every new table is isolated by tenant and environment with FORCE RLS; cross-tenant work goes only through narrow definer functions (REQ-TEN-001).
- AI agents never decide on inbound mail (REQ-AISAFE-001).

## Considered options

1. **A `MailIntakeSpec` spec object in modules, mailboxes as environment settings, a webhook (raw RFC 5322 with a per-mailbox secret token) plus IMAP polling with MailKit, store once, process in a durable job as the sender, suspended queue for people.**
2. A provider-specific inbound parser (for example SendGrid Inbound Parse or Mailgun routes posting JSON fields) as the only intake.
3. Hard-coded Tickets behavior in the kernel (an "email channel" that always creates tickets).
4. Process mail as a platform service account with elevated rights, then set the requester field to the sender.

## Decision outcome

**Chosen option 1.**

### Mail intakes as configuration

`MailIntakeSpec` (`packages/Esmbler.Sdk/Specs/Mail.cs`) is a public spec object in module releases (`SpecCatalog.MailIntakes`, composed by `Composer`, listed by `ConfigDiff` as `mail_intake`, linked by `DependencyGraph` to its entity, fields and workflow):

| Property | Meaning |
| --- | --- |
| `Entity` | The entity new messages create |
| `TitleField` | Short or long text field that takes the subject (cut to the field's length) |
| `BodyField` | Long text or rich text field that takes the new text of the message |
| `RequesterField` | User field for the sender (left to its default when the sender creates the record) |
| `AddressField` | Email field that keeps the sender's address |
| `Set` | Constant values for new records, such as `channel = email` (choice, text or toggle fields only) |
| `ReopenTransition`, `ReopenFrom` | The transition a reply runs (as its sender) when the record is in one of these states |
| `FollowUpFrom` | States in which a reply starts a new record instead (closed, cancelled) |
| `Enabled` | On or off |

`ConfigValidator.Mail.cs` refuses unknown entities, fields of the wrong type (`config.mail_bad_field`), constants on people, group, auto-number, status, system or requester fields, unknown transitions and states (`config.not_found`), states to reopen from without a transition and a state in both lists (`config.mail_reopen`). Module intakes stay in the module's namespace and use the module's own entities (`config.foreign_notification_element`), and IDs are unique across the catalog.

There are no overlay ops for mail intakes yet: tenants cannot add or change one, and they turn intake off through the mailbox's mode ([KI-047](../known-issues.md), ASM-128). When ops are added, changes to `Set` and `RequesterField` must count as permission changes, since they decide what an email writes and for whom.

`Esmbler.Sdk.Specs.MailConventions.ReplyMarker` (`##- Reply above this line -##`) is the public line modules put at the top of emails a person may answer; everything from it down is dropped from a reply. `MailReasons` lists the stable reason codes reviewers see.

### Mailboxes as environment settings

A mailbox (`kernel.mail_inboxes`, migration `0018_mail_intake.sql`) is an environment setting like a channel instance: never in an artifact, never promoted. It has a key, a label, its address, the intake it feeds, a mode (`webhook`, `imap`, `disabled`) and settings:

| Setting | Meaning |
| --- | --- |
| `senderVerification` | `required` (default) or `none`; only people with permission administration may set `none` |
| `authservId` | The authserv-id of the mail server whose `Authentication-Results` header is trusted; required unless verification is `none` |
| `maxPerSenderPerHour` | 1 to 500, default 20 |
| `host`, `port`, `security`, `folder` | IMAP server (on the operator allow-list), port (993), `ssl` or `starttls`, folder (`INBOX`) |
| `usernameRef`, `passwordRef` | Secret names, resolved per workspace environment through `ISecretStore` |

Unknown settings are refused, so a password can never be stored as a setting. People with `notifications.manage` (or `permissions.admin`) list, save and rotate mailboxes with fresh facts; agents are refused. Saving is audited as `mail.inbox_save` (mode, intake, address, verification); rotating as `mail.token_rotate`. `GET /api/admin/mail/intakes` lists the published intakes (ID, label, entity, enabled) for the mailbox form.

### Receiving

```mermaid
sequenceDiagram
    autonumber
    participant MTA as Workspace mail server
    participant API as POST /api/mail/inbound
    participant W as Worker (ImapPollingService)
    participant Rx as MailIntakeService.ReceiveAsync
    participant DB as kernel.inbound_messages
    participant Job as mail.process job
    participant Rec as RecordService (as the sender)
    participant N as Notifications (ADR 0021)
    alt webhook mailbox
        MTA->>API: raw RFC 5322, Authorization: Bearer esm_mail_...
        API->>API: rate limit 300/min per address, 10 MB cap
        API->>API: kernel.mail_inbox_by_token(sha256(token)) names tenant, environment, mailbox
    else IMAP mailbox
        W->>MTA: every minute, mailboxes due (allow-listed host, secret references)
        MTA-->>W: up to 50 new UIDs, each marked Seen
    end
    API->>Rx: store
    W->>Rx: store
    Rx->>DB: one row per mailbox and Message-ID (or content hash); duplicate returns the first
    Rx->>Job: schedule mail.process (dedup mail:{id}) in the same transaction
    Job->>DB: lock row; guards in order; thread lookup
    alt a guard stops it
        Job->>DB: suspended or rejected with a reason, audited
    else reply on a thread
        Job->>Rec: the requester's public reply, or another member's internal note (source email), message marked processed in the same transaction
        Rec->>Rec: reopen transition as the sender if the record is resolved
    else new message or follow-up
        Job->>Rec: create as the sender (idempotency key mail:{id})
    end
    Rec->>N: activity.added / record.created events
    N->>MTA: email with Message-ID, References, Reply-To, Auto-Submitted, {{activity.body}}
```

- **Webhook.** `POST /api/mail/inbound` (`apps/Esmbler.Api/Endpoints/MailEndpoints.cs`) takes the raw message as the body and the mailbox token as a bearer token. There is no session: the token alone names one enabled webhook mailbox of an active workspace, through the definer function `kernel.mail_inbox_by_token`, which compares the SHA-256 of the token with the stored hash (the token itself is shown once, on creation or rotation, and starts with `esm_mail_`). The endpoint is rate limited per remote address (`LoginThrottle.InboundMailPolicy`, 300 a minute) before the token is checked, refuses bodies over 10 MB while reading (413) and answers 202 with the message ID and whether it was a duplicate. Any provider that can forward raw MIME (an MTA pipe, Mailgun or SendGrid raw mode, a forwarding rule) can use it.
- **IMAP polling, the real provider.** `ImapPoller` (`packages/Esmbler.Kernel.Infrastructure/Mail/ImapPoller.cs`, MailKit) runs in the worker (`ImapPollingService`) every minute. `kernel.mail_inboxes_due` lists IMAP mailboxes of active workspaces not polled in the last two minutes, least recently polled first (20 per pass). For each, the poller re-checks the host against `Esmbler:Notifications:AllowedImapHosts` (empty by default), reads the credentials through secret references, fetches up to 50 UIDs after the last one seen (starting over when `UIDVALIDITY` changes), skips messages over 10 MB, hands each to `ReceiveAsync`, marks it `\Seen` and stores the last UID. Errors are stored on the mailbox as short codes (`host_not_allowed`, `credentials_missing`, `authentication_failed`, `connect_failed`, `folder_not_found`), never the server's text.
- **Store once.** `ReceiveAsync` reads only the Message-ID header, then inserts one `kernel.inbound_messages` row keyed by mailbox and `id:<message-id>` (or `sha256:<content>` without one), with a unique index; a second delivery returns the first row (`duplicate: true`), also when two deliveries race. The same transaction schedules the durable job `mail.process` (dedup `mail:{id}`), so a stored message is always processed.

### Processing and the guards

`MailIntakeService.ProcessAsync` locks the row and stops at the first guard that fails. Rejected messages are never processed; suspended ones wait for a person. Each stop is audited (`mail.rejected`, `mail.suspended`). A message whose job keeps failing until its retries run out is marked `failed` with `processing_failed` (`MailProcessJobHandler.OnAbandonedAsync`), so people see it instead of it waiting forever.

| Order | Guard | Outcome |
| --- | --- | --- |
| 1 | The mailbox is disabled, or its intake or entity is missing or disabled in the published configuration | suspended `no_intake` |
| 2 | The message cannot be parsed | failed `unreadable` |
| 3 | Not exactly one valid From address | suspended `bad_sender` |
| 4 | Own mail: the Message-ID is one of the platform's deliveries, or the sender is one of the workspace's mailboxes or an email channel's `from` | rejected `own_message` |
| 5 | Automatic mail (RFC 3834): `Auto-Submitted` other than `no`, `Precedence` bulk, junk, list or auto_reply, `X-Autoreply`, `X-Autorespond`, `List-Id`, `List-Unsubscribe`, `X-Loop`, a `multipart/report` delivery report, a null `Return-Path`, or a system sender such as `mailer-daemon` or `noreply` | rejected `auto_reply` |
| 6 | More than `maxPerSenderPerHour` messages from this sender to this mailbox in the last hour | suspended `too_many_from_sender` |
| 7 | Sender verification (unless the mailbox says `none`) | suspended `unverified_sender` |
| 8 | No text and no subject | rejected `empty` |
| 9 | The sender is not an enabled user who is a member of the workspace | suspended `unknown_sender` |
| 10 | The record service refuses the write (403 or 404) or the record would be invalid | suspended `not_allowed` or `invalid_record` |

**Sender verification.** The platform runs no SPF, DKIM or DMARC checks itself. `MailGuard.Verify` reads only the topmost `Authentication-Results` header, which must carry the mailbox's trusted `authservId` (anything below it could come from the sender), after removing comments and quoted strings (RFC 8601), so text a sender controls inside them cannot pose as a result. It accepts a `dmarc=pass` whose `header.from` is the From domain, a `dkim=pass` whose `header.d` (or `header.i`) is aligned, or an `spf=pass` whose `smtp.mailfrom` is aligned (never `smtp.helo`). Alignment is relaxed (RFC 7489): the authenticated domain is the From domain or a parent or child of it, never a different domain; there is no public suffix list ([KI-051](../known-issues.md), ASM-116). Turning verification off (`none`) lets anyone write as any member, so it needs permission administration.

**Text.** `MailParser` (MimeKit) takes the plain-text part, or the HTML part turned into text without scripts and styles. `MailGuard.NewText` keeps only the new part of a reply: text above the reply marker, an "On ... wrote:" line (also wrapped), `-----Original Message-----`, an underscore separator or an Outlook `From:`/`Sent:` block, without quoted `>` lines or the signature separator (a line of two dashes and a space), at most 20,000 characters. Subjects lose `Re:`/`Fwd:` prefixes (also `AW`, `WG`, `SV`, `VS`), the `[NUMBER]` tag and control characters. Plain text becomes escaped rich text (`MailGuard.ToRichText`) and is sanitized again by the record service.

### Writing as the sender

Every write runs through `RecordService` as the sender, with the sender's own permissions, rules and workflow, on channel `email`:

- **Threading.** `MailThreading.FindAsync` looks for the record of the intake's entity that the message answers: first a delivery whose Message-ID is in `In-Reply-To` or `References` (newest first, at most 20), then an earlier inbound message that created or replied to a record, then up to three `[NUMBER]` tags in the subject matched against the entity's auto-number field. Finding a record grants nothing: the sender still needs the right to write on it, so a guessed number from someone who may not is suspended as `not_allowed`.
- **Reply.** Only the record's requester (the sender is the person in `RequesterField`) adds a `public_reply`. Any other member who answers by email (an agent replying to an escalation email whose Reply-To is the mailbox, a colleague) adds an internal `work_note`, which needs their right to write notes, so nothing reaches the requester by accident. Both carry `source = email` (migration 0018 adds `kernel.activity.source`; the conversation shows an Email badge), and how the record was found is kept on the message (`threadedBy`: `header` or `subject`). The message is marked processed in the same transaction as the activity, so a retried job never adds it twice. When the requester's reply finds the record in a `ReopenFrom` state, the requester then runs `ReopenTransition`; if they may not, the reply stands and the record stays as it is.
- **Follow-up.** When the record is in a `FollowUpFrom` state, the reply creates a new record instead (outcome `follow_up`), not linked to the closed one (ASM-119).
- **New record.** The title from the subject (or the first line), the body from the new text, `Set` constants and the sender's address; the requester field is left to its default, which is the creating person (ASM-117). The create carries the idempotency key `mail:{id}`.
- **CCs and attachments.** `To` and `Cc` addresses (50 each) and attachment names, types and sizes (50) are recorded on the message for reviewers. CCs do not become participants ([KI-048](../known-issues.md)); attachment contents are not imported ([KI-046](../known-issues.md), G2-M7).

### Review of suspended mail

People who work an intake's entity (they read all its records and may create them) review its mailboxes' messages at `/mail` (`GET /api/mail/messages`, detail, `accept`, `reject`); the bootstrap's `mail` flag shows the page to them when a mailbox exists. Agents are refused. The detail shows the new text, To and Cc, the verification result, automatic-mail signals and attachment names.

- **Accept** reprocesses the message without the own-mail, automatic-mail, rate and verification guards, but never without the membership and permission checks. The platform acts as the sender only when their message was verified (for example one suspended as `invalid_record` or `not_allowed`); otherwise, and always for an unknown sender, the reviewer acts with their own rights on the sender's behalf: a new record is created by the reviewer with the claimed member (or, for an unknown sender, the member the reviewer names) as requester and the sender's address in `AddressField`, and a thread reply becomes the reviewer's internal `work_note` ("Email from ..."), never a public reply (ASM-118). A forged message can therefore never act as the person it claims to come from. Accepting is audited as `mail.accept` (with `onBehalf`).
- **Reject** drops the raw message at once and is audited as `mail.reject`. A decided message cannot be decided again (409).

### Outbound threading and loop prevention

`ChannelMessage` (`packages/Esmbler.Sdk/Notifications/INotificationChannel.cs`) gains `MessageId`, `References`, `ReplyTo` and `AutoSubmitted`:

- `notify.deliver` gives every email delivery an opaque random Message-ID in the sending address's domain (`MailThreading.NewMessageId`; no record, person or event in it), stored on `kernel.notification_deliveries.message_id`, also in capture mode. Deliveries are purged after 90 days; after that the `[NUMBER]` tag is the fallback.
- `MailThreading.ForRecordAsync` adds the first inbound Message-ID that created the record as `References` and `In-Reply-To`, so the requester's mail client threads the conversation, and the address of the first mailbox (by key) whose enabled intake feeds the record's entity as `Reply-To`.
- `SmtpEmailChannel` sets `Auto-Submitted: auto-generated` and `X-Auto-Response-Suppress: All` on every notification, so auto-responders stay quiet.

### Reply text in notifications

Templates may use `{{activity.body}}` (`NotificationRenderer`, allowed by the validator). It renders the plain text (`NotificationRenderer.PlainText`) of the public reply an `activity.added` event is about, and only that: internal work notes, redacted replies and any other event render empty, and external recipients always get an empty value. `NotifyResolveJobHandler.ReplyAsync` reads the reply, and the reply's author is not notified of their own reply.

### Retention

`kernel.purge_inbound_mail` (definer, run hourly by `AuditMaintenance.PurgeAsync`) drops the raw content of processed and rejected messages a day after processing and removes messages 30 days after receipt (the floor in SQL), suspended ones included, never during a legal hold, audited as `retention.purged` (`inbound_message`).

### Tickets 1.9.0

Tickets 1.9.0 ships the intake `core.tickets.mail.tickets` (title, description, requester, `contact_email`, `channel = email`, reopen from resolved through `reopen`, follow-up from closed and cancelled), the subscriptions `core.tickets.sub.public_reply` (`activity.added` with `event.kind == 'public_reply'`) and `core.tickets.sub.created_by_email`, the templates `core.tickets.template.public_reply` (reply marker, `{{activity.body}}`, link) and `core.tickets.template.received_by_email`, and routes to the requester in the app and by email. It is additive and changes no permission set.

### Consequences

- Good: email opens and continues tickets with exactly the sender's rights; a spoofed, automatic, looping or unknown message never writes anything on its own, and people see why.
- Good: intake is idempotent end to end (unique message key, keyed job, idempotent create, reply marked in the activity's transaction).
- Good: internal notes never reach email, and outbound mail threads in the requester's client and keeps auto-responders quiet.
- Good: what email does is module configuration, validated and versioned; another module can ship its own intake.
- Bad: sender verification depends on the workspace's mail server adding a trustworthy `Authentication-Results` header; a mailbox set to `none` (a permission administrator's decision) trusts any From address (still limited to members' own rights).
- Bad: no attachments, CCs as followers, tenant customization of intakes or MCP review tools yet (KI-046 to KI-053).
- Bad: IMAP polling adds up to a few minutes of latency, and IMAP needs a password (no OAuth2, [KI-052](../known-issues.md)).
- Neutral: two more definer functions and a third purge path.

## Pros and cons of the options

| Option | Pros | Cons |
| --- | --- | --- |
| 1. Spec object, mailboxes as settings, raw webhook plus IMAP, store once, job as the sender | Works with any provider or forwarding rule; one parser; dogfooded configuration; same permissions as the app; durable and idempotent | Sender verification relies on the receiving server's `Authentication-Results`; IMAP latency |
| 2. Provider-specific parsed webhooks | No MIME parsing in the platform | Ties the platform to one provider account (a human action) and its field mapping; loses headers needed for threading and verification |
| 3. Kernel-hard-coded ticket channel | Least code | Violates dogfooding; no other module could use email; not reviewable configuration |
| 4. Service account writes, requester set afterwards | No suspended queue for membership | Any forged From address could write with elevated rights; rules and permissions would judge the wrong person |

## Deferred

- **Attachments** imported into the record with scanning and limits (G2-M7, [KI-046](../known-issues.md)).
- **Overlay ops and builder or MCP editing** of mail intakes ([KI-047](../known-issues.md)).
- **CCs as followers**, and replies from CCs that are not the requester ([KI-048](../known-issues.md)).
- **HTML email, per-intake signatures and templates** ([KI-049](../known-issues.md)).
- **DKIM signing** of outbound mail by the platform ([KI-050](../known-issues.md), [human actions](../human-actions.md)).
- **A public suffix list** for alignment ([KI-051](../known-issues.md)) and **IMAP OAuth2** ([KI-052](../known-issues.md)).
- **MCP tools** to read the review queue ([KI-053](../known-issues.md)).

## Security notes

- **Spoofing.** Only the trusted server's topmost aligned pass verifies a sender; everything else is suspended. Accepted mail runs as the sender only when it was verified, otherwise as the reviewer on their behalf. Threat model [T-81](../security/threat-model.md#email-intake).
- **Loops and storms.** Own mail and RFC 3834 automatic mail are rejected, `Auto-Submitted` is set on every notification, the reply author is not notified, and each sender is capped per hour. [T-82](../security/threat-model.md#email-intake).
- **Replying into others' records.** Threading never grants access. [T-83](../security/threat-model.md#email-intake).
- **Internal notes.** `{{activity.body}}` is public replies only, never to external recipients; only the requester's own email becomes a public reply, and everyone else's (members and accepted non-members) becomes an internal note. [T-84](../security/threat-model.md#email-intake).
- **Webhook tokens, MIME, SSRF, credentials and isolation.** [T-85 to T-89](../security/threat-model.md#email-intake). `kernel.mail_inboxes` and `kernel.inbound_messages` have FORCE RLS on tenant and environment; the three new definer functions are allow-listed in `Isolation.DatabaseIsolationTests.Only_allow_listed_security_definer_functions_exist`. MailKit and MimeKit stay in `Esmbler.Kernel.Infrastructure` (`Only_infrastructure_talks_to_mail_servers`, which also checks `Esmbler.Api` and `Esmbler.Worker`).
- **Untrusted content.** Email text is record data like any other: sanitized as rich text, and wrapped as untrusted data when an AI agent reads it ([T-20](../security/threat-model.md#ai-tool-misuse-and-prompt-injection)).

## More information

- Related: [ADR 0021](0021-notification-routing-and-delivery.md) (channels, deliveries, templates), [ADR 0022](0022-alerts-and-audit-policy.md) (retention and legal holds), [ADR 0005](0005-job-and-workflow-execution.md) (jobs), [ADR 0010](0010-database-migrations-and-roles.md) (migration 0018, definer functions), [ADR 0012](0012-repository-layout-and-module-boundaries.md) (MailKit boundary), [design](../architecture/events-audit-alerts-notifications.md).
- Assumptions: ASM-115 to ASM-131 in [assumptions](../decisions/assumptions.md).
- Verification:
  - Unit: `Mail.MailGuardTests` (`Only_the_trusted_servers_aligned_pass_verifies_a_sender`, `The_topmost_result_of_the_trusted_server_counts_and_nothing_verifies_without_one`, `Only_the_new_part_of_a_reply_is_kept`, `Plain_text_becomes_escaped_rich_text`, `Subjects_lose_prefixes_numbers_and_control_characters`, `Record_numbers_are_found_in_square_brackets`, `Automatic_mail_is_recognized`, `Html_only_mail_is_read_as_text_without_scripts`), `Configuration.MailIntakeSpecTests` (`A_ticket_intake_validates_and_shows_in_the_dependency_graph`, `Broken_intakes_are_refused_with_a_reason`, `Two_intakes_cannot_share_an_id`, `Templates_may_show_the_reply_and_only_people_inside_the_workspace_read_it`, `Rich_text_becomes_plain_text_for_email`); `TicketsReleaseTests` and `DesignTokenTests` for 1.9.0; architecture `Only_infrastructure_talks_to_mail_servers`.
  - Integration: `Mail.MailIntakeTests` (`A_message_opens_a_ticket_for_its_sender_and_replies_go_both_ways_without_internal_notes`, `Replies_reopen_resolved_tickets_closed_ones_start_a_follow_up_and_nobody_replies_into_someone_elses_ticket`, `Duplicates_automatic_mail_and_the_platforms_own_mail_never_make_tickets`, `Unverified_unknown_and_too_frequent_senders_wait_for_an_agent_to_accept_or_reject`, `Mailboxes_belong_to_one_workspace_and_only_people_with_the_right_configure_them`), `Api.MailApiTests.The_webhook_takes_a_message_once_with_the_mailbox_token_and_nothing_without_it`, `Isolation.DatabaseIsolationTests`.
  - Playwright: `mail.spec.ts` `@REQ-BIZ-003` (email opens a ticket, an unknown sender waits and is accepted on behalf of a member; axe in light and dark).
  - Not automated yet: the IMAP poller against a real IMAP server ([KI-054](../known-issues.md)).
