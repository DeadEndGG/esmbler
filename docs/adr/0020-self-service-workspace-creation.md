---
title: "ADR 0020: Self-service workspace creation"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-WS-001, REQ-WS-002, REQ-WS-006, REQ-ACC-15, REQ-TEN-001, REQ-TEN-006]
---

# ADR 0020: Self-service workspace creation

## Context and problem statement

The product owner's review ([connected workspace](../spec/connected-workspace.md)) asks for a first-class "Create a workspace" flow (REQ-WS-001). A new admin describes what they want, starts from a complete starter such as Ticketing, answers only the questions that have no sensible default, and gets connected forms, explorer, dashboard, navigation and permissions (REQ-WS-002). A new workspace must show guidance and clearly labeled preview data, never figures that look real (REQ-WS-006). REQ-ACC-15 tests the whole path.

Until now only operators and the demo seed created tenants (`ProvisioningService`, ASM-043). Letting any signed-in person create a tenant is a lasting decision, so self-service creation is off unless an operator turns it on. Tenants cost database rows, an OpenFGA store and installed configuration. Once people rely on self-service, taking it away is hard.

Who may create a workspace, what goes into it, how is creation bounded, and what does an empty workspace show?

## Decision drivers

- No code and no schema knowledge: the module already connects its surfaces, so the flow should reuse that, not wire anything by hand.
- Ask only what has no default.
- Tenant isolation stays as it is: a new workspace is an ordinary tenant with RLS.
- Bounded resource use per person, enforced atomically, and an operator switch that keeps creation off until an operator decides to offer it.
- AI agents never create workspaces.
- Empty states are honest: no sample records that look like real tickets, no zeros that look like a broken dashboard.

## Considered options

1. **Module-declared starters; a signed-in person creates a workspace from one; the latest release is installed; a per-person limit is enforced in a `SECURITY DEFINER` function.**
2. Operator-only provisioning (the status quo), with invitations into workspaces an operator creates.
3. AI-planned creation: the assistant builds the workspace from nothing through MCP build tools.
4. Clone a template workspace (copy its configuration and sample records).

## Decision outcome

**Chosen option 1.**

### Starters in the SDK

`StarterSpec` (`packages/Esmbler.Sdk/Modules/Starters.cs`) describes a complete workspace a module offers: a stable `Id`, a name, a description, lowercase `Keywords`, and the `Groups` (teams) to create. `StarterGroupSpec` has a group `Key` that must match the module's assignment rules, a default `Name`, the `Roles` its members hold, `CreatorJoins`, and an optional `Question` (with `Help` and an `Example` that is never used as a value). A group with a question has no sensible default name, so the flow asks for it.

`IModuleDefinition.Starters` defaults to empty. Tickets declares the "IT ticketing" starter (`TicketsModule.cs`): `service_desk` (asked: "Which team receives new tickets?"; the creator joins) and `network` (named Network). Both hold the Tickets agent role.

A starter adds no configuration of its own. Creating a workspace installs the module's latest release, which already holds the forms, detail pages, explorers, dashboards, navigation, roles and catalog.

### Generated review

`WorkspaceService.Starters()` lists each starter with the surfaces its module's latest release generates: forms and detail pages, explorers, dashboards (with their metric labels), navigation, permissions (roles with descriptions) and, when present, the service catalog. The web review step shows this list. It is derived from the release on every call, so it cannot drift from what is installed.

### Creating a workspace

`WorkspaceService.CreateAsync`:

1. Refuses when creation is turned off (`Esmbler:AllowWorkspaceCreation`, default false; self-service creation is an operator decision).
2. Removes Unicode control and format characters (such as bidi overrides) from the name and the answers, then validates the name (2 to 60 characters), the starter ID and every required answer (1 to 60 characters). An unknown starter ID is truncated to 40 characters in the error message.
3. Builds a slug from the name plus five random characters (`riverside-it-k3x9q`).
4. Provisions the tenant with no sample data (`TenantSeed.SampleData = false`), the starter's teams named from the answers, and the creator attached by principal ID (`UserSeed.Principal`, no email lookup and no new account) as tenant administrator and member of the teams marked `CreatorJoins`.
5. Installs the latest release in production and sandbox and activates it as the first configuration revision.
6. Writes the audit entry `workspace.create` (starter, module, team keys) in the new tenant.
7. Marks the workspace active through `kernel.activate_workspace`. Only then does it appear in the creator's workspace list.

The creator's principal comes only from the authenticated token. The browser then switches into the new workspace explicitly (REQ-TEN-003) and opens home, which is the Overview dashboard.

### The per-person limit in the database

Migration `0012_workspace_creation.sql` adds `kernel.tenants.created_by` and the function `kernel.create_workspace(id, slug, name, creator, limit)`. It is `SECURITY DEFINER`, owned by `esmbler_definer`, with a fixed `search_path`; only `esmbler_app` may execute it. Migration `0013_workspace_provisioning_state.sql` replaces it (as its owner, through `SET ROLE`) with a stricter version:

- It fails closed when an argument is missing (null ID, creator or limit, or a limit below 1).
- It refuses any creator that is not an enabled `kind = 'user'` principal, so an agent or a disabled account cannot create a workspace. `ProvisioningService` turns that refusal into `403` "Your account cannot create workspaces."
- It takes a per-person advisory lock, counts the creator's active workspaces plus provisioning ones younger than 15 minutes, and inserts the tenant row with status `provisioning` only when the count is below the limit. Parallel requests from one person therefore cannot exceed it. `ProvisioningService` turns a refusal into `409 workspace_limit`.

Migration 0013 also adds `provisioning` to the allowed `kernel.tenants.status` values and the function `kernel.activate_workspace(id, creator)` (`SECURITY DEFINER`, owned by `esmbler_definer`, `EXECUTE` only for `esmbler_app`). It sets a `provisioning` tenant that `creator` created to `active` and does nothing else. `WorkspaceService` calls it after provisioning and the audit entry. A run that fails earlier leaves the tenant in `provisioning`: the workspace switcher lists only active tenants (`IdentityService.TenantsForAsync`, also used to resolve a switch), and after 15 minutes the tenant no longer counts toward the limit. Nothing removes such tenants yet (KI-023).

`ProvisioningService` calls `kernel.create_workspace` only when `TenantSeed.CreatedBy` is set. Operator and seed provisioning still use `kernel.provision_tenant` and leave `created_by` null, so they do not count.

The limit is `Esmbler:MaxWorkspacesPerPerson` (default 5). For local development and acceptance runs, Compose turns creation on (`Esmbler__AllowWorkspaceCreation: "true"`) and sets the limit to 100, because each acceptance run creates a workspace.

### API

- `GET /api/starters`: signed in, no workspace needed (a new admin may have none). Returns `enabled` and the starters with their questions and surfaces.
- `POST /api/workspaces` `{name, starter, answers}`: signed in; tokens with an agent claim are refused (403); `403` when creation is off; rate limited per person by its own policy `LoginThrottle.WorkspacePolicy` (`workspaces`, 3 a minute; `429 rate_limited` beyond). Returns `201` with the tenant ID, slug and name in the body and no `Location` header.

### Web flow

`/workspaces/new` (`apps/web/src/app/pages/create-workspace.page.ts`) has three steps: describe (name, an optional description, the starter), questions (only those without defaults) and review (the generated surfaces and the answers), then create. The description suggests a starter by keyword match ("Matches your description"); the person still chooses (ASM-069). The workspaces page offers "Create a workspace".

### Honest empty states

`MetricService.DashboardAsync` returns `DashboardData.Empty` when every visible figure counts zero and the viewer can see no records of the dashboard's entity. That check runs inside the dashboard's request budget and in its own savepoint; if it runs out of time, the real (zero) figures are shown instead of previews. The figures are then previews (`MetricResult.Preview`): sample values shaped like the metric (its date buckets, its choice options, or numbered sample teams and people), with no drill and no record data (ASM-071). The tiles carry a "Preview" badge and the text "Sample figure, not your data". The dashboard shows a "No tickets yet" banner with next steps: create the first ticket (or make the first request), invite the team, review the configuration. The explorer's empty state offers to create the first record. The first real record turns previews into real figures that drill into it.

### Flow

```mermaid
sequenceDiagram
    participant W as Web (/workspaces/new)
    participant A as API
    participant S as WorkspaceService
    participant P as ProvisioningService
    participant DB as PostgreSQL
    W->>A: GET /api/starters
    A->>S: Starters() (surfaces from the latest release)
    S-->>W: starters, questions, surfaces
    W->>A: POST /api/workspaces {name, starter, answers}
    A->>A: refuse agents; workspaces rate limit
    A->>S: CreateAsync(principal from token)
    S->>P: ProvisionAsync(CreatedBy, CreationLimit, no sample data)
    P->>DB: kernel.create_workspace (enabled person, advisory lock, count, insert as provisioning)
    DB-->>P: true, false (409 workspace_limit) or refused (403)
    P->>DB: environments, teams, creator membership and admin role
    P->>DB: install and activate the latest release
    S->>DB: audit workspace.create
    S->>DB: kernel.activate_workspace (provisioning to active)
    S-->>W: 201 {tenantId, slug, name}
    W->>A: switch to the new workspace, open home
```

### Consequences

- Good: a new admin gets a complete, connected workspace in one step without code (REQ-ACC-15).
- Good: starters are module data, so a future module offers its own without kernel changes; the review cannot drift from what is installed.
- Good: the limit holds under concurrency because the database checks and inserts in one locked step.
- Good: empty workspaces never show real-looking numbers or fake records.
- Bad: the configuration is published at creation rather than left as a draft (ASM-074), and there is no persona preview inside the flow yet (G2-M9c).
- Bad: provisioning is several transactions. A failure after the tenant row is written leaves a partly provisioned workspace in `provisioning`. It is hidden from the switcher and stops counting toward the creator's limit after 15 minutes, but nothing removes it yet (KI-023).
- Bad: there is no self-service deletion yet, so the limit counts every workspace a person ever created.
- Neutral: self-service creation is off by default; an operator turns it on with `Esmbler:AllowWorkspaceCreation`. The creation endpoint has its own per-person rate limit (3 a minute); the per-person workspace limit is the real bound.

## Pros and cons of the options

| Option | Pros | Cons |
| --- | --- | --- |
| 1. Starters, self-service, limit in a definer function | No code; reuses the module's connected surfaces; atomic limit; off until an operator turns it on | Two new privileged functions; partly provisioned (hidden) workspaces possible on failure |
| 2. Operator-only provisioning | No new abuse surface | Fails REQ-WS-001 and REQ-ACC-15; every pilot waits for an operator |
| 3. AI-planned creation from nothing | Flexible | Slow, nondeterministic, needs a model key; agents would need rights to create tenants, which they must never have |
| 4. Clone a template workspace | Simple to explain | Copies records and identifiers between tenants; templates drift from module releases; breaks upgrade paths |

## Security notes

- **Resource abuse.** Creation is off unless an operator turns it on. Each person may create at most `MaxWorkspacesPerPerson` workspaces, checked and inserted under a per-person advisory lock in `kernel.create_workspace`. Requests are rate limited per person (`workspaces` policy, 3 a minute). Names and answers are length-checked.
- **Spoofed names.** Unicode control and format characters (bidi overrides) are removed from names and team answers, so a name cannot render differently in the switcher. Unknown starter IDs are truncated in error messages.
- **Principal attachment.** The creator is attached by the principal ID from the token, never from the request body or an email lookup, so a request cannot add someone else to a workspace or take over an account by email.
- **Definer functions.** `kernel.create_workspace` and `kernel.activate_workspace` run as `esmbler_definer` (BYPASSRLS; `SELECT` and `INSERT` on `kernel.tenants`, `UPDATE` only on its `status` and `authz_revision` columns; `SELECT` on `kernel.principals`) with `search_path = kernel, pg_temp`, `EXECUTE` revoked from `PUBLIC` and granted only to `esmbler_app`. `create_workspace` inserts only a tenant row, fails closed on missing arguments and refuses creators that are not enabled people; `activate_workspace` only moves a `provisioning` tenant of that creator to `active`. Both trust the creator and limit they are given, so only `ProvisioningService` and `WorkspaceService` call them, with values from the token and the platform options.
- **Agents.** No MCP tool creates workspaces. Agent tokens use the `esmbler-mcp` audience, which the API does not accept, and `POST /api/workspaces` also refuses any token with an agent claim as a second check.
- **Isolation.** A new workspace is an ordinary tenant with FORCE RLS; the creator's other workspaces see nothing of it.
- **Previews.** Preview figures are computed from fixed patterns and configuration labels, never from records, and carry no drill.
- Threat model entry: [T-72](../security/threat-model.md#tenant-isolation).

## More information

- Related: [ADR 0010](0010-database-migrations-and-roles.md) (roles, privileged functions, migrations 0012 and 0013), [ADR 0011](0011-identity-and-development-authentication.md) (identity, explicit workspace switch), [ADR 0019](0019-metrics-dashboards-and-connected-explorer.md) (metrics and dashboards), [connected workspace](../spec/connected-workspace.md).
- Assumptions: ASM-069 to ASM-075 in [assumptions](../decisions/assumptions.md).
- Deferred to G2-M9c: persona preview inside the creation flow ("preview as an agent or a requester"); see [progress](../progress.md).
- Verification:
  - Integration: `Configuration.WorkspaceCreationTests` (`Starters_ask_only_questions_without_defaults_and_list_the_connected_surfaces`, `A_new_admin_creates_a_ticketing_workspace_that_starts_empty_with_labeled_previews_and_drills_into_real_tickets`, `Creation_is_limited_per_person_validated_and_can_be_turned_off` (also an agent principal refused and bidi characters stripped), `Parallel_creations_never_exceed_the_per_person_limit`), `Api.SecurityHardeningTests.Workspace_creation_needs_a_signed_in_person_and_is_off_unless_an_operator_turns_it_on`, `Isolation.DatabaseIsolationTests.Only_allow_listed_security_definer_functions_exist`.
  - Playwright: `workspace.spec.ts` `@REQ-ACC-15`; the creation-flow audit in `a11y.spec.ts`.
