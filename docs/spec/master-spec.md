# AI-Native ESM Platform — Master Prompt

Oct 10, 2026 · @Daniel Maceiras

Revision 3 — Revision 2 (architecture review: phased delivery, configuration deployment, data and workflow migration, package conflicts, asynchronous processing, AI safeguards, measurable acceptance gates) plus live editing while chatting, Linear/Notion-inspired design direction, and the chosen technology stack.

This is the full product vision and requirements reference. Execution instructions (autonomy, stack conventions, gate order, agent files, documentation standards) are in `docs/spec/execution-brief.md`; where the two differ, the execution brief wins. Product inputs marked \[FILL IN\] below are resolved by the defaults in the execution brief, section 2.

---

## Role and vision

You are a principal software architect and product designer. Design an AI-native Enterprise Service Management (ESM) platform covering tickets and service requests, incidents, problems, change requests, CRM, projects, knowledge, and assets/CMDB.

The platform is composable: every capability is a module that can be installed, removed, and combined per tenant like building blocks. Users build and modify modules, pages, fields, rules, and relationships by chatting with an AI assistant (e.g., Claude) that operates the platform through an MCP server, assembling declarative specs from the elements the platform exposes. Third parties can build and sell modules, and mods to first-party modules, on a marketplace. All customization is tenant-scoped, and everything built automatically follows the platform's design system.

Be concrete: include schemas, interfaces, and example payloads, not generic advice. State assumptions explicitly.

## Product inputs and first-release scope

Resolve these inputs before making irreversible architecture decisions. If an answer is missing, propose a default with its consequences and identify which milestone needs the decision.

| Input | Value or decision required |
| --- | --- |
| First buyer and primary users | \[FILL IN: internal IT/service desk, MSP, customer support, or another segment\] |
| First complete workflow | Recommended starting point: intake → triage → assignment → optional approval/fulfillment tasks → resolution → requester confirmation |
| Engineering team and operating capacity | \[FILL IN: team size, skills, support/on-call capacity\] |
| Budget and delivery constraints | \[FILL IN: infrastructure budget, desired pilot date, contractual requirements\] |
| Initial and growth scale | \[FILL IN: tenants, active users, records per tenant, custom fields, peak writes, searches, concurrent automations, attachment volume\] |
| Hosting and regions | \[FILL IN: cloud, required regions, pooled/dedicated needs, deployment ownership\] |
| Data classification and integrations | \[FILL IN: allowed sensitive data, model-provider restrictions, first required connectors\] |
| Technical preferences | Containerized deployment; C# on the current .NET LTS (ASP.NET Core) backend; Angular frontend; PostgreSQL. Details in the execution brief |

**Domain boundaries**

- Tenant: the isolation, configuration, subscription, and administrative boundary.
- Customer account: an organization served within a tenant; account membership never implies membership in a different tenant.
- Department/team: an organizational and routing unit within a tenant.
- Environment: an isolated runtime/configuration target within a tenant, with separate data, credentials, jobs, and integration endpoints.
- A person may belong to multiple tenants. Tenant/environment switching must be explicit and authorized; never infer it from a record ID or user-controlled tool argument.
- If MSP operation is chosen, decide whether clients are accounts within one tenant or separate tenants. Define any cross-tenant operator experience as an explicit, audited capability.

**First usable release**

Deliver Tickets/Service Requests, email and portal intake, public replies and internal notes, assignments, SLA timers, basic approvals and fulfillment tasks, a small reporting surface, one validated integration, and AI customization of supported fields, pages, rules, and workflows. Include tenant isolation, audit, permission enforcement, versioned configuration, migrations, and recovery from the beginning.

Start with a constrained element catalog, one supported extension toolchain, private package installation, and one builder at a time with concurrency protection. Preserve public SDK boundaries.

Later phases add Incidents and Knowledge, followed by Problems, Changes, CRM, Projects, full Assets/CMDB, advanced analytics, real-time collaborative editing, additional extension languages, native mobile apps, and public marketplace commerce. The roadmap may reorder these based on validated customer need. Do not expand the first release merely because a capability appears in the long-term catalog.

Track product outcomes: time to onboard a tenant, time to configure a working service workflow, successful AI customizations without custom code, upgrade success rate, and operator effort per tenant. Set numeric targets with the product owner before the pilot.

## Architecture model

- **Core:** modular monolith, API-first and event-driven, with enforced module boundaries (separate schemas/packages, lint rules blocking cross-module imports). Designed so any module can later be extracted into a service.
- **Mandatory kernel:** contains tenant/environment context, identity and authorization enforcement, the record and relationship contracts, configuration versions and activation, audit, the module/capability registry, and reliable event contracts. It knows no business-module internals.
- **Shared platform services:** workflow/state machines, SLA/business-hours, assignment/routing, forms/layout rendering, notifications, search, reporting, and automation. Declare their dependencies and failure behavior separately from the kernel. A service required by an installed module cannot be removed while that dependency remains.
- **Business modules:** Tickets, Incidents, CRM, and the other domain capabilities are installed on top of the kernel and shared services. Publish a dependency graph and distinguish mandatory services from optional modules.
- **Module communication** only via (1) a domain event bus, (2) versioned public APIs/contracts, (3) the shared relationship/link service. Modules never read or write another module's data directly.
- **Metadata-driven configuration layer:** entities, fields, forms, pages, workflows, rules, and automations are declarative specs, so most customization needs no code.
- **Server-side extensions** run in a sandbox based on the WebAssembly Component Model, WIT interfaces, and a pinned Wasmtime runtime, with capability-based permissions, CPU/memory/time limits, and per-tenant/environment isolation. Evaluate WASI 0.3 against the required host interfaces and actual language toolchains. Publish a tested language/runtime compatibility matrix; do not assume Rust, JS/TS, and Go have equivalent support. Start with one supported toolchain and add others after conformance and isolation tests.
- **UI extensions:** untrusted publisher UI runs in sandboxed iframes on isolated origins with restrictive content/security policies and a typed, validated postMessage bridge. Validate sender, origin/channel identity, message schema, and allowed capabilities. Do not provide ambient platform credentials or arbitrary host DOM access. Web components may be used for trusted platform components or inside the isolated frame; Shadow DOM is not a security boundary.
- **Dogfooding rule:** all first-party modules are built only on the public SDK and extension APIs. No private backdoors.
- **Tech stack:** use the preferences in Product inputs. Recommend and justify unspecified choices using the initial workload and team constraints. Record major decisions and rejected alternatives in architecture decision records.
- **Control and execution separation:** configuration editing, package management, and publishing must not share unrestricted privileges with ordinary record operations. The runtime consumes validated, immutable configuration artifacts.
- **Service extraction:** module boundaries preserve an extraction path, but do not promise extraction without engineering work. Document transaction boundaries, deployment coupling, and shared-service dependencies.

## Multi-tenancy and tenant-scoped customization

All customization is strictly tenant-scoped. A change made by one tenant never affects any other tenant.

- **Layered metadata:** Platform base (versioned first-party modules) → Installed packages (per tenant) → Tenant overlay (fields, rules, pages, workflows, labels, permissions). The runtime resolves an "effective config" per tenant by merging the layers.
- **Base is immutable to tenants.** Overlays are additive: add, extend, hide, relabel, reorder, override within published extension points. Removing or breaking core fields or contracts that other modules or packages depend on is rejected with a clear error.
- **Namespacing** for all identifiers (core.\*, publisher.\*, tenant\_\_\*) to prevent collisions between platform updates, packages, and tenant customizations.
- **Custom field storage:** default to shared physical structures with no per-tenant tables or columns. Choose JSONB, typed extension tables, or a justified hybrid. Physical tenant-specific indexes are DDL: explicitly decide whether controlled index DDL is permitted, and specify approval, quotas, build cost, and retirement. If all per-tenant DDL is prohibited, use shared indexing structures and document query limits.
- **Upgrades:** compose candidate base/package versions beneath tenant overlays, then validate the resolved configuration and required data/workflow migrations. Promote automatically only when the tenant's update policy and compatibility gates permit it. Never silently resolve a behavioral or security conflict.
- **Isolation:** tenant/environment scope on records and all derived data; Postgres row-level security (or equivalent); scoped caches including configuration and authorization revisions; scoped rule and automation execution; per-tenant rate limits, quotas, and fair scheduling.
- **Database enforcement:** application runtime roles must not be superusers, have BYPASSRLS, or own protected tables. Use FORCE ROW LEVEL SECURITY where appropriate, constrain privileged functions, and separate migration/backup roles. Tenant context must be transaction-scoped when using connection pools. Test database, API, background-job, search, export, attachment, and reporting access paths.
- **Tenancy tiers:** pooled (shared DB) by default, with dedicated database/region for enterprise or data-residency customers, without code changes.
- **Per-tenant environments:** dev/sandbox and prod, with promotion (diff, approve, deploy) and rollback of overlay changes.
- **Environment safety:** sandbox data is synthetic or explicitly sanitized. Production credentials, webhook destinations, notification recipients, and enabled schedules are not copied automatically. Preview/simulation modes suppress external side effects unless a separately authorized test destination is configured.

## Configuration composition and deployment

Treat configuration as a compiled, versioned deployment artifact.

- Every entity, field, node, rule, workflow, and extension point has a stable identifier independent of its display label.
- Store overlays as typed operations against stable identifiers, with the base version and expected prior value/version needed to detect conflicts. Do not rely on blind JSON object merging or array positions.
- Resolve an exact package dependency lockfile and deterministic composition order. Compare previous base, new base, and tenant changes to distinguish compatible additions from conflicts.
- Maintain a dependency graph covering schema references, formulas, rules, pages, workflows, policies, reports, packages, and active workflow definitions.
- Validate schema types, referenced IDs, authorization changes, extension-point cardinality, expression cost, dependency cycles, and migration readiness before activation.
- The compiled artifact includes its content hash, exact dependency versions, renderer/SDK compatibility, migrations, permission-impact report, and validation results. Preserve provenance explaining which layer produced each effective value.
- Publishing uses optimistic concurrency against the expected published revision. Bind human approval to the exact artifact hash, target tenant/environment, and relevant permission context; any material change invalidates the approval.
- Prepare required backward-compatible storage changes and backfills before atomically switching the active configuration pointer. Do not claim a distributed data migration is one atomic database transaction.
- Each request or command uses one pinned configuration revision. A form submitted under an incompatible older revision receives an actionable conflict/reload response. Active workflow instances pin their definition versions, while authorization is checked using current access rules.
- Cache invalidation and background workers use explicit version identifiers. Define behavior during propagation, activation failure, and loss of the configuration service.
- Preserve the previous usable artifact and require health checks after activation. Roll back configuration only when the data, workflow, and dependency compatibility checks permit it.

Deliverables: overlay operation schema, conflict examples, dependency graph schema, compile/publish state machine, artifact manifest, concurrent-publish example, and a failed activation/recovery walkthrough.

## Record ownership, queries, and schema evolution

**Ownership and queries**

- Each module owns the logical schema and domain invariants for its entities. The platform record engine provides persistence, authorization, validation, concurrency checks, and audit through public contracts. Specify whether physical storage is shared, module-specific, or hybrid.
- Cross-module access uses authorized APIs or declared query/projection contracts. The relationship service does not authorize access merely because two records are linked.
- Define a typed query contract for filter, sort, pagination, aggregation, relationships, formulas, and custom fields. Bound joins/traversal depth, execution time, result size, and export workloads.
- Apply authorization before pagination and aggregation. Define how search and analytics satisfy the same restrictions and how revocations propagate. Do not leak restricted values through counts, facets, formulas, snippets, rollups, or error messages.
- Specify data types precisely: decimal/currency precision, UTC instants versus local dates, null versus absent values, stable choice IDs, and tenant-scoped uniqueness.
- Use record revisions for optimistic concurrency and idempotency keys for retried commands. Define conflict responses and permitted merge behavior for simultaneous edits.
- Specify referential integrity, relationship ownership, deletion restrictions, soft-delete behavior, and restoration. Shared person/account identities and global platform metadata need explicit ownership and scoping rules.

**Migration and recovery**

- Classify schema changes as compatible, requiring backfill, requiring explicit conversion, or destructive. Include rename, type change, required-field addition, choice removal, field retirement, and entity removal.
- A migration plan includes preconditions, affected-record counts, conversion rules, invalid-record handling, checkpoints, retry/idempotency behavior, operational limits, and recovery steps.
- Use expand/backfill/contract transitions where needed. Preserve field identity across relabeling; do not silently drop values that fail conversion.
- Version workflow definitions and define whether existing instances stay pinned, migrate through an explicit state mapping, or require an approved cancellation. Preserve pending approvals and fulfillment tasks.
- Version SLA calendars/policies and specify how edits affect running timers, recorded breaches, and historical reports.
- Distinguish configuration rollback, data recovery, workflow migration, and compensating external actions. Sent emails and completed external operations are not undone by restoring a configuration version.
- Block unsafe rollback and offer an actionable forward-repair plan. Test failed and partially completed migrations with realistic records.

Deliverables: physical/logical storage decision, query interface, representative indexed query plans, migration manifest, and examples of a field conversion and workflow upgrade with active records.

## Transactions, events, and durable execution

- Specify the transaction boundary for each command. A record change, its audit entry, and its outbox event should commit consistently; identify any cross-module operation that instead needs a saga and compensation.
- Assume at-least-once delivery. Use durable consumer deduplication/inbox records and idempotent handlers; do not claim the outbox alone gives exactly-once effects.
- Event envelopes include event ID, tenant/environment, entity/record ID, record revision, event/schema version, configuration/definition reference, occurrence time, correlation/causation IDs, and a minimally necessary payload.
- Define ordering guarantees per record or aggregate. Consumers that maintain current-state projections must reject or reconcile stale revisions. Consumers of business transitions must detect gaps and recover required transitions rather than blindly discard older events.
- External side effects use provider idempotency where available. Otherwise define reconciliation for ambiguous outcomes and acknowledge limits on duplicate prevention.
- Persist workflow state and timers so work survives process restarts. Define ownership/leases, retry backoff, maximum attempts, dead-letter inspection, cancellation, operator replay, and compensation.
- Replay must be scoped, audited, and safe against repeated notifications, tasks, approvals, and charges. Reauthorize execution when relevant permissions or package grants have changed.
- Detect automation cycles using causation chains and execution budgets. Limit recursive triggers, fan-out, concurrent jobs, and outbound calls, with tenant fairness and backpressure.
- Synchronous validators are deterministic and time-bounded, with no external network calls. Define failure behavior per hook: validation/security failures block the transaction; optional enrichments may run asynchronously.
- Connector execution has its own rate-limit, retry, checkpoint, and credential-lifecycle policies. Never hold a database transaction open while waiting for a remote system.

Deliverables: event envelope and consumer contract, duplicate/out-of-order scenarios, workflow recovery example, retry/replay operator interface, and a side-effect reconciliation example.

## Identity and permissions (core module)

Identity and permissions are a core kernel module that every other module, package, and the AI builder relies on.

**Identity**

- **Principals:** internal users (agents, admins, approvers), external users (customers, portal requesters, partners), groups/teams, service accounts, package principals, and AI agents as first-class, auditable principals.
- **Org model:** departments, locations, reporting hierarchy, skills, and on-call schedules, usable in rules and routing.
- **Enterprise auth:** SSO (SAML, OIDC), SCIM provisioning and deprovisioning, MFA, passkeys, session policies, per-tenant identity providers.
- **User types and licensing:** e.g., agent (paid), requester (free), approver (light). Keep authorization and commercial entitlements separate; an operation must satisfy both where applicable. License changes never silently grant access or erase data.

**Authorization model**

- **Hybrid RBAC + ABAC + ReBAC:** roles for coarse access; attributes for context (department, region, priority); relationship-based access (Zanzibar-style) for ownership and links, e.g., "members of the group that owns this queue can edit its tickets" or "contacts of an account can view that account's tickets." Evaluate engines such as OpenFGA, SpiceDB, or Cedar, and justify the choice.
- **Four levels:** entity (create/read/update/delete), record (row-level conditions and relationships), field (hidden, read-only, masked), and action (approve change, publish config, install package, export data).
- **Policies are specs:** default modules ship default roles and policies; packages declare the permissions they add; tenants customize in their overlay. Same layering, versioning, draft, and rollback as all other config.
- **UI follows permissions automatically:** pages, fields, actions, and list results render according to the viewer's access; no per-page permission code.
- **List filtering at scale:** permission-aware queries and search (pre-filtered, not post-filtered), with caching and consistency guarantees explained.
- **Derived surfaces:** apply equivalent restrictions to reports, exports, search facets/snippets, formulas, rollups, activity feeds, notifications, logs, model context, and retrieval indexes. Document revocation propagation and bounded staleness; sensitive actions fail closed when authorization cannot be established.
- **Relationship changes:** creating a link must not implicitly grant broader access unless an explicit, authorized relationship policy allows it. Test linking confidential records to less-restricted records.

**AI, packages, and builders**

- **The AI never exceeds its delegated authority:** interactive MCP actions use the intersection of the invoking user's current permissions, the agent's delegated scopes, and applicable package capabilities. The AI cannot elevate itself through a configuration change. Permission changes require a distinct authorized administrative action and any required independent approval; ordinary build/publish rights do not imply permission-administration rights.
- **Builder permissions:** who can customize what, e.g., tenant admin, module builder scoped to one module, draft-only builder whose changes require admin approval to publish.
- **Packages:** effective access is the intersection of the package's granted capabilities and the invoking user's permissions; background jobs run as the package principal with only its consented scopes.
- **Plain-English policies:** the AI turns "only change managers can approve high-risk changes" into a policy spec, simulates it, and shows impact ("14 users lose approve access") before publishing.
- **Background AI:** unattended agents use an explicit service/agent principal with approved scopes, schedules, budgets, expiry/revocation behavior, and audit attribution. Do not reuse a stale interactive session or assume a human is invoking each run.

**Explainability and governance**

- **Explain access:** "why can Maria see this ticket?" or "why can't she edit this field?" answered by an explain tool (also exposed via MCP).
- **View as user:** permit authorized persona simulation using synthetic or appropriately filtered data. Permission to edit a page does not grant access to the target user's records. Any real-data impersonation requires separate authorization and audit.
- **Governance:** full audit log, periodic access reviews, just-in-time elevated access with expiry, delegation (out-of-office), break-glass access for major incidents, separation of duties (e.g., requester cannot approve own change).

Deliverables for this section: permission data model, policy spec schema with examples at all four levels, how permission checks integrate with the record engine, search, MCP, and the extension runtime, and an example "explain access" response.

## Package types and extension points

Three kinds of packages:

1. **Modules:** new capabilities (e.g., "Field Service").
2. **Mods:** extend an existing module via its published extension points (e.g., "AI triage for Incidents"). Mods never patch internals.
3. **Config packs:** no-code bundles (workflows, forms, catalog items, dashboards, rules).

Every module must publish these extension points, each versioned and listed in a public contract registry:

- **Lifecycle hooks:** before/after create, update, delete, transition (sync validators + async subscribers)
- **Overridable actions and pluggable strategies:** assignment, risk scoring, SLA calculation, priority matrix
- **Schema extensions:** add fields and related entities to another module's records
- **UI slots:** record tabs, side panels, header actions, list columns, card areas, dashboard widgets
- **Workflow steps and automation triggers/actions**

**Package manifest:** name, publisher, version, type, target modules + semver ranges, required capabilities/scopes, extension points used, events consumed/published, data schemas + migrations, UI contributions, settings, pricing model.

**Package lifecycle:** install, migrate, upgrade, disable, uninstall, with defined handling of package data (grace period, export, purge).

**Composition and dependency contracts**

- Every extension point declares its cardinality: multiple subscribers, ordered pipeline, or one selected provider. Specify deterministic ordering, timeout/error behavior, and whether contributions may be composed.
- Two mods cannot silently replace the same exclusive strategy. Require explicit selection or reject activation with a conflict report.
- Resolve exact package versions in a lockfile, including dependencies on shared services and extension contracts. Semver declarations are inputs to validation, not proof of behavioral compatibility.
- Show the impact of changing or removing a field, policy, package, or extension provider across dependent forms, rules, reports, workflows, and active instances.
- Uninstall/disable/license-expiry plans define dependency blocking, queued jobs, active approvals/workflows, retained fields/data, read/export access, and administrator recovery. Do not silently strand operational work.
- A detached module gets a distinct identity and ownership boundary. Remap dependencies deliberately; detachment does not create a hidden fork still masquerading as the original module.
- Define publisher-abandonment handling, package/version revocation, emergency isolation, security-update policy, and support responsibility. A package kill switch must expose its operational impact and preserve customer data.
- Validate both dependency compatibility and tenant-overlay compatibility before staged rollout. State the supported version window and deprecation policy.

## Default modules (ship-ready starting point)

Each released first-party module ships as a complete usable default. Tenants customize on top with the AI builder and receive upgrades through the compatibility and migration process. The list below is the long-term module catalog; delivery follows the phased roadmap.

- **Default module at first launch:** Ticketing/Service Requests, including the initial request/approval/fulfillment workflow.
- **Subsequent default modules:** Incidents and Knowledge, then Problems, Changes, CRM, Projects, and Assets/CMDB, prioritized by validated customer workflows.
- **Each default module ships complete:** entities, fields, choice lists, states and transitions, SLA policies, assignment rules, notifications, record and list pages, portal request form, dashboards, roles, and sample data.
- **Customize, never fork:** all tenant changes (via AI or drag-and-drop) are stored in the tenant overlay. The default stays intact underneath and keeps receiving platform upgrades.
- **Visible customizations:** the builder marks what the tenant changed vs. the default ("modified" badges), can compare any element with its default, and the AI can explain "what have we customized in Ticketing?"
- **Reset to default:** per field, rule, page, or whole module, with a dependency/data impact preview. Reset changes configuration; data deletion or migration is a separate authorized operation.
- **Detach (later escape hatch):** a tenant may fully detach a module into its own copy under the package identity/dependency rules, with an explicit warning that it will no longer receive upstream upgrades.
- **Starter templates:** opinionated configurations on top of defaults per department (IT, HR, Facilities, Customer Support) and per industry, selectable at onboarding or installable later. Templates are config packs, so publishers can sell their own.
- **AI onboarding:** on first run, the AI interviews the admin (team size, departments, existing tools, SLA needs) and proposes customizations to the defaults as a reviewable draft.
- **Reference implementation:** the default Ticketing module is built entirely on the public SDK, specs, and extension points, and serves as the canonical example in developer docs.

Deliverables for this section: the complete spec of the default Ticketing module (entities, fields, states, SLAs, rules, pages, notifications, roles), and an example where a tenant asks the AI to customize it (add fields, a conditional rule, a new card on the record page), then later receives a platform upgrade to Ticketing without losing those changes.

## Business behavior required for usable modules

Treat these as explicit behavior contracts and acceptance scenarios, not merely widget names.

| Area | Required behavior |
| --- | --- |
| Ticket lifecycle | Ownership, watchers/participants, parent/child work, duplicate detection, merge/split policy, reopening, cancellation, concurrent edits, and retained audit history |
| Conversations | Separate public replies from internal work notes; recipient visibility checks; notification rules; safe rich-text rendering; edit/redaction history; portal participant access |
| Email intake | Threading and sender verification policy; retries/deduplication; unknown senders; auto-reply/loop prevention; attachment processing; prevention of internal-note disclosure |
| Requests and catalog | Typed request variables, approval stages, fulfillment tasks/dependencies, partial completion, rejection, cancellation, and ownership of request versus task SLAs |
| Approvals | Sequential/parallel stages, delegation, expiry/escalation, separation of duties, requester changes after approval, and active-instance behavior on policy updates |
| SLA/business hours | Start/pause/resume/stop rules, business versus elapsed time, reopening, time zones, daylight-saving transitions, holidays, calendar/policy versioning, and historical breach reporting |
| Attachments | Object-level authorization, malware scanning/quarantine, size/type limits, authorized downloads, retention/legal-hold handling, and orphan cleanup |
| Knowledge | Draft/review/publish lifecycle, version history, audience restrictions, article ownership, feedback, and permission-aware search/retrieval |
| CMDB (later phase) | Identification, discovery/import provenance, reconciliation, authoritative-source precedence, duplicates, stale records, relationship quality, and accountable owners |
| Connectors | Credential rotation/revocation, remote quotas, timeouts/retries, sync checkpoints, field mapping, conflict resolution, deletion semantics, and operator-visible sync health |

Define CRM scope before implementing it: customer-support account/contact context and a sales pipeline are different product commitments.

## Admin Center and module admin panels

Every module and package, first-party or marketplace, gets its admin panel inside one unified Admin Center, so admins never learn a different UI per module.

- **Settings schema in the manifest:** each package declares its settings (types, defaults, validation, grouping, help text) in its manifest. The platform auto-generates the admin panel from that schema using design-system components. Most packages need zero custom admin UI.
- **Custom admin pages when needed:** packages can add pages to Admin Center slots, built from the same elements and layout system as any other page.
- **Standard sections for every package:** Overview, Settings, Permissions/roles it adds, Setup checklist, Health (errors, runs, latency), Usage and quotas, Logs, License and billing, Version and updates, Data (retention, export, uninstall). Health includes failed jobs, controlled replay, migration status, dependency conflicts, and emergency disable impact.
- **Setting scopes:** tenant-wide, per environment (dev/prod), and optionally per group, department, or location, with clear inheritance.
- **Settings are config:** stored in the tenant overlay, versioned, with draft, diff, promotion, and rollback like everything else.
- **Secrets:** API keys and credentials are declared as secret fields and stored in a vault. UI, MCP reads, logs, previews, and package exports expose only metadata/references. Prefer host-mediated connector calls so extensions do not receive raw credentials; any necessary injection is narrowly scoped to the owning package, tenant, environment, destination, and operation. Support rotation and revocation.
- **Delegated admin:** packages declare admin permissions (e.g., incidents.admin); a tenant can make someone admin of one module without full tenant admin rights.
- **Setup wizard on install:** packages can declare onboarding steps (connect account, map fields, choose defaults) rendered by the platform.
- **Feature flags:** packages can expose toggles for gradual rollout to groups.
- **AI-operable:** settings are readable and writable via MCP ("turn on auto-assignment for the Network queue"), with the same permission checks and draft/publish flow.

Deliverables for this section: settings schema format with an example, the auto-generated admin panel it produces, and the Admin Center navigation structure.

## AI-native builder via MCP

The AI builder is the core product differentiator. The default builder composes declarative platform elements. Custom code is a distinct extension-development path with separate permissions, validation, and release controls.

- **Spec-driven everything:** entities, fields, relationships, pages, layouts, widgets, workflows, rules, automations, and permissions are declarative JSON specs, validated against JSON Schema and rendered by the platform (server-driven UI).
- **Self-describing element registry:** every element exposes a props schema, description, usage guidance, constraints, and examples written to be consumed by an LLM.
- **MCP resources:** element catalog, design system (tokens, components, patterns, do/don't guidance), current tenant app graph and schema, docs.
- **MCP discovery tools:** list/describe elements, entities, extension points, marketplace packages.
- **MCP build tools:** create/update entity, add field, link entities (1:1, 1:many, many:many), create page, insert/update/move/delete node, wrap in container, add rule, define workflow, create automation, set permissions.
- **MCP review tools:** validate, simulate rule, preview (returns a live preview URL), diff vs. published.
- **MCP lifecycle tools:** publish, rollback, export as package, install marketplace package (with admin consent).
- **Precise, actionable errors** so the AI can self-correct (what is wrong, where, and a valid alternative).
- **Draft workspaces:** all AI configuration changes land in an isolated draft; a human reviews the exact diff and preview before publishing. Use immutable artifact approval, optimistic concurrency, and compatibility-checked rollback.
- **Tenant/environment-bound by auth:** context comes only from an authenticated, explicitly selected session. MCP build tools expose no arbitrary tenant/environment override. Interactive calls use the current user's identity and delegated scopes; destructive or schema-breaking operations require server-enforced approval. Audit actor, agent, target, operation, configuration revision, and outcome.
- **Escape hatch:** when no element fits, an authorized developer may use the AI to generate a custom component or function. It enters the reviewed package build/release pipeline and cannot execute immediately as ordinary configuration. Apply sandbox, capability, supply-chain, design, and performance checks.
- **Chat to package:** export configuration and reviewed extension code as a package after dependency and portability checks. Exclude tenant records, secrets, private identifiers, environment bindings, and sensitive sample data by default; require explicit mapping for deploy-time settings.

**AI execution safeguards**

- Treat records, comments, attachments, retrieved documents, connector responses, and marketplace descriptions as untrusted data. Instructions embedded in them cannot expand tool authority or approve actions.
- Human approval records identify the exact artifact/operation hash, affected resources, environment, approver, expiry, and expected revisions. Approval is invalidated by material changes. Recheck permissions and preconditions at execution time.
- Simulations and previews cannot send emails, call production write APIs, publish webhooks, or schedule live jobs. Use synthetic data and explicit test connectors.
- Enforce model-provider, regional-processing, retention, and allowed-data policies before sending context. Apply field permissions and redaction to prompts, retrieved snippets, outputs, tracing, and stored conversations.
- Retrieval indexes and cached AI results carry tenant/environment scope and authorization provenance. Define revocation/deletion propagation; never reuse a broader-permission answer for a less-privileged user.
- MCP authorization validates intended token audiences and separates downstream credentials; prohibit token passthrough. Connector egress is capability/destination restricted with SSRF protections.
- Bound tool calls, execution duration, context size, model spend, and record/action counts. Expose cancellation and per-agent disable controls.
- Evaluate the builder against saved customization tasks, policy-change cases, ambiguous requests, malicious embedded instructions, and unsupported operations. Report successful intent completion and unintended effects, not just valid JSON generation.
- Runtime AI modes are explicit: suggestion only, human-approved action, or narrowly preauthorized automation. Define the authorized action set and stop conditions for each.

## Layout system and visual builder

**Layout system**

- A page spec is a tree: Page → Section → Row → Column → Container → Element.
- Each row defines its own columns on a 12-column grid (e.g., 4/4/4, 8/4, 3/6/3), with responsive breakpoints and mobile stacking.
- Every node has a stable ID. Container types declare allowed children and constraints; invalid layouts are rejected.
- The same layout system powers record views, list views (configurable columns, sort, filter, grouping), boards, dashboards, and portal pages.

**Point-and-prompt visual builder**

- A canvas where users select one or more nodes (element, container, row). The selection is attached to the AI chat as structured context: node IDs, type, path, current spec.
- Example: user clicks the right-hand card and says "add a Vendor field here"; the AI calls insert\_node(parent\_id, position, spec).
- AI-proposed changes render as a highlighted preview on the canvas; the user accepts or rejects each change.
- Drag-and-drop editing works on the same spec, so human and AI edits are interchangeable. All edits are Command objects (undo/redo, diff, audit).

**Live editing while chatting (core experience)**

- The user customizes a page from the page itself: on any page they may customize (e.g., the Tickets record page), they open the AI chat panel docked beside it, and the page switches into a clearly labeled draft mode with sandbox or sample data.
- Each change the AI makes renders on the page as it happens, step by step, within about one second of the tool call (p95), with new or changed elements highlighted. Chat steps link to their elements on the page and the reverse.
- The user can try rules immediately in the draft, stop the agent mid-change, undo any single step, mix in drag-and-drop edits, and accept or discard the whole draft. Other users keep seeing the published page until publish.
- Mechanism: MCP build tools write Commands to the draft; the server pushes draft-change events over a real-time channel; the renderer applies spec changes incrementally without reload; version checks prevent human and AI edits from overwriting each other.

**Module builder workspace**

The builder shows the whole module in one workspace, so builders see every surface they are creating, live, while they chat with the AI:

- **Workspace tabs:** Data model (entities, fields, relationship graph), Pages (record, list, board, portal), Rules, Workflows and automations, Admin panel, Permissions, Notifications, Test, Package.
- **Live admin panel preview:** as the builder or AI adds settings to the module, the auto-generated admin panel updates side by side and can be clicked through like the real thing.
- **Persona preview:** switch the preview between personas (tenant admin, module admin, agent, approver, requester/portal user) to see exactly what each will see and can do.
- **Device preview:** desktop, tablet, mobile.
- **Test mode:** generated sample data, a rule and workflow simulator, and saved test scenarios that run before publish.
- **Collaboration:** first release supports version-checked edits and explicit conflict resolution so human and AI changes cannot silently overwrite each other. Later add real-time multi-builder presence, comments, and attribution, with a specified operation merge model.
- **Readiness checklist before publish:** validation errors, missing permissions, accessibility and design-lint issues, untested rules, breaking changes for dependent packages.

## Field rules and business rules

- **Field validation:** required, type (number, date, email…), min/max, length, regex, allowed values, uniqueness.
- **Conditional rules:** condition → action (require, show/hide, read-only, set value, restrict options, show message). Example: "if category == 8, then justification is required."
- **Expressions** in a safe, non-Turing-complete language (e.g., CEL) with a typed field reference model.
- **Two scopes:** data-level rules are enforced on every write path (UI, API, imports, automations, AI); form-level rules apply to one view only.
- **Server-side enforcement is authoritative.** The same rule specs are evaluated client-side for instant feedback.
- **Rule ordering, conflict detection,** and a simulate tool ("what happens if category = 8?").
- **Rule execution contract:** define evaluation phases, dependency ordering, cycle detection, missing/null handling, and maximum expression cost. A field cannot become both inaccessible to its editor and required without an authorized source/default or an actionable error. Use conformance cases to keep server and client evaluation aligned; never send hidden field values to the client to evaluate a rule.
- **AI confirmation:** the AI converts natural language into rule specs and confirms by restating the rule in plain English, including its scope.

## Design system: everything built matches the platform

Everything built (by tenants, by the AI, or by marketplace publishers) must look and behave like a native part of the platform.

- **One design system:** design tokens (color, typography, spacing, radius, elevation, motion), a component library, layout patterns, and interaction patterns (forms, tables, empty states, errors, confirmations, loading, notifications).
- **Specs reference tokens and components only.** No raw colors, CSS, HTML, or pixel values in specs. The AI cannot emit raw styling.
- **Page patterns as starting points:** standard record page, list page, board, dashboard, portal page, wizard. The AI starts from a pattern, then customizes.
- **Bounded tenant theming:** logo, brand color mapped onto the token palette with automatic contrast checks, light/dark mode, density. No overrides that break layout or component consistency.
- **Marketplace UI:** custom components must be built with the design-system SDK (tokens + primitives) and pass automated design linting (token usage, spacing, typography), accessibility checks, and visual review before listing.
- **Versioned design system:** compatible token/component updates propagate through validated release policies. Breaking renderer or interaction changes require compatibility checks, accessibility/regression tests, and staged rollout against supported specs.
- **Accessibility and i18n built in:** WCAG 2.2 AA, keyboard navigation, screen readers, RTL, translatable labels.
- **Design direction, inspired by Linear and Notion:** learn from their interaction patterns and design principles without copying their branding, logos, icons, or visual identity. From Linear: speed as a feature (optimistic updates, instant navigation), keyboard-first with a command palette, dense but calm lists and boards, minimal chrome, subtle fast motion, excellent dark mode, inline property editing. From Notion: block-based composition mapped onto the layout tree (drag handles on every node), a slash menu to insert elements alongside the AI chat, inline editing instead of separate edit forms, clean typography and generous whitespace. Editing a page should feel like editing a Notion page; working tickets should feel as fast as Linear.

## Element catalog

The long-term platform exposes the following elements and spec objects through the registry and MCP. Mark each with its delivery phase; implement only the subset needed for the first workflow initially. Propose additions only where a concrete workflow reveals a gap.

**Every element declares:** id, version, category, description for the AI, props schema, allowed parents and children, data binding (which field or query it shows), events it emits, rule hooks (required, visible, read-only), design tokens used, accessibility behavior, responsive behavior, and examples.

**Spec objects (non-visual)**

| Category | Objects |
| --- | --- |
| Data model | Entity, Field definition, Relationship (1:1, 1:many, many:many), Choice list, Computed/formula field, Rollup |
| Logic | Validation rule, Conditional rule, Workflow (states, transitions, guards), Automation (trigger → condition → action), Approval policy, SLA policy, Assignment rule, Escalation rule |
| Experience | Page, Layout, Record view, List view, Board view, Form, Dashboard, Report, Portal page, Catalog item |
| Platform | Role, Permission set, Notification template, Translation strings, Theme, Package manifest, Environment |

**Layout and containers**

| Category | Elements |
| --- | --- |
| Structure | Page, Section, Row, Column (1–12 span) |
| Containers | Card, Panel, Tab group + Tab, Accordion, Split pane, Drawer, Modal, Wizard/Stepper, Collapsible group |
| Navigation | App nav, Breadcrumbs, Record header, Global search, Command palette |

**Field types**

| Category | Elements |
| --- | --- |
| Text | Short text, Long text, Rich text, Email, Phone, URL |
| Numbers | Integer, Decimal, Currency, Percent, Duration, Rating |
| Dates | Date, Date-time, Time, Date range |
| Choice | Single select, Multi-select, Radio, Checkbox, Toggle, Tags, Status, Priority |
| References | Lookup (record reference), Multi-lookup, User, Group/Team, Account, Asset/CI |
| Media | Attachment, Image, Signature |
| Special | Auto-number ID, Formula, Rollup, Address/Location, Color, JSON |

**Display and data widgets**

| Category | Elements |
| --- | --- |
| Display | Heading, Text block, Label/value pair, Badge, Avatar, Icon, Callout/Banner, Divider, Spacer, Empty state, Progress bar |
| Collections | Data table, Related list, Kanban board, Calendar, Timeline/Gantt, Tree, Card grid, Saved views, Filter bar |
| Record activity | Activity feed, Comments, Work notes, Attachments panel, Audit history, Approvals panel, Checklist, Linked records graph |
| Service-specific | SLA timer, Priority matrix, Impact/urgency picker, Change risk score, CAB calendar, Major incident banner |
| Dashboard | KPI/metric tile, Bar, Line, Area, Pie/Donut, Gauge, Heatmap, Leaderboard, Report embed |
| Portal | Catalog item card, Request form, Request status tracker, Knowledge article view, Search results, Chat/virtual agent |
| AI | AI summary, Suggested actions, Similar records, AI field suggestion, Sentiment indicator |

**Actions**

| Category | Elements |
| --- | --- |
| Buttons | Button, Button group, Action menu, Split button, Link |
| Record actions | Workflow transition buttons, Quick create, Create related record, Clone, Merge, Bulk actions |
| Feedback | Toast, Confirmation dialog, Inline error, Loading/skeleton state |

## Software design patterns

Show where these patterns apply and the concrete responsibility each serves. Avoid introducing infrastructure solely to demonstrate a pattern; record justified deferrals:

- **Composite:** the page and layout tree
- **Command:** every edit, human or AI, for undo/redo, diff, and audit
- **Specification + Interpreter:** business rules as data, evaluated by a safe expression engine
- **Strategy:** pluggable behaviors (assignment, SLA calculation, risk scoring) that mods can swap in
- **Registry + Extension Point:** elements, hooks, and packages are registered, never hard-wired
- **Hexagonal architecture (ports and adapters):** inside each module
- **Transactional Outbox:** reliable event publishing in the same transaction as data changes
- **Saga:** cross-module workflows (e.g., Incident → Change → approval)
- **Observer / pub-sub:** domain events between modules
- **Layered configuration (overlay):** base → packages → tenant

## Marketplace and commerce

Design package contracts and private installation early. Implement public listings, payouts, and marketplace commerce only after the partner beta validates compatibility, support, and operational controls.

1. **Publisher onboarding:** company, freelancer, or consultancy accounts with identity verification and payouts via a marketplace payments provider (e.g., Stripe Connect).
2. **Build:** SDK, CLI, local emulator, free developer tenant with sample data, or build by chat through the MCP.
3. **Submit:** package signing, automated security scanning, contract tests against the extension points used, performance limits, design and accessibility linting. Human review for higher-risk packages (outbound data access, sensitive records).
4. **List:** store page with description, screenshots, pricing, requested permissions, compatible module versions, and trust badges (verified publisher, security reviewed).
5. **Install:** admin consent screen listing requested capabilities; the package installs into a draft environment, then is promoted to prod. Installing adds a package layer to that tenant only.
6. **Run:** packages run on the platform inside the sandbox. Customer data stays on platform infrastructure unless outbound access was declared and granted.
7. **Pay:** the platform bills the tenant, keeps its share, and pays out publishers. Pricing models: free, one-time, per seat, per tenant, usage-based, free trials. Use idempotent usage metering and reconciliation. A lapsed license preserves data and follows a documented grace/read/export and active-work policy; define which new operations are blocked and how existing work is completed or migrated.
8. **Update:** semver releases, staged rollouts, auto-update policies, and compatibility-checked rollback or forward repair. New permissions require admin re-consent. Package revocation and failed rollout have explicit operator recovery procedures.

- **Mod compatibility:** mods declare target module ranges (e.g., Incidents ^2.0). Define a maintained compatibility matrix and supported-version window; run affected supported-package contract tests against candidate releases, then validate each tenant's installed combination before rollout. Notify publishers of breaking changes with a deprecation window.
- **Private listings:** internal-only packages for an enterprise, or packages shared only with a consultancy's clients.
- **AI discovery:** the AI can search the marketplace through MCP and suggest installing an existing package instead of building from scratch.
- **Publisher tools:** ratings, reviews, install and revenue analytics.
- **Compliance responsibilities:** document platform, tenant, and publisher responsibilities for access controls, audit evidence, data processing, retention/deletion, subprocessors, and residency. Map controls to applicable customer obligations; do not treat SOC 2 or GDPR as a package flag or claim compliance solely from automated scanning.

## Platform essentials

Cover these in the architecture and assign an implementation phase. The first release includes the subset needed to safely operate the initial workflow; later-phase features must not silently expand its acceptance scope:

- **Omnichannel intake:** email-to-ticket (inbound parsing, threading, replies), portal, chat widget, Slack/Microsoft Teams, phone/voice integration, API. Channels are pluggable packages.
- **Integrations:** public REST/GraphQL API generated from specs (custom fields included), outbound webhooks, connector framework (Jira, GitHub, monitoring tools, HR systems), SSO and directory sync.
- **Migration and import:** begin with CSV import with validation, dry run, deduplication, and resumability. Phase in importers for ServiceNow, Jira Service Management, Zendesk, and Freshservice, with AI-assisted field mapping. Preserve source identifiers, history/attachment access rules, and import error reports.
- **Runtime AI agents (not only the builder):** triage, categorization, summarization, suggested replies, knowledge deflection, and auto-resolution, all running with explicit permissions and audit. AI usage metering and cost controls per tenant.
- **Reporting and analytics:** report builder over standard and custom fields, scheduled reports, an analytics store that does not load the transactional database, and export to BI tools.
- **Data lifecycle and compliance:** retention policies, archiving, GDPR export and deletion, legal hold, encryption at rest and in transit, optional customer-managed keys, data residency.
- **Operations:** scoped observability for logs, metrics, traces, and audit; alerting; failure recovery; backups; disaster recovery targets; SLOs; and a status page. PostgreSQL physical point-in-time recovery operates at cluster scope. For pooled tenants, design an isolated restore-and-extract/reconcile procedure, including records, configuration, relationships, attachment versions, and authorization data. Rebuild derived indexes and reconcile job/event checkpoints without repeating external effects. Test tenant recovery independently of whole-cluster recovery.
- **Platform limits (governors):** caps on rules, automations, custom fields, API calls, and extension runtime per tenant, with clear errors and upgrade paths.
- **Platform plans and billing:** your own subscription tiers (seats by user type, AI usage, storage, marketplace purchases on one invoice).
- **Mobile:** responsive web in the first release; native apps and push notifications in a later phase. Validate renderer compatibility and define offline/conflict behavior before promising shared-spec parity across clients.
- **Auto-generated documentation:** each tenant's customized modules produce up-to-date docs (fields, rules, workflows) that the AI can also answer questions from.

## Acceptance scenarios and operating targets

Agree on workload assumptions and numeric targets before a pilot. If values are unknown, propose candidates explicitly labeled as unapproved assumptions. Do not claim a target is met without measured evidence.

| Target | What must be specified and measured |
| --- | --- |
| Interactive performance | p95/p99 record read/write, list/search, and page-render latency at the declared tenant/record/custom-field scale |
| Configuration deployment | Compilation/validation time, activation latency, concurrent-edit conflicts, and usable recovery time |
| Authorization | Policy-check cost, permission-aware query cost, revocation propagation, and behavior when the authorization service is unavailable |
| Background processing | Sustainable throughput, oldest-job age, timer accuracy, retry overhead, and fairness while one tenant is overloaded |
| Search/reporting | Index freshness, permission/deletion propagation, query runtime, export limits, and transactional-database impact |
| Recovery | Separate RPO/RTO targets for platform outage, dedicated deployment recovery, and single pooled-tenant recovery |
| Extension runtime | Startup and execution budgets, memory/CPU limits, host-call/egress quotas, and enforced isolation |
| AI | Task-completion rate, unauthorized/unintended-action rate in the evaluation suite, latency, and cost per successful customization |
| Unit cost | Storage, search, jobs, models, and support cost per tenant/workload, with governor behavior before exhaustion |

Require automated or reproducible evidence for these scenarios:

1. Tenant A adds a field, rule, and page card; tenant B's behavior and accessible data remain unchanged.
2. Two builders edit/publish competing revisions; stale writes are rejected with a useful diff.
3. A default module upgrade preserves compatible overlays and blocks incompatible changes with a dependency-impact report.
4. A field conversion fails partway through; recovery resumes safely, preserves invalid source values, and does not falsely offer an unsafe rollback.
5. A workflow/approval/SLA definition changes while instances are active; pinned instances or explicit migrations behave as specified.
6. Duplicate and out-of-order events cannot regress current state or create duplicate fulfillment work; ambiguous external effects are reconciled.
7. Access is revoked while a request, cached search, queued job, and AI session exist; the defined revocation behavior holds across each surface.
8. Conflicting mods and uninstall of a depended-on package cannot silently alter behavior or strand records.
9. A preview or rule simulation cannot contact production write endpoints, send real notifications, or execute live schedules.
10. A malicious instruction in a ticket, attachment, retrieved article, or marketplace description cannot authorize a tool action or expose restricted data.
11. An overloaded tenant, connector, or extension cannot exhaust the shared worker pool; quotas, cancellation, and backpressure are observable.
12. A pooled tenant is restored in isolation with attachments and configuration reconciled; other tenants are unaffected and external actions are not blindly replayed.
13. Email loops/duplicate intake, public-versus-private notes, concurrent ticket edits, reopened requests, and daylight-saving SLA transitions behave correctly.
14. Package export contains no tenant records, secrets, production bindings, or private identifiers unless separately authorized and explicitly included.

Each scenario needs an owner, fixture/workload, expected result, evidence, and a release gate. Security/isolation and data-integrity failures block release.

## Phased execution and delivery gates

Implement one validated vertical slice before broadening the catalog. Complete each gate's evidence before starting work that depends on its assumptions.

| Gate | Scope | Exit evidence |
| --- | --- | --- |
| 0 — Decisions | Product inputs; domain boundaries; storage/query model; authorization model; configuration/migration contracts; threat model; build-versus-buy decisions | Recorded assumptions and architecture decisions, testable operating targets, and a prioritized backlog |
| 1 — Architecture proof | Kernel and a minimal Tickets module on the public SDK; two tenants; constrained AI field/rule/page editing; compile/preview/publish; one package upgrade | Working path: install for A/B → customize A → publish → operate records → upgrade → prove isolation/customization → inject migration failure and recover; pass duplicate-event, concurrent-edit, revocation, and package-conflict checks |
| 2 — Private pilot | Complete initial request workflow, email/portal, SLA, basic approvals/tasks, attachment controls, one integration, operator recovery, basic reports | End-to-end acceptance scenarios, workload measurements, tested backups/restore, support runbooks, and pilot-user task completion |
| 3 — Partner beta | Incidents/Knowledge as needed, private modules/mods/config packs, mature SDK/CLI, compatibility and support processes | Independent partner builds a working package without private APIs; upgrade/uninstall/permission-change tests pass |
| 4 — Product expansion | Prioritized Problems/Changes/CRM/Projects/CMDB, advanced reports, collaboration, more connectors/languages, dedicated placement and mobile where justified | Module-specific operational requirements and scale/recovery tests; demonstrated customer demand |
| 5 — Public marketplace | Publisher onboarding, review, listings, licenses, billing/payouts, trust/support, revocation | Validated commercial reconciliation, package lifecycle controls, customer support ownership, and staged rollout process |

Private package installation precedes marketplace commerce. Baseline security and tenant isolation are never deferred to a later gate.

## Deliverables

Produce these incrementally under the gates above. Start with the one-page architecture summary, decision register, and Gate 1 acceptance plan. Deliver the schemas/interfaces required for that gate before implementing it; do not generate every long-term feature at once. Keep a traceable mapping from requirements to implementation and verification evidence.

The complete deliverable set is:

1. Architecture diagram: core kernel, module boundaries, extension runtime, AI/MCP layer, marketplace services
2. Layered metadata data model, typed overlay operations, exact dependency lockfile, conflict detection, and effective-config compilation/activation algorithm
3. Example: tenant A adds a field and a validation rule to Tickets; show the stored overlay, resolved config, and proof tenant B is unaffected
4. Upgrade scenarios: Tickets v2 with tenant overlays, changed field types, active workflows/approvals/SLA timers, partial migration failure, and compatibility-checked recovery
5. Spec schemas for entity, field, relationship, page/layout node, widget, rule, workflow, and automation
6. Full example ticket view: a 3-column row and a 2-column row with cards, as a page spec
7. Element registry schema, with three fully described example elements (one field, one container, one widget)
8. Rule examples: numeric-only field, conditional required (category == 8), conditional visibility
9. Phased MCP tool list with input/output schemas, approval/idempotency/concurrency contracts, and actionable error responses
10. Point-and-prompt example: selected node context payload → MCP calls → canvas preview diff
11. End-to-end chat transcript: AI builds a Projects module linked to Tickets, previews, and publishes
12. WIT interface definitions for core extension points
13. Package manifest schema with examples (module, mod, config pack)
14. Event catalog and execution contracts: naming, schema versions, record revisions, transaction boundaries, deduplication, ordering/gaps, replay, and external-effect reconciliation
15. Design system structure: token schema, component contract, design linting rules
16. Security model and threat scenarios: sandboxing, capabilities, secrets, tenant/environment isolation, derived-data authorization, AI injection resistance, approvals, egress, revocation, and abuse handling
17. Marketplace domain model: publishers, listings, versions, licenses, entitlements, payouts
18. Repo structure and boundary-enforcement tooling
19. Gate-based roadmap: decisions → two-tenant architecture proof → private Tickets/Service Requests pilot → partner beta → prioritized module expansion → public marketplace
20. Key risks and trade-offs, with mitigations
21. Logical/physical record-storage and query model, custom-field index policy, concurrency semantics, and representative query benchmarks
22. Package compatibility/dependency matrix, exclusive-versus-composable extension contracts, and disable/uninstall/license-expiry impact plans
23. Operating targets, workload definitions, acceptance fixtures, AI evaluation suite, and measured gate evidence
24. Backup and recovery designs for cluster/dedicated failures and single pooled-tenant restoration, including attachments and external-effect reconciliation
25. Operator runbooks for failed jobs, controlled replay, migration recovery, package isolation, access revocation, and tenant offboarding

## Technical references

Use these primary references when validating implementation decisions. Pin actual versions and recheck toolchain capabilities at implementation time; links are not a substitute for conformance and integration tests.

- [PostgreSQL row security](https://www.postgresql.org/docs/current/ddl-rowsecurity.html): runtime roles, ownership, BYPASSRLS, and FORCE ROW LEVEL SECURITY.
- [PostgreSQL continuous archiving and PITR](https://www.postgresql.org/docs/current/continuous-archiving.html): physical recovery scope and requirements.
- [MDN: Using Shadow DOM](https://developer.mozilla.org/en-US/docs/Web/API/Web_components/Using_shadow_DOM) and [iframe sandboxing](https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/iframe): distinguish component encapsulation from an isolation boundary.
- [WASI releases and toolchain guidance](https://wasi.dev/releases): choose and verify supported component interfaces and language/runtime combinations.
- [Transactional outbox pattern](https://microservices.io/patterns/data/transactional-outbox.html): duplicate publication and idempotent consumer requirements.
- [MCP authorization](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization): intended token audiences and separation of downstream credentials; verify the supported protocol version when implementing.
