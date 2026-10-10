---
title: Zendesk ticketing (competitive research)
status: reference
owner: product
last_reviewed: 2026-10-10
requirements: [REQ-BIZ-001, REQ-BIZ-002, REQ-BIZ-003, REQ-BIZ-004, REQ-BIZ-005, REQ-OPS-004, REQ-WS-003, REQ-WS-004, REQ-NOT-001, REQ-NOT-003, REQ-NOT-004, REQ-ALR-001, REQ-TEN-004, REQ-CFG-002, REQ-AI-005]
---

# Zendesk ticketing (competitive research)

Research on Zendesk Support and the Zendesk Suite as of October 2026, for the Esmbler product team. It covers the data model, channels, workflow automation, agent experience, administration, reporting, AI, extensibility, pricing and known pain points. The last section, [What Esmbler should learn](#what-esmbler-should-learn), maps the findings onto the Esmbler backlog in [progress](../progress.md).

## How to read this document

- **Sources.** Primary sources are Zendesk's help center (support.zendesk.com), developer documentation (developer.zendesk.com), pricing pages and press releases. Claims that come only from third-party blogs, reviews or vendors are marked *(third-party)*. Zendesk renames features often and gates most of them by plan, so check a linked page before relying on a specific limit.
- **Plans.** Zendesk sells "Suites" (Team, Growth, Professional, Enterprise, Enterprise Plus) and cheaper "Support" plans. Since 2025 it also sells a Customer Service Suite and an Employee Service Suite. Plan gates are listed where they matter, because Zendesk uses them to tier features.
- **Esmbler baseline.** Esmbler already has tickets, requests, catalog items, approvals, SLAs with business calendars, events and an AI builder over MCP. See the [Tickets module architecture](../architecture/modules/tickets.md) and the [connected workspace addendum](../spec/connected-workspace.md).

## Executive summary

1. **Zendesk runs on a ticket, a flat set of fields, and tags.** Drop-down, multi-select and checkbox fields write tags. Triggers, automations, views, SLAs and reports key off those tags. This makes Zendesk flexible, but configuration turns into hard-to-follow tag logic.
2. **Statuses are fixed categories with custom labels.** Custom statuses map onto five system categories (New, Open, Pending, On-hold, Solved), plus Closed. SLAs, routing and reports use the category. This is the same idea as Esmbler's state categories.
3. **Email is still the backbone, and Zendesk's email handling is mature.** It threads replies with message-ID headers plus a hidden encoded ID, checks that only the requester or CCs can update a ticket, and holds a suspended-ticket queue with about 25 named reasons. CCs (visible, public replies only) are separate from followers (internal, see notes). Esmbler's G2-M5 should match this model.
4. **Automation is event triggers plus hourly automations.** Triggers run in order on every create and update, and can re-run each other. Automations run once an hour, change at most 1,000 tickets per run, need a "nullifying" condition, and are the only way to warn before an SLA breach. Notifications are mostly email templates inside triggers. Esmbler's durable timers and routed notifications are more precise and easier to manage.
5. **SLAs have seven metrics**, including next reply time, periodic and pausable update, requester wait and agent work time. Targets are keyed only on the system Priority field. Esmbler covers response and resolution today. It lacks the conversation-driven metrics.
6. **Configuration lifecycle has improved recently.** Zendesk now has snapshots, environment comparison, test deploys and revert between sandbox and production. But renaming a custom field during a deploy deletes the field and its data, and versioning is per account, not layered. Esmbler's typed overlays on stable IDs, together with module upgrades, remain a real advantage.
7. **Reporting (Explore) is separate and delayed.** Data syncs about hourly on Professional and Enterprise, and daily on Lite. Drill-in opens a panel of rows, not the agent's working queue. SLA reporting uses its own dataset, one row per target instance. Esmbler's plan in G2-M9, with shared metric definitions, live counts and drill-down into the explorer, answers a common complaint.
8. **AI is now the main product and the main pricing lever.** It includes AI agents, intelligent triage (topic, sentiment, language, entities), Copilot (summaries, writing tools, merge suggestions, auto assist with procedures), Admin Copilot, and an MCP client and server (early access in 2026). AI agents are billed per "verified" automated resolution: $1.50 committed, $2.00 pay-as-you-go.
9. **Admin Copilot is the closest competitor to Esmbler's AI builder.** It recommends and drafts changes to triggers, automations, SLAs and messaging triggers, and nothing applies without admin approval. It works one object at a time. It does not build connected forms, views, dashboards and permissions as one reviewable draft.
10. **The main complaints in reviews are cost, add-ons and plan gating, admin complexity, and Explore's learning curve.** G2 rates Zendesk 4.3/5 from more than 6,000 reviews. Praise centers on ease of use for agents, ticket handling and macros.

## 1. Data model

### Tickets

| Aspect | Zendesk behavior | Source |
| --- | --- | --- |
| Standard (system) fields | Subject, description, status, type (question, incident, problem, task), priority (low, normal, high, urgent), requester, assignee, group, tags, CCs, followers, brand, form. Some can be turned off; some cannot be edited | [About ticket fields](https://support.zendesk.com/hc/en-us/articles/4408886739098-About-ticket-fields) |
| Custom field types | Checkbox, credit card (only the last four digits are stored), date (`YYYY-MM-DD`), decimal, drop-down (up to 2,000 values), multi-line (65,536 characters), multi-select (up to 2,000 values), numeric, regex (pattern up to 255 characters), text, lookup relationship, and multi-lookup (ticket fields only, new in September 2026) | [About custom field types](https://support.zendesk.com/hc/en-us/articles/203661866-About-custom-field-types), [What's new September 2026](https://support.zendesk.com/hc/en-us/articles/11180657600026-What-s-new-in-Zendesk-September-2026) |
| Tags as glue | Drop-down, multi-select and checkbox values write tags, and a tag must not be reused across fields, or the fields lock. Business rules and views often key off tags | [About custom field types](https://support.zendesk.com/hc/en-us/articles/203661866-About-custom-field-types) |
| Ticket forms | Several forms per account. Each form is a field set for one request type, shown to agents and, optionally, to end users in the help center | [Managing your ticket forms](https://support.zendesk.com/hc/en-us/articles/4408836460698-Managing-your-ticket-forms) |
| Conditional fields | Defined per form, with separate conditions for agents and for end users. Used to show fields progressively and to make them required. Suite Growth and above, or Support Enterprise. Only some field types can be parents *(third-party: a limit of 1,500 conditions per form and user type)* | [Creating conditional ticket fields](https://support.zendesk.com/hc/en-us/articles/4408834799770-Creating-conditional-ticket-fields) |
| Lifecycle | Solved tickets close after 4 days by default (configurable from 1 hour to 28 days). Closed tickets are read-only. They are archived about 120 days after closing (sooner on very high-volume accounts), and archived tickets drop out of views and triggers but stay searchable. Deletion schedules exist for retention | [Why solved tickets close](https://support.zendesk.com/hc/en-us/articles/4408883475354-Why-do-solved-tickets-change-to-a-closed-status), [About ticket archiving](https://support.zendesk.com/hc/en-us/articles/4408887617050-About-ticket-archiving), [Ticket deletion schedules](https://support.zendesk.com/hc/en-us/articles/6062884435866-Creating-ticket-deletion-schedules-for-data-retention-policies) |

### Statuses and status categories

- Every custom status belongs to one of the fixed categories: New, Open, Pending, On-hold or Solved. The category is set when the status is created and cannot be changed later. No custom status can go in New or Closed ([Creating custom ticket statuses](https://support.zendesk.com/hc/en-us/articles/4412575861018-Creating-custom-ticket-statuses), [Custom ticket statuses API](https://developer.zendesk.com/api-reference/ticketing/tickets/custom_ticket_statuses/)).
- A status can have a separate name and description for end users, so "Waiting on vendor" can appear as "In progress" to the requester. On-hold is internal, and requesters see it as Open.
- Statuses can be limited to specific ticket forms. SLA pause rules, merging and reporting use the category, not the label. Custom statuses are on every plan.
- **Esmbler comparison:** Esmbler's state categories (`new`, `active`, `pending`, `resolved`, `closed`, `cancelled`) do the same job ([Tickets module](../architecture/modules/tickets.md#states-and-transitions)). Esmbler's statuses come from a workflow with role-gated transitions and required fields, which is stricter than Zendesk's free-form status drop-down. Zendesk has no "cancelled" category and no transition model.

### Users, organizations, groups, brands

- **Users** are end users, agents, admins, light agents (can view tickets and add internal notes, but cannot be assigned tickets or reply publicly) and contributors. Each has custom user fields ([Custom roles API role types](https://developer.zendesk.com/api-reference/ticketing/account-configuration/custom_roles/)).
- **Organizations** have custom fields, a default group for routing, and `shared_tickets` and `shared_comments` flags. With these flags, members of an organization can see and comment on each other's requests in the help center ([Organizations API](https://developer.zendesk.com/api-reference/ticketing/organizations/organizations/)).
- **Groups** are agent teams, and an agent can be in several. Ticket access is set by role: all tickets, the agent's groups, the agent's organization, or assigned only.
- **Brands** (multi-brand) give separate help centers, support addresses and forms within one account.

### Linked work: side conversations, problem and incident, merging

| Feature | Behavior | Source |
| --- | --- | --- |
| Side conversations | Threads started from a ticket by email, Slack, Microsoft Teams, or as a child ticket assigned to another group. Each is tracked on the parent ticket and can be started from a macro. Suite Professional and above | [Creating side conversations](https://support.zendesk.com/hc/en-us/articles/4604286879642-Creating-side-conversations) |
| Problem and incident | The ticket type is set to Problem, and incidents link to it. Solving the problem solves every linked incident and copies the solving comment to them. Reopening the problem does not reopen its incidents. AI agent tickets cannot be linked. An incident links to only one problem (a long-standing community request) | [Working with problem and incident tickets](https://support.zendesk.com/hc/en-us/articles/4408835103898), [Community request](https://support.zendesk.com/hc/en-us/community/posts/4419974852506-Link-Incident-to-Multiple-Problem-reports) |
| Merging | Both tickets must be in a category below Solved, although an unsolved ticket can be merged into a solved one. The source ticket closes with the tag `closed_by_merge`. Only the latest public comment is carried over, and the agent can edit it and make it public or internal. The source's requester and CCs become CCs on the target. **A merge cannot be undone.** AI-agent tickets and shared tickets cannot be merged | [Merging tickets](https://support.zendesk.com/hc/en-us/articles/4408882445594) |

## 2. Channels and intake

### Email

- **Connection.** Mail is forwarded from an external address, or connected through the Gmail or Exchange connectors. Zendesk sends outbound mail from an external address only after that address is added and passes forwarding verification ([Connect your support email](https://support.zendesk.com/hc/en-us/articles/9104802071578-Zendesk-Suite-trial-Connect-your-support-email)).
- **Threading.** Incoming mail is matched to a ticket by message-ID headers (`In-Reply-To` and `References` must be unique per ticket). Zendesk also adds a hidden encoded ID of about 11 characters to outgoing mail, so threading works even when headers are stripped. Threading data for headers is kept for 30 days ([Email threading article](https://support.zendesk.com/hc/en-us/articles/115004690668), [community thread on the hidden ID](https://support.zendesk.com/hc/en-us/community/posts/5518983887130-White-hidden-text-string-at-the-end-of-public-reply-to-new-ticket)). Quoted history is trimmed at a "reply above this line" marker.
- **Who may update a ticket.** Only the requester or a CC can update a ticket by email. Mail from anyone else is suspended or added as a flagged comment. By default, replies from end-user CCs are made **private** ([Configuring CCs and followers](https://support.zendesk.com/hc/en-us/articles/4408843795482)).
- **CCs and followers.** A ticket can have up to 48 email CCs. CCs can be end users or agents, are visible to everyone on the thread, and receive public comments. Followers are internal only, unlimited, and invisible to end users, and they receive internal notes too. Admins can block specific addresses or domains from being CCs ([Configuring CCs and followers](https://support.zendesk.com/hc/en-us/articles/4408843795482)).
- **Suspended tickets** are a review queue with named reasons. The reasons include: anonymous or unverified sender, automated mail (`Auto-Submitted`, `Precedence`, `X-Autoreply`), out-of-office, an email loop (many messages from one sender in a short time puts that sender on the blocklist for an hour), mail from the account's own support address, a no-reply or system sender (mailer-daemon, postmaster), failed DMARC, malware or malicious patterns, an oversized message, a missing ticket token on an update, a blocklisted sender or a sender outside the allowlist, and registration required. Suspended tickets are deleted automatically after 14 days, and mail with a spam score of 99% or more is rejected outright ([Causes for ticket suspension](https://support.zendesk.com/hc/en-us/articles/4408828416282-Causes-for-ticket-suspension), [Understanding suspended tickets and spam](https://support.zendesk.com/hc/en-us/articles/4408889141146-Understanding-suspended-tickets-and-spam)).
- Zendesk does not support bulk ticket creation by email. Bulk creation goes through the API or the Channels framework.

### Web form, help center and service catalog

- End users submit requests through the help center using ticket forms and conditional fields, and track them under "My requests". Members of shared organizations also see their organization's requests.
- **Service catalog** (generally available September 2025, Suite Growth and above). Admins define services and assets. Each catalog item gets an automatically generated request form that cannot be edited, and submitting it creates a linked ticket. Since September 2026, agents can convert a ticket from any channel into a service request by setting its Service field ([Service catalog GA](https://support.zendesk.com/hc/en-us/articles/9544621220762-Announcing-the-general-availability-of-the-service-catalog), [Adding services](https://support.zendesk.com/hc/en-us/articles/8478940252698-Adding-services-to-your-service-catalog)).

### Messaging, chat, voice and API

- **Messaging** is persistent and asynchronous. It covers the web widget, mobile SDKs, WhatsApp, Facebook Messenger, Instagram, LINE and others, and is built on Sunshine Conversations. It replaces session-based live chat ([Why migrate from live chat to messaging](https://support.zendesk.com/hc/en-us/articles/4408834108186-Why-migrating-from-live-chat-to-messaging-is-the-right-choice)).
- **Voice.** Zendesk Talk turns calls into tickets with recordings, IVR, and overflow and after-hours routing. **Zendesk Contact Center** is an add-on built on Amazon Connect, with transcription and sentiment. Voice AI agents support more than 60 languages ([About Zendesk Contact Center](https://support.zendesk.com/hc/en-us/articles/9459059241626-About-Zendesk-Contact-Center), [Relate 2026 press release](https://www.zendesk.com/newsroom/press-releases/relate-2026/)).
- **API and Channels framework** support programmatic creation and bulk import (see [Extensibility](#8-extensibility)).

### Public replies and internal notes

- Every comment is public or internal. Agents switch between "Public reply" and "Internal note" in the composer. Light agents can add only internal notes. Followers receive internal notes by email; CCs never do.
- Customers do not receive messaging notifications when a problem ticket solves its incidents. Messaging tickets have channel-specific rules, which shows how hard it is to keep public and internal visibility consistent across channels.
- **Esmbler comparison:** REQ-BIZ-002 already separates public replies from work notes with visibility checks. G2-M5 must keep internal notes out of outbound mail, and must default inbound replies from non-requesters to private, as Zendesk does.

## 3. Workflow automation

### Triggers, automations, macros and views

| Mechanism | How it works | Limits and gotchas | Source |
| --- | --- | --- | --- |
| Ticket triggers | Event-based. On every create or update, triggers are checked in order. Conditions combine "all" and "any" groups, with actions to set fields, notify, call webhooks or add tags. One trigger's actions can make later triggers fire. Triggers are grouped into categories. Object triggers (for custom objects) and messaging triggers also exist | Each trigger must be under 65 KB. Triggers do not run on closed tickets. Order matters. *(third-party)* **Triggers cannot use SLA conditions**; only automations can | [About triggers](https://support.zendesk.com/hc/en-us/articles/4408822236058-About-Zendesk-triggers-and-how-they-work), [Triggers API](https://developer.zendesk.com/api-reference/ticketing/business-rules/triggers/) |
| Automations | Time-based. They run **about once an hour, at a random point in the hour**, against all non-closed tickets. Conditions use whole hours ("Hours since …", "Hours until next SLA breach") | 1,000 tickets per automation per run. A ticket can be updated by automations at most 100 times. 500 active automations. Each automation needs a nullifying condition, or it loops | [About automations](https://support.zendesk.com/hc/en-us/articles/4408832701850) |
| Macros | Manual bundles of actions without conditions: comment text, field changes, tags, followers, assignee, attachments, and starting a side conversation. They are personal or shared, and contextual workspaces suggest the relevant ones | At most 5,000 shared macros | [Creating macros](https://support.zendesk.com/hc/en-us/articles/4408844187034-Creating-macros-for-tickets) |
| Views | Saved ticket queries with counts. Personal views are capped at 10 and shared views at 100 (raised from 30 in October 2024), and views can be grouped into folders. **Guided mode** (Enterprise) removes the ticket list, so agents press "Play" to get the next ticket in the view's sort order | Admins complain about the caps, and views lack a revision history | [Categorizing views](https://support.zendesk.com/hc/en-us/articles/8043835835674-Announcing-the-ability-to-categorize-views), [Setting up Guided mode](https://support.zendesk.com/hc/en-us/articles/4408825479066-Setting-up-Guided-mode), [Views limitation thread](https://support.zendesk.com/hc/en-us/community/posts/4409217537050) |

Notifications in Zendesk are mostly **trigger actions that send email** using placeholder templates, such as "Notify requester of comment update" or "Notify assignee of assignment". A typical account has dozens of notification triggers next to its routing triggers, and nobody dares to delete "Trigger 14" *(third-party)*.

### SLA policies

The source is [Defining SLA policies](https://support.zendesk.com/hc/en-us/articles/4408829459866-Defining-SLA-policies). SLAs require Suite Growth or above, or Support Professional or above.

| Metric | Starts | Pauses | Stops |
| --- | --- | --- | --- |
| First reply time | Ticket created | — | First public agent comment (not measured if an agent creates the ticket with a public comment) |
| Next reply time | Oldest unanswered end-user comment | — | Next public agent comment |
| Periodic update | Each public agent comment | — | Next public agent comment |
| Pausable update | Each public agent comment | Pending | Next public agent comment |
| Requester wait time | Created | Pending (and Solved) | Solved |
| Agent work time | Created | On-hold, Pending | Solved |
| Total resolution time | Created | Solved only | Solved |

- Policies are an ordered list, and the first matching policy applies. Conditions can use any standard or custom field, but **targets are keyed only on the system Priority field**. A ticket without a priority gets no SLA. The smallest target is 15 seconds. Each target can use calendar hours or business hours.
- When a ticket reopens, requester wait and agent work time resume where they stopped. Reply metrics start new targets when the customer comments.
- **Schedules** have a time zone, weekly hours and holidays, and are on Professional and above. **Multiple schedules** are Enterprise only, and a "Set schedule" trigger action selects one. Triggers and automations get "Within business hours?" and "On a holiday?" conditions ([Setting your schedule](https://support.zendesk.com/hc/en-us/articles/4408842938522-Setting-your-schedule-with-business-hours-and-holidays)).
- **Pre-breach warnings** exist only as hourly automations. An automation with "Hours until next SLA breach less than 2" may fire anywhere from about one to two hours before the breach. Zendesk says there is "no native method" to notify at the moment of breach ([Alerting your team to tickets nearing breach](https://support.zendesk.com/hc/en-us/articles/4408820131994-Workflow-How-to-alert-your-team-to-tickets-nearing-an-SLA-breach)).
- **Group SLAs** set internal team targets, for example time in a group, separately from customer SLAs.

### Routing

The source is [Omnichannel routing](https://support.zendesk.com/hc/en-us/articles/4828787357210-Activating-and-configuring-omnichannel-routing) unless noted.

- **Order of work.** Work is ordered by when it became eligible for routing. Professional and above can order by priority, and Team and above can order by nearness to SLA breach.
- **Choosing the agent.** Work goes to the agent with the most spare capacity (as a number or a percentage), or by round robin to the agent who has waited longest. The agent must have an eligible unified status (custom statuses on Professional and above).
- **Capacity rules** set limits per channel, for example 1 call, 3 messaging conversations and 5 emails. Focus mode limits an agent to one real-time channel at a time.
- **Skills** (Professional and above) are required or optional. Optional skills time out (1 hour for email, 30 seconds for messaging and calls by default).
- **Custom queues** (Professional and above) send tickets to primary groups with secondary groups as fallback, and take priority over the standard queue. Enterprise can split a queue by percentage into subqueues, for example for outsourcing partners ([Custom queues](https://support.zendesk.com/hc/en-us/articles/6866051942042-Announcing-custom-omnichannel-routing-queues), [Percentage-based routing](https://support.zendesk.com/hc/en-us/articles/9050695734426-Announcing-percentage-based-routing-within-omnichannel-routing)).
- **Reassignment** happens when a reopened ticket's agent is away, and when an agent's status changes (up to 50 open tickets, after one minute). Unaccepted messages are reassigned after a time limit.
- **Predictive routing** (announced June 2026) assigns each ticket to the agent predicted to resolve it fastest ([Predictive routing](https://support.zendesk.com/hc/en-us/articles/10835440994586-Announcing-AI-based-predictive-routing-for-omnichannel-routing)).

### Approvals

The source is [Understanding approvals](https://support.zendesk.com/hc/en-us/articles/8481179038490-Understanding-approvals-and-how-they-work), with approvals generally available since July 2025.

- Approvals are available on Customer Service Suite Professional and above, and on Employee Service Suite Growth and above.
- An agent starts an approval request from a ticket for up to five approvers. Approvers can be agents, groups of up to 30 members, or end users. Several approvers form a parallel block in which all must approve, and one denial denies the request. **Sequential approval means creating requests one after another**, and for a group only the first response counts.
- Agents respond in Agent Workspace or Slack; end users respond in the help center. A ticket cannot be closed while an approval is pending. An "Approval status" field is created automatically for reporting. Approval requests can also be created by the API or by action flows.
- **Esmbler comparison:** Esmbler's G2-M3 approvals already go further, with policy-driven sequential and parallel stages, delegation, expiry and escalation, and separation of duties ([ADR 0016](../adr/0016-approvals.md)).

## 4. Agent workspace

| Capability | What it does | Plan | Source |
| --- | --- | --- | --- |
| Agent Workspace | One ticket interface for all channels. Tickets open in tabs. Ticket properties are on the left, the conversation is in the center, and a context panel on the right shows the customer, their other tickets, knowledge, apps and AI. Agents switch channels inside the composer | All | [Layout builder](https://support.zendesk.com/hc/en-us/articles/5447690090138-About-custom-layouts-with-layout-builder) |
| Layout builder | Admins arrange the components on a ticket page | Professional and above | [Layout builder](https://support.zendesk.com/hc/en-us/articles/5447690090138-About-custom-layouts-with-layout-builder) |
| Contextual workspaces | Up to 500 workspaces, each with up to 10 conditions. A workspace chooses the form, suggested macros, app order and layout for each kind of ticket. These are recommendations, not restrictions | Enterprise | [Setting up contextual workspaces](https://support.zendesk.com/hc/en-us/articles/4408833498906-Setting-up-contextual-workspaces) |
| Keyboard shortcuts | A shortcut sheet is available from the profile menu. In 2025 Zendesk added shortcuts to accept a messaging conversation (Ctrl+Alt+Q), end it (Ctrl+Alt+E) and switch tabs (Ctrl+Shift+1–9) | All | [New keyboard shortcuts](https://support.zendesk.com/hc/en-us/articles/9285444325914-Announcing-new-keyboard-shortcuts-in-Agent-Workspace) |
| Notifications | A bell tray that keeps a history of read items and has an unread filter (2026). Alerts for assignment, replies, @mentions and reopened tickets. A favicon badge and sound for new work. Each agent controls their own settings under Admin Center, Workspaces, Notifications | All | [Redesigned notifications](https://support.zendesk.com/hc/en-us/articles/9615994942234-Announcing-redesigned-messaging-notifications), [Notification improvements](https://support.zendesk.com/hc/en-us/articles/11069213656090-Announcing-messaging-notification-improvements-in-the-Agent-Workspace) |
| Small helpers (2026) | Links to a specific comment, audio replies, hiding and reordering agent statuses, and custom fields in search columns and filters | Various | [What's new September 2026](https://support.zendesk.com/hc/en-us/articles/11180657600026-What-s-new-in-Zendesk-September-2026) |

**What makes agents fast:**

- Macros applied in one keystroke, with suggested macros.
- Views with live counts, and Play mode to take the next ticket without choosing.
- The composer and channel switching on the same screen as the ticket.
- Collision detection, which shows who else is viewing the ticket.
- The customer's other tickets in the context panel.
- AI summaries and writing tools in the composer.

Reviewers consistently praise how easy it is to learn as an agent. Their complaints are about admins, not agents ([G2 review summary](https://learn.g2.com/zendesk-review)).

## 5. Administration and configuration

- **Admin Center** is one console for channels, objects and rules, workspaces, people, apps and integrations, and account settings. Configuration is per account. Zendesk has no module or package layering: the default triggers and automations are copied into the account and then belong to the customer.
- **Roles.** The built-in roles are end user, agent, admin, light agent and contributor. **Custom agent roles are Enterprise only.** Enterprise also includes predefined roles (Advisor, Staff, Team lead), and roles control access to tickets, comments, business rules and views ([Custom agent roles](https://support.zendesk.com/hc/en-us/articles/4408882153882-Custom-agent-roles-Enterprise-)). Light agents need Growth or above.
- **Audit log** (Admin Center, Account, Logs) records changes to configuration, can be filtered by item type and exported as CSV ([Viewing the audit log](https://support.zendesk.com/hc/en-us/articles/4408828001434-Viewing-the-audit-log-for-changes)). It has gaps: community posts report that view changes are not fully audited and views have no revision history, and that messaging trigger edits did not record who made them ([community thread on views auditing](https://support.zendesk.com/hc/en-us/community/posts/6256415529626-Zendesk-Support-Audit-logs-Improve-auditing-changes-to-Views)).
- **Sandboxes.**
  - A *standard* sandbox copies settings only. A *premium* sandbox copies configuration, and a partial premium sandbox also copies up to 10,000 non-closed tickets.
  - Replication is a one-time snapshot. Targets and webhooks are copied inactive, and some security settings are not copied ([About sandbox environments](https://support.zendesk.com/hc/en-us/articles/6150628316058-About-Zendesk-sandbox-environments), [Premium sandbox](https://support.zendesk.com/hc/en-us/articles/4408822049818-Creating-a-premium-sandbox-with-data-replication)).
  - Support Enterprise includes one standard sandbox. Suite Enterprise adds a partial premium sandbox, and Enterprise Plus a full premium sandbox.
- **Change management** (Suite Growth and above):
  - **Snapshots** (read-only), **inspect and compare** across environments, and **deployments** that check dependencies, run a test deploy, deploy, and **revert** to the snapshot of the destination taken before the deploy ([Deploying configurations](https://support.zendesk.com/hc/en-us/articles/9474556880922-Deploying-account-configurations-from-one-environment-to-another), [Inspecting configurations](https://support.zendesk.com/hc/en-us/articles/8514047212442-Inspecting-sandbox-and-production-account-configurations)).
  - Known risks: **renaming a custom field in a deployment deletes the field and its data and creates a new one**, a locale mismatch makes items look new or deleted, and private apps cannot be deployed.
  - Since September 2026, AI configurations (Copilot, intelligent triage) can be versioned and deployed in the same way.
  - Before this existed, teams used Git-based tools such as Salto for history, diff and rollback ([Salto lifecycle guide](https://www.salto.io/blog-posts/how-to-manage-full-application-lifecycle-in-zendesk)).

## 6. Reporting

- **Explore** is a separate analytics product, with datasets for Tickets, Updates history, SLAs, Satisfaction, Backlog, Talk, Chat and messaging, and AI agents. Metrics and attributes include ([Metrics and attributes for Support](https://support.zendesk.com/hc/en-us/articles/4408827693594-Metrics-and-attributes-for-Zendesk-Support)):

  | Metric | Definition |
  | --- | --- |
  | First reply time | Creation to first public agent reply, as median and average, in calendar and business hours |
  | Full resolution time | Creation to final solve |
  | First resolution time | Creation to first solve |
  | Requester wait time, agent work time, on-hold time | Time in the matching status categories |
  | One-touch tickets | Solved with one agent reply or none, and the percentage of solved tickets |
  | Two-touch and multi-touch tickets | Solved with two or more agent replies |
  | Reopens | Reopened tickets as a percentage of solved tickets |
  | Agent replies, assignee stations, group stations | Count of agent replies, and how many assignees and groups a ticket passed through |
  | Created, solved, unsolved, backlog | Ticket counts by date, with backlog from the Backlog dataset |
  | SLA | From the SLAs dataset, with **one row per SLA target instance** that is Achieved, Breached or Active. Counted as "% achieved" either per target or per ticket, where any breach makes the ticket breached ([SLA recipe](https://support.zendesk.com/hc/en-us/articles/4971706120730)) |
  | Satisfaction (CSAT) | Satisfaction score from surveys |

- **Freshness.** Explore Professional and Enterprise sync about hourly; each sync starts an hour after the previous one ends and can take longer on large accounts. **Explore Lite syncs daily.** If no report has run for 30 days, refresh drops to weekly. Live dashboards update in 5–10 seconds but cover only a few metrics. They are being replaced by real-time monitoring dashboards for accounts that first opened Explore after 5 May 2026 ([Data sync frequency](https://support.zendesk.com/hc/en-us/articles/4408820703386-How-frequently-does-my-Zendesk-data-sync-to-Explore), [Data refresh intervals](https://support.zendesk.com/hc/en-us/articles/4408823242778-Data-refresh-intervals-for-Explore-reporting)).
- **Drill-in.** A report author turns on drill-in and picks up to 20 attributes. Clicking a figure then opens a **panel of rows** that can be exported to CSV or Excel. Metric filters do not apply to drill-in, so the rows can differ from the figure, and community threads report mismatches ([Using drill-in](https://support.zendesk.com/hc/en-us/articles/4408826499482-Using-drill-in-to-refine-your-reports)). Drill-in does not open an agent view that the agent can work from.
- **AI analytics.** Advanced Insights (from the HyperArc acquisition), Analyst Copilot and agentic analytics are in early access in 2026 ([Relate 2026 announcements](https://www.zendesk.com/blog/zendesk-insights/innovation/relate-2026--evolving-the-resolution-platform-for-the-autonomous/)).

## 7. AI features (2025–2026)

| Capability | What it does | Availability and pricing | Source |
| --- | --- | --- | --- |
| AI agents | Autonomous agents for messaging, email and voice, running "procedures" and actions. Specialized Industry Agents (commerce first) and Custom Agents built in Agent Builder (September 2026). They can also run inside Salesforce and ServiceNow | Billed per automated resolution | [Specialized AI Agents](https://www.zendesk.com/newsroom/press-releases/zendesk-introduces-specialized-ai-agents-purpose-built-for-your-business/) |
| Automated resolution tiers | Since 18 May 2026 each AI conversation is classified as an *assisted escalation*, a *contained resolution* or a *verified resolution*. Only verified resolutions are billed: the customer did not seek more help, **and an LLM check confirms the issue was resolved**. The conversation ends 72 hours after the first email, 2 hours after the first message (extendable), or at hang-up on calls | $1.50 committed, $2.00 pay-as-you-go. Each plan includes some per agent | [Automated resolution tiers](https://support.zendesk.com/hc/en-us/articles/9570369117338-About-automated-resolution-tiers), [Pricing](https://www.zendesk.com/pricing/) |
| Intelligent triage | Predicts topic (called "intent" before June 2026), sentiment (very negative to very positive), language (about 150) and custom entities, each with a confidence value. Predictions are written to ticket fields from the subject and first comment, and can be updated on each reply. Covers email, web form, API, messaging and call transcripts | Classifications on Professional and above. **Using them in triggers needs the Copilot add-on** | [Intelligent triage](https://support.zendesk.com/hc/en-us/articles/4550640560538-Automatically-detecting-customer-intent-sentiment-and-language) |
| Copilot features included in plans | Writing tools (enhance, expand), ticket summaries, merge suggestions | 5 uses per agent per month, pooled, with a cap of 500. Features pause when the allowance runs out | [Copilot usage allowance](https://support.zendesk.com/hc/en-us/articles/10269211633050-Managing-your-Copilot-AI-features-and-usage-allowance) |
| Copilot add-on | Auto assist, which suggests replies and **actions** from procedures, help center articles and similar tickets; the agent approves them, and admins can pre-approve some actions. Also: tone rewriting, summaries that include internal notes, triage in workflows, workflow recommendations, and knowledge and similar tickets in the context panel | $50 per agent per month | [About auto assist](https://support.zendesk.com/hc/en-us/articles/9945148867866-About-auto-assist), [About Zendesk Copilot](https://support.zendesk.com/hc/en-us/articles/5524125586330-About-Zendesk-Copilot) |
| Admin Copilot | Weekly insights. More than 70 kinds of recommendations, such as deactivating unused triggers or automating a common topic. Conversational help for admins. Drafts changes to triggers, automations, SLAs, messaging triggers and action flows. **Nothing applies until an admin approves it**, and changes are visible in the audit log | Generally available since May 2026. Building action flows from a description was in early access in August–September 2026 | [Admin Copilot](https://www.zendesk.com/service/ai/admin-copilot/), [AI recommendations](https://support.zendesk.com/hc/en-us/articles/9598690362010-Reviewing-and-implementing-AI-recommendations-to-automate-tasks-Beta), [Action flow creation EAP](https://support.zendesk.com/hc/en-us/articles/11084848330394-Announcing-the-EAP-for-AI-powered-action-flow-creation-through-Admin-Copilot) |
| Knowledge | Knowledge Builder drafts articles from past tickets. Knowledge connectors read Confluence, SharePoint, Google Drive, Notion and others. Knowledge Copilot (early access) finds gaps in content | Mixed | [AI Summit October 2025](https://www.zendesk.com/newsroom/articles/zendesk-unveils-powerful-new-ai-capabilities-within-the-resolution-platform-to-accelerate-service-at-scale) |
| Quality | Quality Score rates 100% of human and AI interactions. Zendesk QA is an add-on | Early access soon (2026) | [Relate 2026 press release](https://www.zendesk.com/newsroom/press-releases/relate-2026/) |
| MCP | An MCP **client**, so agents and Copilot can call external tools (early access), and an MCP **server** exposing tickets, knowledge and customer data to outside AI (early access from summer 2026). Zendesk also offers 40 workflow connectors | Early access | [Relate 2026 press release](https://www.zendesk.com/newsroom/press-releases/relate-2026/) |
| Employee Service AI | Internal agents in Slack and Teams that respect permissions at the source (from the Unleash acquisition), IT asset management, and a bundled ITSM offering | Early access in summer 2026 | [Relate 2026 press release](https://www.zendesk.com/newsroom/press-releases/relate-2026/) |

## 8. Extensibility

- **Apps framework (ZAF).** Apps are iframes declared in `manifest.json` at fixed places such as `ticket_sidebar`, `new_ticket_sidebar`, `nav_bar` and the top bar. The ZAF SDK provides `client.get`, `client.set`, `client.invoke` and event hooks, and only apps placed on the ticket page can read or write the ticket. ZCLI builds and serves apps ([App quick start](https://developer.zendesk.com/documentation/apps/getting-started/zendesk-app-quick-start/), [Server-side apps](https://developer.zendesk.com/documentation/apps/build-an-app/building-a-server-side-app/part-1-core-concepts/)). App Builder (generally available October 2025) builds apps without code.
- **Webhooks.**
  - Webhooks are called from triggers and automations, or subscribe to events.
  - Each request is signed with HMAC-SHA256 over the timestamp and body, in the `x-zendesk-webhook-signature` header.
  - Zendesk retries some failures and has a circuit breaker for broken endpoints. Invocation logs can be inspected ([Webhooks API](https://developer.zendesk.com/api-reference/webhooks/introduction/)).
  - *(third-party)* The timeout is 10 seconds, with retries on 429 and 503. The circuit breaker trips at a 70% error rate or 1,000 errors in 5 minutes.
- **API rate limits.** Requests per minute are 200 on Team, 400 on Growth and Professional, 700 on Enterprise, and 2,500 on Enterprise Plus. The High Volume add-on raises Growth and above to 2,500 (minimum 10 seats). Support and Help Center limits are counted separately, and some endpoints have lower limits ([Rate limits](https://developer.zendesk.com/api-reference/introduction/rate-limits/)).
- **Custom objects.**
  - Allowed per account: 3 on Team, 5 on Growth, 30 on Professional, 50 on Enterprise.
  - Each object can have 100 fields, 5 lookup fields (10 on Professional and above), and records of up to 32 KB.
  - Object triggers run on custom objects. In 2026 custom objects gained roll-up summary, currency and parent-child fields ([Custom data API](https://developer.zendesk.com/api-reference/custom-data/introduction), [About custom field types](https://support.zendesk.com/hc/en-us/articles/203661866-About-custom-field-types)).
- **Action Builder and action flows.** No-code workflows across systems, with connectors to Slack, Jira, Okta, Shopify, OpenAI, Claude and others. Extra actions are bought as action credits, at $200 per 100,000 actions ([Pricing](https://www.zendesk.com/pricing/)).
- **Marketplace.** A large catalog of public apps, free and paid. Reviewers say important features such as advanced approvals, conditional logic and reporting often need paid marketplace apps ([G2 Employee Service reviews](https://www.g2.com/products/zendesk-for-employee-service/reviews)).

## 9. Pricing and pain points

### Pricing (US list, per agent per month)

| Plan | Annual billing | Monthly billing | Notable gates |
| --- | --- | --- | --- |
| Support Team | $19 | $25 | Basic ticketing, triggers. No SLAs |
| Suite Team | $55 | $69 | Messaging, voice, help center, omnichannel routing basics. 5 automated resolutions per agent |
| Suite Growth | $89 | $115 | SLAs, conditional fields, light agents, service catalog (Employee Service). Listed on regional and Employee Service pages |
| Suite Professional | $115 | $149 | Skills routing, custom queues, side conversations, layout builder. 10 automated resolutions per agent. Admin Copilot included |
| Suite Enterprise and Enterprise Plus | Quote only (previously about $169) | — | Custom roles, contextual workspaces, multiple schedules, Guided mode, premium sandbox, live dashboards |

Add-ons include Copilot at $50, the workforce engagement bundle at $50, Contact Center at $83, QA at about $35, and advanced data privacy at about $50. Sources: [Zendesk pricing](https://www.zendesk.com/pricing/) and [Employee Service pricing (regional)](https://zendesk.com.mx/pricing/employee-service-pricing). *(third-party)* All-in costs for most teams are estimated at $150–250 per agent per month ([eesel review](https://www.eesel.ai/blog/zendesk-review)).

### Known pain points

| Pain point | Evidence |
| --- | --- |
| Costs grow with add-ons and plan gates; core governance (custom roles, sandboxes, multiple schedules) is Enterprise only | [G2 review summary](https://learn.g2.com/zendesk-review), [G2 Employee Service reviews](https://www.g2.com/products/zendesk-for-employee-service/reviews), plan gates above |
| Admin complexity: trigger order, tag logic and notification triggers grow over time. Nobody knows what an old rule does, so nobody removes it | [G2 review summary](https://learn.g2.com/zendesk-review), [community feedback on triggers](https://support.zendesk.com/hc/en-us/community/posts/5381239312922-Triggers-Automations-We-want-your-feedback) |
| Explore has a steep learning curve; custom reports need higher tiers; data is delayed by hours | [G2 Employee Service reviews](https://www.g2.com/products/zendesk-for-employee-service/reviews), [Data sync frequency](https://support.zendesk.com/hc/en-us/articles/4408820703386-How-frequently-does-my-Zendesk-data-sync-to-Explore) |
| Caps on views; no revision history for views | [Views limitation thread](https://support.zendesk.com/hc/en-us/community/posts/4409217537050) |
| Hourly automations: no precise pre-breach alerts, 1,000 tickets per run | [About automations](https://support.zendesk.com/hc/en-us/articles/4408832701850), [SLA alert workflow](https://support.zendesk.com/hc/en-us/articles/4408820131994-Workflow-How-to-alert-your-team-to-tickets-nearing-an-SLA-breach) |
| Merges are permanent; an incident links to only one problem; problem/incident tickets are not a real problem-management module | [Merging tickets](https://support.zendesk.com/hc/en-us/articles/4408882445594), [community request](https://support.zendesk.com/hc/en-us/community/posts/4419974852506-Link-Incident-to-Multiple-Problem-reports) |
| AI billing is hard to predict, and customers distrust what counts as a "resolution" *(third-party, citing Reddit)* | [Why companies leave Zendesk](https://delight.ai/blog/industry/why-are-companies-leaving-zendesk), [Automated resolution tiers](https://support.zendesk.com/hc/en-us/articles/9570369117338-About-automated-resolution-tiers) |
| Approvals are basic: up to 5 approvers, parallel only, sequential approval means creating requests by hand | [Understanding approvals](https://support.zendesk.com/hc/en-us/articles/8481179038490-Understanding-approvals-and-how-they-work) |

## What Esmbler should learn

### Where Esmbler already stands

| Area | Zendesk | Esmbler today | Assessment |
| --- | --- | --- | --- |
| Status model | Custom labels on fixed categories, with a separate label for end users | Workflow states with categories, role-gated transitions and required fields | Esmbler is stronger. Add **requester-facing labels** for states |
| SLAs | 7 metrics, keyed on system priority, warnings only through hourly automations | Response and resolution, expression conditions, pause and resume, DST-correct calendars, precise warning and breach events ([ADR 0018](../adr/0018-sla-and-business-hours.md)) | Esmbler is stronger on timers. It lacks **next reply time** and **periodic update** |
| Approvals | Ad hoc, at most 5 approvers, parallel only | Policy-driven stages, delegation, expiry, separation of duties ([ADR 0016](../adr/0016-approvals.md)) | Esmbler is stronger |
| Configuration lifecycle | Snapshots, compare, deploy and revert. A field rename loses data. Flat per-account configuration | Typed overlays on stable IDs, content-hashed artifacts, module upgrades that keep tenant changes ([REQ-CFG-002](../spec/traceability.md)) | Esmbler is structurally stronger. G2-M10 must reach Zendesk's level of deploy and revert UX |
| AI for admins | Admin Copilot drafts single objects for approval | One reviewable draft across fields, layout, rules and workflow, with persona preview ([connected workspace](../spec/connected-workspace.md)) | Esmbler's core differentiator. Extend it to dashboards and explorer in G2-M9 and G2-M9b |
| Email | Mature threading, CCs and followers, suspended queue | Not yet (G2-M5) | The largest gap to close |
| Agent speed tools | Macros, views with counts, Play mode, collision, context panel, AI summaries | Saved views, record page, activity | Gap: **macros**, **next-ticket mode**, **collision** |
| Reporting | Explore: separate, delayed, drill-in shows rows | Planned in G2-M9 | Opportunity: live, consistent, drill into the explorer |

### Prioritized features worth matching

P0 means Esmbler needs it for the pilot, P1 for the Gate 2 exit, and P2 later.

| # | Priority | Feature | Esmbler milestone | Notes for the design |
| --- | --- | --- | --- | --- |
| 1 | P0 | **Email threading** by `Message-ID`, `In-Reply-To` and `References`, with a fallback opaque token in the body or subject. Trim quoted history but keep the original message for audit | G2-M5 (REQ-BIZ-003) | Make the token per ticket and unguessable, so it cannot be used to update other tickets. Bind header data for a fixed retention period. Record a threading decision on every inbound message, so an agent can see why it went where it did |
| 2 | P0 | **Sender policy and suspended queue**: only the requester, CCs and known agents can update a ticket; others go to a review queue with **enumerated reason codes** (unknown sender, unverified, domain not allowed, DMARC fail, auto-submitted, out-of-office, loop, own address, no-reply, malware, oversize, missing token); recover or reject; automatic expiry | G2-M5 | Reason codes should be a choice list, so rules, dashboards and the AI can use them. Default replies from non-requesters to **internal**, as Zendesk does with CC replies |
| 3 | P0 | **Loop prevention**: honor RFC 3834 `Auto-Submitted` and `Precedence`, rate-limit per sender, refuse mail from the tenant's own addresses, never auto-reply to auto-replies | G2-M5 (REQ-EVT-007) | Zendesk blocks a looping sender for one hour. Make the window a tenant setting with a safe default |
| 4 | P0 | **CC and follower model**: visible CCs (requester side, receive public replies) separate from followers (internal, can receive notes, never shown) | G2-M5 and G2-M18 | Map CCs to the requester relationship and followers to the master spec's "watchers" ([master spec](../spec/master-spec.md)). Both are notification recipient roles in G2-M18 routes |
| 5 | P0 | **Macros** (named bundles of field changes, comment text and transitions, personal or shared, suggested by context) | Tickets module release after G2-M9 | Model macros as spec objects with stable IDs, so they upgrade like other configuration. Run them through `RecordWriteEngine`, so rules and permissions apply |
| 6 | P0 | **Standard metric catalog**: first reply time, next reply time, first and full resolution time (median and business-hours versions), one-touch percentage, reopen percentage, requester wait, agent work time, created versus resolved, backlog by state category, unassigned, SLA achieved and breached percentage per target and per ticket | G2-M9 (REQ-OPS-004, REQ-WS-004) | Ship these as named metric definitions. Store SLA outcomes **one row per target instance**, which also closes the open item "historical breach reporting" from G2-M4 |
| 7 | P0 | **Drill-down into the explorer**, not a row panel; counts match | G2-M9 (REQ-WS-003) | Zendesk's drill-in ignores metric filters and can disagree with the figure. Esmbler's single metric definition prevents that, so state it in marketing and test it |
| 8 | P1 | **Agent notification preferences**: assignment, reply, @mention, reopen, approval; persistent inbox with read history and unread filter; tab badge | G2-M18 (REQ-NOT-001, REQ-NOT-003, REQ-NOT-004) | Notifications in Zendesk live inside triggers, which is a major source of trigger clutter. Keep notifications in routes and profiles, separate from business rules |
| 9 | P1 | **Precise SLA alerts**: alert at a percentage of the target, at breach, and on escalation, with acknowledgment | G2-M19 (REQ-ALR-001) | Zendesk can warn only roughly an hour ahead, and triggers cannot use SLA conditions. Durable timers make "notify at 75% of target" exact. Demo this |
| 10 | P1 | **Next reply time and periodic update metrics** | Tickets module release after G2-M5 | Requires SLA start and stop conditions on conversation events (an inbound public comment, an outbound agent public reply), not only state changes |
| 11 | P1 | **Requester-facing state labels** and statuses shown to end users | G2-M6 portal | Zendesk shows "On-hold" as "Open" to requesters. The portal tracker needs the same separation |
| 12 | P1 | **Portal**: my requests, organization-shared requests, requests I am copied on, approval responses in the portal, and converting a ticket into a catalog request | G2-M6 (REQ-BIZ-004) | Zendesk shipped ticket-to-service-request conversion only in September 2026. Esmbler can include it from the start |
| 13 | P1 | **Merge** with a preview, a chosen surviving record, and moved participants and conversation; **unmerge** when it is safe | Ticket lifecycle (REQ-BIZ-001) | Zendesk merges cannot be undone and keep only the last public comment. Merge as an audited operation that can be reversed is a differentiator |
| 14 | P1 | **Configuration deployment UX**: snapshot, compare, dependency check, test deploy, deploy, revert | G2-M10 (REQ-TEN-004) | Use stable IDs so a label change is never a delete and recreate, which is the field-rename data loss in Zendesk. Do not copy targets, webhooks or schedules as active (Zendesk copies them inactive) |
| 15 | P1 | **Next-ticket mode** (Zendesk's Play mode or Guided mode) and **collision indicators** on records | Renderer and record page (REQ-LIVE) | Linear-style speed, as the master spec asks. Collision can reuse the realtime channel ([ADR 0008](../adr/0008-realtime-channel-for-live-editing.md)) |
| 16 | P1 | **Webhook signing and circuit breaker**: HMAC over timestamp and body, retry on 429 and 503 with `Retry-After`, automatic disable with a visible log | G2-M8 (REQ-EVT-007) | Esmbler already has per-consumer dead letters and replay (G2-M17). Add signing and deactivation of broken endpoints |
| 17 | P2 | **Runtime triage** (topic, sentiment, language, with confidence) written to fields that rules can use | Runtime AI (master spec, runtime AI agents) | Do not charge extra to use AI output in rules, which is a Zendesk Copilot add-on gate. The fields are ordinary fields with a provenance marker |
| 18 | P2 | **Summaries, writing help, merge suggestions, suggested macros** | Runtime AI | These are cheap and expected. Respect field visibility: summaries must never include hidden fields or notes the viewer cannot see |
| 19 | P2 | **Skills, capacity and round-robin assignment** | Assignment strategy extension point | Esmbler's `core.tickets.strategy.assignment` is the natural place. Start with round robin and capacity per group |
| 20 | P2 | **Side conversations and child tickets** | Fulfillment tasks and parent/child already exist | Email side conversations depend on G2-M5. Slack and Teams belong to G2-M8 connectors |

### Where Esmbler can differentiate

1. **AI-native configuration across connected surfaces.** Admin Copilot drafts or recommends one trigger, SLA or action flow at a time. Esmbler's builder produces one reviewable draft covering form, rules, workflow, explorer, dashboard and permissions, previewed per persona and published together ([REQ-WS-001 to REQ-WS-005](../spec/connected-workspace.md)). Demo request: "Add a Vendor field, required for hardware, group by vendor, and show it on the dashboard." This would take an admin several Zendesk screens.
2. **Upgrade-safe customization.** Zendesk copies its default triggers and automations into each account, where they drift and never upgrade. Its deploy tool can drop field data on a rename. Esmbler's typed overlays with expected prior values, stable IDs and module upgrades ([layered configuration](../explanation/layered-configuration.md)) let a tenant take Tickets 1.6 without losing changes. This is a story Zendesk cannot tell.
3. **Rules instead of tag logic.** Zendesk conditions come from fixed menus, and state is often kept in tags. SLA targets are keyed on the system priority only. Esmbler's CEL expressions over typed fields (REQ-RULE) remove most uses of tags and make rules readable to the AI and to auditors.
4. **Precise time-based behavior.** Durable timers replace hourly automations: no 1,000-ticket cap, no random point in the hour, no nullifying conditions, and alerts exactly when due (G2-M19).
5. **One source of truth for metrics.** Live, permission-aware figures that match the explorer's count, compared with an analytics product that syncs hourly and has a separate drill-in model (G2-M9).
6. **Governance without gating it to Enterprise.** Custom roles, sandboxes, multiple schedules and audit are Enterprise features in Zendesk. Esmbler's ReBAC, environments and hash-chained audit are platform capabilities, and Esmbler's packaging should keep them that way.
7. **Delegated AI with a clear boundary.** Zendesk's MCP server is still in early access, and billing depends on an LLM judging resolutions. Esmbler's MCP server is already the main way to configure the platform, its agents act with the user's permissions intersected with scopes, and every change is audited. Esmbler should meter AI per tenant with transparent counting (master spec, runtime AI agents) rather than copy outcome-based pricing.

### What not to copy

- **Tags as hidden state.** Keep tags (`labels`) as a user-facing field, not a mechanism for rules.
- **Notification logic inside business rules.** Keep G2-M18 routes separate.
- **Feature gating of safety and governance** (sandboxes, roles, audit).
- **Merges that cannot be undone** and **one problem per incident**. When the Incidents and Problems modules arrive (G3-M8 and later), model these as many-to-many relationships.
- **Separate products for chat and messaging.** Esmbler is an ESM platform; email, portal and in-app cover the pilot, and conversational channels can come later as connectors.

## Related documents

- [Tickets module architecture](../architecture/modules/tickets.md)
- [Connected workspace addendum](../spec/connected-workspace.md)
- [Events, audit, alerts and notifications](../architecture/events-audit-alerts-notifications.md)
- [ADR 0017: event, audit, alert and notification services](../adr/0017-event-audit-alert-notification-services.md)
- [ADR 0018: SLA and business hours](../adr/0018-sla-and-business-hours.md)
- [Progress and backlog](../progress.md)
