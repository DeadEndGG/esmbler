---
title: Traceability matrix
status: living
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [all]
---

# Traceability matrix

Maps each master-spec requirement to its gate, implementation, verification, and evidence. Requirement IDs are referenced in code comments, test names or doc comments, and commit messages. Status values: `planned`, `in-progress`, `done` (implemented and verified), `deferred` (moved to a later gate with a reason).

Source sections refer to `docs/spec/master-spec.md` (MS) and `docs/spec/execution-brief.md` (EB).

## Tenancy and isolation

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-TEN-001 | Tenant/environment scope on every record and derived data; Postgres RLS with FORCE, non-owner runtime role, transaction-scoped context | MS Multi-tenancy | 1 | packages/Esmbler.Kernel.Infrastructure/Migrations/*.sql, packages/Esmbler.Kernel.Infrastructure/Persistence/Database.cs | IntegrationTests/Isolation/DatabaseIsolationTests (5) | done |
| REQ-TEN-002 | A change made by one tenant never affects another tenant (config, data, caches, jobs) | MS Multi-tenancy | 1 | packages/Esmbler.Kernel.Infrastructure/Configuration/*, packages/Esmbler.Kernel.Infrastructure/Identity/AccessService.cs (cache keyed by tenant) | IntegrationTests/Acceptance/Gate1PathTests; ConfigLifecycleTests.Tenant_A_customization_leaves_tenant_B_unchanged; e2e gate1.spec.ts @REQ-ACC-01 | done |
| REQ-TEN-003 | Tenant/environment switching is explicit and authorized; never inferred from record IDs or tool arguments | MS Domain boundaries | 1 | apps/Esmbler.Api/Endpoints/AuthEndpoints.cs, packages/Esmbler.Hosting/Auth/RequestContextFactory.cs | IntegrationTests/Api/ApiTests.Switching_to_a_tenant_you_do_not_belong_to_is_forbidden, Requests_without_a_selected_workspace_are_refused | done |
| REQ-TEN-004 | Per-tenant environments (sandbox, prod) with promotion and rollback of overlay changes | MS Multi-tenancy | 2 | | | planned |
| REQ-TEN-005 | Sandbox data synthetic; production credentials, destinations, schedules never copied | MS Environment safety | 2 | | | planned |
| REQ-TEN-006 | Per-tenant rate limits, quotas, and fair scheduling | MS Multi-tenancy | 2 | packages/Esmbler.Kernel.Infrastructure/Migrations/0005_durable_jobs.sql (claim_outbox), claim_jobs, Events/Jobs.cs (quota) | IntegrationTests/Events/DurableJobsTests (fairness); per-tenant API rate limit is KI-010 | in-progress |
| REQ-TEN-007 | Dedicated database/region tier without code changes | MS Tenancy tiers | 4 | | | planned |

## Configuration composition and deployment

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-CFG-001 | Layered metadata: platform base → packages → tenant overlay resolved into effective config | MS Multi-tenancy | 1 | packages/Esmbler.Kernel/Configuration/Composer.cs | UnitTests/Configuration/OverlayTests | done |
| REQ-CFG-002 | Overlays are typed operations against stable IDs with expected prior values; no blind JSON merge | MS Config composition | 1 | packages/Esmbler.Sdk/Specs, Overlay | UnitTests/Configuration/* | done |
| REQ-CFG-003 | Namespacing (core.*, publisher.*, tenant__*) | MS Multi-tenancy | 1 | packages/Esmbler.Kernel/Configuration/Composer.cs (provenance, customized) | UnitTests/Configuration/OverlayTests.Canonical_customization_compiles_and_records_provenance | done |
| REQ-CFG-004 | Dependency graph over schema, rules, pages, workflows, policies | MS Config composition | 1 | packages/Esmbler.Kernel/Configuration/Composer.cs (expected values) | UnitTests/Configuration/UpgradeTests | done |
| REQ-CFG-005 | Validation before activation (types, references, containment, expression cost, cycles, migrations) | MS Config composition | 1 | packages/Esmbler.Kernel/Configuration/DependencyGraph.cs | UnitTests/Configuration/OverlayTests; IntegrationTests/ConfigLifecycleTests.Invalid_operations_are_refused_with_actionable_errors_and_undo_respects_dependencies | done |
| REQ-CFG-006 | Compiled immutable artifact with content hash, lockfile, compat, migrations, permission impact, provenance | MS Config composition | 1 | packages/Esmbler.Kernel/Configuration/ConfigCompiler.cs, packages/Esmbler.Kernel.Infrastructure/Configuration/DraftService.cs | IntegrationTests/Configuration/ConfigLifecycleTests | done |
| REQ-CFG-007 | Publish with optimistic concurrency; approval bound to artifact hash, tenant/environment, permission context | MS Config composition | 1 | packages/Esmbler.Kernel.Infrastructure/Configuration/PublishService.cs | IntegrationTests/ConfigLifecycleTests.Competing_edits_and_stale_publishes_are_rejected_with_details, Agents_request_publish_but_only_humans_publish_and_permissions_need_admin | done |
| REQ-CFG-008 | Each request pins one config revision; incompatible older revisions get an actionable conflict | MS Config composition | 1 | packages/Esmbler.Kernel.Infrastructure/Records/RecordService.cs (config_conflict) | IntegrationTests/ConfigLifecycleTests.Form_submitted_under_an_incompatible_older_revision_gets_a_conflict | done |
| REQ-CFG-009 | Upgrades compose candidate base under overlays; conflicts never silently resolved | MS Multi-tenancy | 1 | packages/Esmbler.Kernel.Infrastructure/Configuration/UpgradeService.cs | IntegrationTests/Configuration/UpgradeTests.Conflicting_customization_blocks_the_upgrade_with_an_impact_report; e2e @REQ-ACC-03 | done |
| REQ-CFG-010 | Preserve previous artifact; health check after activation; compatibility-checked rollback | MS Config composition | 1 | packages/Esmbler.Kernel.Infrastructure/Configuration/ConfigActivator.cs, ActivationHealthCheck.cs, PublishService.RollbackAsync | IntegrationTests/Configuration/ActivationHealthTests; ConfigLifecycleTests.Rollback_restores_an_earlier_configuration_as_a_new_revision | done |
| REQ-CFG-011 | Visible customizations, compare with default, reset to default with impact preview | MS Default modules | 1 | packages/Esmbler.Sdk/Overlay/OverlayOps.cs (ResetElement), PreviewService.CustomizationsAsync, MCP reset_to_default | UnitTests/Configuration/OverlayTests.Reset_*; IntegrationTests/Configuration/ResetToDefaultTests | done |

## Records, queries, and schema evolution

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-REC-001 | Shared physical storage for custom fields (no per-tenant DDL); documented index policy | MS Multi-tenancy, Records | 1 | packages/Esmbler.Kernel.Infrastructure/Migrations/0001_kernel.sql (kernel.records jsonb, GIN, tsvector) | IntegrationTests/Records/QueryBenchmarkTests | done |
| REQ-REC-002 | Typed query contract (filter, sort, paging) with bounded cost | MS Records | 1 | packages/Esmbler.Kernel.Infrastructure/Records/QueryCompiler.cs | IntegrationTests/Records/RecordServiceTests.Query_filters_sort_and_search_respect_semantics; Records/QueryBenchmarkTests (100k tickets) | done |
| REQ-REC-003 | Authorization applied before paging and counting; no leaks via counts, lookups, errors | MS Records | 1 | packages/Esmbler.Kernel.Infrastructure/Records/QueryCompiler.cs (permission predicate in SQL) | IntegrationTests/Records/RecordServiceTests.Requester_sees_only_related_tickets_and_no_hidden_fields | done |
| REQ-REC-004 | Record revisions for optimistic concurrency; idempotency keys for retried commands | MS Records | 1 | packages/Esmbler.Kernel.Infrastructure/Records/RecordService.cs | IntegrationTests/Records/RecordServiceTests.Create_update_conflict_and_history, Idempotency_key_replays_the_same_result | done |
| REQ-REC-005 | Precise data types (decimal scale, UTC instants vs local dates, null vs absent, stable choice IDs) | MS Records | 1 | packages/Esmbler.Kernel/Records/FieldNormalizer.cs | UnitTests/Records/WritePipelineTests | done |
| REQ-REC-006 | Referential integrity, deletion restriction, soft delete and restore | MS Records | 1 | packages/Esmbler.Kernel.Infrastructure/Records/RecordService.cs (DeleteAsync, RestoreAsync) | IntegrationTests/Records/RecordServiceTests.Deleted_records_can_be_restored_by_people_who_may_delete_them | done |
| REQ-REC-007 | Audit history of every change with actor, agent, and config revision | MS Kernel | 1 | packages/Esmbler.Kernel.Infrastructure/Records/RecordService.cs, Services/AuditWriter.cs | IntegrationTests/Events/EventDeliveryTests.Record_change_audit_and_event_commit_together | done |
| REQ-MIG-001 | Schema change classification and migration plans (expand/backfill/contract) | MS Migration | 1 | packages/Esmbler.Kernel.Infrastructure/Configuration/UpgradeService.cs, MigrationExecutor.cs | IntegrationTests/Configuration/UpgradeTests (classification partial, KI-003) | in-progress |
| REQ-MIG-002 | Failed/partial migrations resume safely, preserve invalid values, block unsafe rollback | MS Migration | 1 | packages/Esmbler.Kernel.Infrastructure/Configuration/MigrationExecutor.cs | IntegrationTests/Configuration/UpgradeTests.Upgrade_preserves_customizations_and_recovers_from_a_partial_migration_failure | done |
| REQ-MIG-003 | Workflow definitions versioned; active instances pinned or explicitly migrated | MS Migration | 2 | | | planned |
| REQ-MIG-004 | SLA calendars/policies versioned; edits do not corrupt running timers or history | MS Migration | 2 | | | planned |

## Events and durable execution

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-EVT-001 | Record change, audit entry, and outbox event commit in one transaction | MS Transactions | 1 | packages/Esmbler.Kernel.Infrastructure/Records/RecordService.cs (outbox in transaction) | IntegrationTests/Events/EventDeliveryTests.Record_change_audit_and_event_commit_together | done |
| REQ-EVT-002 | At-least-once delivery with durable inbox deduplication and idempotent handlers | MS Transactions | 1 | packages/Esmbler.Kernel.Infrastructure/Events/OutboxDispatcher.cs (inbox) | IntegrationTests/Events/EventDeliveryTests.Resolving_notifies_the_requester_once_even_when_redelivered | done |
| REQ-EVT-003 | Event envelope fields (IDs, tenant/env, revision, schema version, config ref, correlation/causation) | MS Transactions | 1 | packages/Esmbler.Sdk/Events/EventEnvelope.cs | IntegrationTests/Events/EventDeliveryTests.Record_change_audit_and_event_commit_together | done |
| REQ-EVT-004 | Per-record ordering; projections reject stale revisions | MS Transactions | 1 | packages/Esmbler.Kernel.Infrastructure/Events/RecordStateProjection.cs | IntegrationTests/Events/EventDeliveryTests.Projection_ignores_stale_and_duplicate_revisions | done |
| REQ-EVT-005 | Durable jobs and timers with leases, backoff, max attempts, dead letters, replay | MS Transactions | 2 | packages/Esmbler.Kernel.Infrastructure/Events/Jobs.cs, JobRunner.cs, OperationsService.cs | IntegrationTests/Events/DurableJobsTests | done |
| REQ-EVT-006 | Automation cycle detection, execution budgets, fan-out limits | MS Transactions | 2 | packages/Esmbler.Kernel.Infrastructure/Events/Jobs.cs (JobScheduler governors) | IntegrationTests/Events/DurableJobsTests.Automation_depth_and_fan_out_are_bounded_and_audited | done |
| REQ-EVT-007 | External side effects idempotent or reconciled | MS Transactions | 2 | | | planned |

## Identity and authorization

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-IAM-001 | Principals: users, groups, service accounts, package principals, AI agents (auditable) | MS Identity | 1 | packages/Esmbler.Kernel.Infrastructure/Identity/IdentityService.cs (users, groups, agents) | IntegrationTests/Api/ApiTests; Mcp/McpServerTests.Disabling_the_agent_stops_its_next_call | done |
| REQ-IAM-002 | A person may belong to multiple tenants | MS Domain boundaries | 1 | packages/Esmbler.Kernel.Infrastructure/Identity/IdentityService.TenantsForAsync | IntegrationTests/Records/RecordServiceTests.Tenants_are_isolated_through_the_service_layer | done |
| REQ-IAM-003 | SSO (OIDC/SAML), SCIM, MFA, passkeys, per-tenant IdPs | MS Identity | 2 | | | planned |
| REQ-AUTHZ-001 | Hybrid RBAC + ABAC + ReBAC with a justified engine | MS Authorization | 0 | docs/adr/0004-authorization-engine.md | spikes/authz-openfga | done |
| REQ-AUTHZ-002 | Four levels: entity, record, field, action | MS Authorization | 1 | packages/Esmbler.Kernel/Authorization/PolicyDecisionPoint.cs | UnitTests/Records/WritePipelineTests.Record_level_access_follows_relationships | done |
| REQ-AUTHZ-003 | Policies are specs with layering, versioning, draft, rollback | MS Authorization | 1 | packages/Esmbler.Sdk/Specs (permission sets), overlay permission.field_access | IntegrationTests/ConfigLifecycleTests.Agents_request_publish_but_only_humans_publish_and_permissions_need_admin; Rollback_restores_an_earlier_configuration_as_a_new_revision | done |
| REQ-AUTHZ-004 | UI follows permissions automatically; hidden fields never sent to clients | MS Authorization | 1 | packages/Esmbler.Kernel/Authorization/PolicyDecisionPoint.cs (field level) | IntegrationTests/Api/ApiTests.Requester_view_omits_hidden_fields_and_explain_says_why | done |
| REQ-AUTHZ-005 | Permission-aware list filtering (pre-filtered) | MS Authorization | 1 | packages/Esmbler.Kernel.Infrastructure/Records/QueryCompiler.cs | IntegrationTests/Records/RecordServiceTests.Requester_sees_only_related_tickets_and_no_hidden_fields; QueryBenchmarkTests | done |
| REQ-AUTHZ-006 | Revocation propagation with bounded staleness; fail closed | MS Authorization | 1 | packages/Esmbler.Kernel.Infrastructure/Migrations/0003_authz_revision.sql, Identity/AccessService.cs, Events/JobRunner.cs | IntegrationTests/Identity/RevocationTests (4) | done |
| REQ-AUTHZ-007 | AI acts with intersection of user permissions and delegated scopes; cannot self-elevate | MS AI, packages | 1 | packages/Esmbler.Kernel/Authorization/PolicyDecisionPoint.cs (agent intersection) | UnitTests/Records/WritePipelineTests.Agent_gets_intersection_of_user_permissions_and_scopes; ArchitectureTests Assistant_changes_workspaces_only_through_mcp | done |
| REQ-AUTHZ-008 | Permission changes need a distinct permissions.admin action | MS AI, packages | 1 | packages/Esmbler.Kernel.Infrastructure/Configuration/PublishService.cs | IntegrationTests/ConfigLifecycleTests.Agents_request_publish_but_only_humans_publish_and_permissions_need_admin | done |
| REQ-AUTHZ-009 | Explain access ("why can Maria see this ticket?") via API and MCP | MS Explainability | 1 | packages/Esmbler.Kernel.Infrastructure/Identity/AccessExplainer.cs, MCP explain_access | IntegrationTests/Api/ApiTests.Requester_view_omits_hidden_fields_and_explain_says_why; Mcp/McpServerTests.Explain_access_tool_says_why_a_field_is_hidden | done |
| REQ-AUTHZ-010 | Linking records never implicitly grants access | MS Authorization | 1 | packages/Esmbler.Kernel/Authorization/PolicyDecisionPoint.cs (access only via declared relationship fields) | IntegrationTests/Records/RecordServiceTests.Requester_cannot_read_others_tickets_by_id | done |
| REQ-AUTHZ-011 | Governance: access reviews, JIT elevation, delegation, break-glass, separation of duties | MS Governance | 2 | | | planned |

## AI builder and MCP

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-AI-001 | Spec-driven: everything is declarative JSON validated against JSON Schema | MS AI builder | 1 | packages/Esmbler.Sdk/Schemas, docs/reference/schemas (generated) | UnitTests/Configuration/*; DocGen --check in CI | done |
| REQ-AI-002 | Self-describing element registry readable by the AI | MS AI builder | 0 | packages/Esmbler.Sdk/Registry, docs/reference/element-catalog.md (generated) | TicketsReleaseTests.Registry_loads_gate1_catalog | done |
| REQ-AI-003 | MCP resources and discovery, build, review, lifecycle tools | MS AI builder | 1 | apps/Esmbler.Mcp/Tools/* | IntegrationTests/Mcp/McpServerTests.Server_lists_the_builder_tools_and_no_publish_tool | done |
| REQ-AI-004 | Precise, actionable errors so the AI can self-correct | MS AI builder | 1 | apps/Esmbler.Mcp/Tools/ToolRunner.cs (structured errors) | IntegrationTests/Mcp/McpServerTests.Scopes_limit_the_agent_and_errors_are_structured; Api/ApiTests.Validation_errors_are_actionable_problem_details | done |
| REQ-AI-005 | Draft workspaces; human reviews diff and preview before publish | MS AI builder | 1 | packages/Esmbler.Assistant, apps/Esmbler.Api/Assistant/AssistantService.cs | IntegrationTests/Assistant/AssistantTests.Api_streams_progress_and_the_change_lands_in_the_draft; Acceptance/Gate1PathTests | done |
| REQ-AI-006 | MCP context bound by authentication; no tenant/environment override; audit actor, agent, target, revision, outcome | MS AI builder | 1 | packages/Esmbler.Hosting/Auth/AuthRegistration.cs (audiences) | IntegrationTests/Mcp/McpServerTests.Api_tokens_are_rejected_by_the_mcp_server | done |
| REQ-AI-007 | Clarify at most twice; plan big changes; explain changes in plain English including rule scope | EB 3 | 1 | packages/Esmbler.Assistant/ScriptedPlanner.cs (plan, question with options), ClaudePlanner system prompt | UnitTests/Assistant/ScriptedPlannerTests; IntegrationTests/Assistant/AssistantTests (ambiguous-asks) | done |
| REQ-AI-008 | Escape hatch: generated code goes through the package pipeline | MS AI builder | 3 | | | planned |
| REQ-AI-009 | Chat to package export without tenant records or secrets | MS AI builder | 3 | | | planned |
| REQ-AISAFE-001 | Untrusted content cannot expand tool authority (prompt injection) | MS AI safeguards | 1 | apps/Esmbler.Mcp/Tools/ToolRunner.cs (untrusted wrapper) | IntegrationTests/Mcp/McpServerTests.Embedded_instructions_in_records_cannot_widen_authority | done |
| REQ-AISAFE-002 | Simulations/previews have no external side effects | MS AI safeguards | 1 | packages/Esmbler.Kernel.Infrastructure/Configuration/PreviewService.cs | IntegrationTests/Acceptance/PreviewSafetyTests | done |
| REQ-AISAFE-003 | Bounded tool calls, duration, context, spend; cancellation and per-agent disable | MS AI safeguards | 1 | ToolRunner budgets, AssistantBudget, IdentityService.SetAgentDisabledAsync | IntegrationTests/Mcp/McpServerTests.Tool_calls_are_rate_limited_per_agent_session, Disabling_the_agent_stops_its_next_call (daily spend budget: KI-002) | done |
| REQ-AISAFE-004 | MCP token audience validation; no token passthrough | MS AI safeguards | 1 | packages/Esmbler.Hosting/Auth/TokenService.cs | IntegrationTests/Api/ApiTests.Agent_tokens_are_rejected_by_the_api_audience | done |
| REQ-AISAFE-005 | AI evaluation suite (task completion, unintended effects, injection, unsupported ops) | MS AI safeguards, EB 3 | 1 | tests/ai-evals/builder-cases.json | IntegrationTests/Assistant/AssistantTests (43 cases incl. malicious, policy, unsupported; Claude scoring needs a key, KI-007) | done |

## Layout, live editing, rules, design system

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-LAY-001 | Page tree Page → Section → Row → Column → Container → Element on a 12-column grid with stable IDs | MS Layout | 1 | apps/web/src/app/renderer/* | e2e gate1.spec.ts; a11y.spec.ts | done |
| REQ-LAY-002 | Container types declare allowed children; invalid layouts rejected | MS Layout | 1 | packages/Esmbler.Kernel/Configuration/ConfigValidator.cs (containment) | UnitTests/Configuration/OverlayTests.Card_inside_a_row_is_rejected_by_containment_rules | done |
| REQ-LAY-003 | Point-and-prompt: selection attached to chat as structured context | MS Visual builder | 1 | apps/web/src/app/builder/*, renderer/node.ts, pages/record.page.ts | e2e gate1.spec.ts @REQ-LIVE-003 @REQ-LAY-003 | done |
| REQ-LAY-004 | All edits are Commands (undo/redo, diff, audit); drag-and-drop and AI edits interchangeable | MS Visual builder | 1 | packages/Esmbler.Kernel.Infrastructure/Configuration/DraftService.cs | IntegrationTests/ConfigLifecycleTests.Invalid_operations_are_refused_with_actionable_errors_and_undo_respects_dependencies | done |
| REQ-LIVE-001 | Chat docked beside the real page; draft mode with sample data | EB 3 | 1 | apps/web/src/app/builder/chat-panel.ts, pages/record.page.ts (draft mode) | e2e gate1.spec.ts @REQ-ACC-01 | done |
| REQ-LIVE-002 | Each AI change visible on the page within 1 s of its tool call (p95) | EB 3 | 1 | apps/Esmbler.Api/Endpoints/LiveHub.cs, core/live.ts | IntegrationTests/Api/LiveChannelTests; e2e experience.spec.ts (p95 step visible < 1 s) | done |
| REQ-LIVE-003 | Chat steps link to page elements and back; stop, undo one step, accept or discard draft | EB 3 | 1 | builder/chat-panel.ts (steps, undo, redo, stop), builder-state.ts | e2e gate1.spec.ts @REQ-LIVE-003 | done |
| REQ-LIVE-004 | Version checks prevent human and AI edits overwriting each other; other users see published page | EB 3 | 1 | packages/Esmbler.Kernel.Infrastructure/Configuration/DraftService.cs (versions, draft_conflict) | IntegrationTests/ConfigLifecycleTests.Competing_edits_and_stale_publishes_are_rejected_with_details; Api/LiveChannelTests.Requesters_cannot_watch_drafts | done |
| REQ-RULE-001 | Field validation (required, type, min/max, length, regex, allowed values, uniqueness) | MS Rules | 1 | packages/Esmbler.Platform/Rules/* | UnitTests/Records/WritePipelineTests | done |
| REQ-RULE-002 | Conditional rules (require, show/hide, read-only, set value, restrict options, message) | MS Rules | 1 | packages/Esmbler.Platform/Rules/RulesHook.cs (every write path) | UnitTests/Records/WritePipelineTests.Tenant_conditional_rule_requires_justification_for_access_requests; e2e @REQ-ACC-01 | done |
| REQ-RULE-003 | Safe non-Turing-complete expressions; server/client conformance | MS Rules | 1 | packages/Esmbler.Expressions, apps/web/src/app/core/expressions | UnitTests/Expressions/ConformanceTests; web expressions.spec.ts | done |
| REQ-RULE-004 | Data-level rules enforced on every write path; form-level per view | MS Rules | 1 | packages/Esmbler.Platform/Rules/RuleEvaluator.cs (ordering) | UnitTests/Records/WritePipelineTests | done |
| REQ-RULE-005 | Rule ordering, conflict and cycle detection, simulate tool | MS Rules | 1 | packages/Esmbler.Kernel.Infrastructure/Configuration/PreviewService.SimulateAsync, Composer cycle detection | UnitTests/Configuration/OverlayTests.Set_value_cycles_are_detected; IntegrationTests/Acceptance/PreviewSafetyTests | done |
| REQ-RULE-006 | Hidden field values never sent to the client to evaluate a rule | MS Rules | 1 | packages/Esmbler.Kernel.Infrastructure/Records/RecordService.cs (server field states), ExperienceService.ForEntity (hidden fields pruned) | IntegrationTests/Api/ApiTests.Requester_view_omits_hidden_fields_and_explain_says_why | done |
| REQ-DS-001 | Specs reference tokens and components only; no raw styling | MS Design system | 1 | apps/web/src/styles/tokens.css, apps/web/src/styles.css (Tailwind theme, ADR 0014), registry token lists | Registry schema validation (UnitTests); design lint in CI is KI-005 | in-progress |
| REQ-DS-002 | Linear/Notion-inspired UX: command palette, keyboard-first, slash menu, drag handles, inline editing, dark mode | EB 3 | 1 | apps/web/src/app/shell/command-palette.ts, core/shortcuts.ts, renderer inline editing, slash menu, drag handles | e2e gate1.spec.ts @REQ-DS-002 | done |
| REQ-DS-003 | WCAG 2.2 AA, keyboard navigation, screen readers, translatable labels | MS Design system | 1 | apps/web (ARIA roles, keyboard alternatives) | e2e a11y.spec.ts (axe WCAG 2.2 AA, no serious/critical) | done |
| REQ-DS-004 | Bounded tenant theming with contrast checks | MS Design system | 2 | | | planned |
| REQ-EL-001 | Gate 1 element subset fully described in the registry | EB 5 | 0 | packages/Esmbler.Sdk/Registry/Elements (60 elements) | docs/reference/element-catalog.md | done |

## Modules, packages, and extension runtime

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-MOD-001 | Default Tickets module ships complete and is built only on the public SDK | MS Default modules | 1 | modules/tickets (public SDK only) | UnitTests/Configuration/TicketsReleaseTests; ArchitectureTests | done |
| REQ-MOD-002 | Module boundaries enforced by architecture tests; no cross-module data access | MS Architecture, EB 2 | 1 | tests/Esmbler.ArchitectureTests/BoundaryTests.cs | BoundaryTests (5) | done |
| REQ-MOD-003 | Incidents and Knowledge modules | MS Default modules | 3 | | | planned |
| REQ-MOD-004 | Problems, Changes, CRM (support context), Projects, CMDB | MS Default modules | 4 | | | planned |
| REQ-PKG-001 | Package types (module, mod, config pack) and manifest | MS Packages | 3 | | | planned |
| REQ-PKG-002 | Extension points with declared cardinality; exclusive strategies conflict explicitly | MS Packages | 3 | | | planned |
| REQ-PKG-003 | Lockfile, compatibility checks, uninstall/disable plans that never strand work | MS Packages | 3 | | | planned |
| REQ-EXT-001 | Wasm Component Model sandbox with WIT interfaces and pinned Wasmtime | MS Architecture | 0 (spike), 3 | docs/adr/0006-extension-runtime.md | spikes/extension-runtime (Gate 3 implementation pending) | in-progress |
| REQ-EXT-002 | Capability-based permissions and CPU/memory/time limits per tenant/environment | MS Architecture | 3 | | | planned |
| REQ-EXT-003 | Tested language/runtime compatibility matrix; one toolchain first | MS Architecture | 0 (spike), 3 | docs/adr/0006-extension-runtime.md (matrix) | spikes/extension-runtime/README.md | in-progress |
| REQ-EXT-004 | UI extensions in sandboxed iframes with validated postMessage bridge | MS Architecture | 3 | | | planned |

## Business behavior, admin, operations, marketplace

| ID | Requirement | Source | Gate | Code | Tests / evidence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| REQ-BIZ-001 | Ticket lifecycle: ownership, parent/child, reopen, cancel, concurrent edits, audit | MS Business behavior | 1 | packages/Esmbler.Platform/Workflow/WorkflowHook.cs | IntegrationTests/Records/RecordServiceTests.Transition_flow_resolve_and_confirm_by_requester | done |
| REQ-BIZ-002 | Conversations: public replies vs internal work notes with visibility checks | MS Business behavior | 1 | packages/Esmbler.Kernel.Infrastructure/Records/RecordService.cs (activity visibility) | IntegrationTests/Records/RecordServiceTests.Work_notes_are_hidden_from_requesters | done |
| REQ-BIZ-003 | Email intake (threading, sender policy, dedup, loop prevention) | MS Business behavior | 2 | | | planned |
| REQ-BIZ-004 | Requests, catalog, approvals, fulfillment tasks | MS Business behavior | 2 | | | planned |
| REQ-BIZ-005 | SLA and business hours (pause/resume, time zones, DST, holidays) | MS Business behavior | 2 | | | planned |
| REQ-BIZ-006 | Attachments with object-level authorization, scanning, limits | MS Business behavior | 2 | | | planned |
| REQ-BIZ-007 | Knowledge lifecycle and permission-aware retrieval | MS Business behavior | 3 | | | planned |
| REQ-ADM-001 | Admin Center with auto-generated package panels from settings schema | MS Admin Center | 2 | | | planned |
| REQ-ADM-002 | Secrets as references in a vault; never exposed in UI, MCP, logs, exports | MS Admin Center | 2 | | | planned |
| REQ-OPS-001 | Scoped observability (logs, metrics, traces, audit) | MS Platform essentials | 2 | | | planned |
| REQ-OPS-002 | Backups and isolated pooled-tenant restore | MS Platform essentials | 2 | | | planned |
| REQ-OPS-003 | Platform limits (governors) with clear errors | MS Platform essentials | 2 | | | planned |
| REQ-OPS-004 | Basic reports over standard and custom fields | MS First release | 2 | | | planned |
| REQ-OPS-005 | One validated integration and outbound webhooks | MS First release | 2 | | | planned |
| REQ-OPS-006 | Containerized: one `docker compose up` starts the system with seed data | EB 2 | 0 | deploy/docker-compose.yml, deploy/docker/* | docs/testing/gate-0-test-guide.md scenario 1 | done |
| REQ-MKT-001 | Publisher onboarding, review pipeline, listings | MS Marketplace | 5 | | | planned |
| REQ-MKT-002 | Licenses, billing, payouts, revocation | MS Marketplace | 5 | | | planned |

## Acceptance scenarios

| ID | Scenario (MS "Acceptance scenarios") | Gate | Tests / evidence | Status |
| --- | --- | --- | --- | --- |
| REQ-ACC-01 | Tenant A adds field, rule, page card; tenant B unchanged | 1 | IntegrationTests/Acceptance/Gate1PathTests; ConfigLifecycleTests.Tenant_A_customization_leaves_tenant_B_unchanged; e2e @REQ-ACC-01 | done |
| REQ-ACC-02 | Competing builder revisions; stale writes rejected with diff | 1 | IntegrationTests/ConfigLifecycleTests.Competing_edits_and_stale_publishes_are_rejected_with_details; e2e @REQ-ACC-02 | done |
| REQ-ACC-03 | Module upgrade preserves compatible overlays, blocks incompatible with impact report | 1 | IntegrationTests/Configuration/UpgradeTests; UnitTests/Configuration/UpgradeTests; e2e @REQ-ACC-03 | done |
| REQ-ACC-04 | Field conversion fails partway; safe resume, invalid values preserved, no unsafe rollback | 1 | IntegrationTests/Configuration/UpgradeTests.Upgrade_preserves_customizations_and_recovers_from_a_partial_migration_failure | done |
| REQ-ACC-05 | Workflow/approval/SLA definition change with active instances | 2 | | planned |
| REQ-ACC-06 | Duplicate/out-of-order events cannot regress state or duplicate work | 1 (events), 2 (fulfillment) | IntegrationTests/Events/EventDeliveryTests (Gate 1 events part; fulfillment in Gate 2) | in-progress |
| REQ-ACC-07 | Revocation across request, cached search, queued job, AI session | 1 | IntegrationTests/Identity/RevocationTests (4) | done |
| REQ-ACC-08 | Conflicting mods and uninstall of depended-on package | 3 | | planned |
| REQ-ACC-09 | Preview/simulation cannot reach production endpoints or send notifications | 1 | IntegrationTests/Acceptance/PreviewSafetyTests; e2e @REQ-ACC-09 | done |
| REQ-ACC-10 | Malicious embedded instructions cannot authorize tool actions or expose data | 1 | IntegrationTests/Mcp/McpServerTests.Embedded_instructions_in_records_cannot_widen_authority; AI eval malicious cases | done |
| REQ-ACC-11 | Overloaded tenant/connector/extension cannot exhaust shared workers | 2 | | planned |
| REQ-ACC-12 | Pooled tenant restored in isolation | 2 | | planned |
| REQ-ACC-13 | Email loops, public vs private notes, concurrent edits, reopen, DST SLA | 2 | | planned |
| REQ-ACC-14 | Package export excludes tenant records, secrets, bindings | 3 | | planned |
