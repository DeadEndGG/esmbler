---
title: "ADR 0001: Record architecture decisions"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-AUTHZ-001, REQ-EXT-001, REQ-CFG-006, REQ-REC-001]
---

# ADR 0001: Record architecture decisions

## Context and problem statement

Esmbler is built autonomously, gate by gate, by coding agents that may lose context between sessions. The execution brief (section 1) says irreversible or expensive decisions are recorded as ADRs with alternatives and rationale, and that nobody stops to ask for approval. A later session, or a human reviewer testing a gate, must be able to see why a choice was made and what evidence would reopen it.

How do we record architecture decisions so that they stay findable, reviewable in pull requests, and honest about what is still open?

## Decision drivers

- Docs-as-code: Markdown in the repository, reviewed and linted with the code (execution brief section 7).
- A format that agents and humans both fill in the same way.
- Explicit revisit triggers, because most choices are "simplest thing for the pilot".
- Stable numbering so code comments, commits and other docs can link to a decision.

## Considered options

1. **MADR** (Markdown Architectural Decision Records).
2. Nygard's original short ADR format (context, decision, status, consequences).
3. Decisions recorded only in `docs/architecture/` prose.

## Decision outcome

**Chosen option: MADR**, one file per decision at `docs/adr/NNNN-short-title.md`.

- **Numbering:** four digits, assigned in order and never reused. A number may be reserved for a decision that a spike is still producing (0006 was reserved this way).
- **Sections:** Context and problem statement, Decision drivers, Considered options, Decision outcome (with Consequences), Pros and cons of the options, More information and revisit triggers.
- **Front matter:** `title`, `status`, `owner`, `last_reviewed`, `requirements` (IDs from [the traceability matrix](../spec/traceability.md) only).
- **Statuses:**
  - `proposed`: written but not yet backed by evidence or a spike.
  - `accepted`: the decision in force.
  - `superseded`: replaced. The front matter adds `superseded_by: NNNN`, and the new ADR names the one it replaces. Superseded ADRs are never deleted.
- **Changing a decision:** small clarifications edit the ADR in place and bump `last_reviewed`. A different choice is a new ADR that supersedes the old one.
- **Spikes:** a decision that depends on a spike links to the spike folder under `spikes/` and quotes its measured evidence.
- **Assumptions vs ADRs:** a default chosen instead of asking a human goes in [assumptions](../decisions/assumptions.md). An ADR is only needed when the choice is expensive to reverse.

### Consequences

- Good: every ADR has the same shape, so the `docs-steward` reviewer agent can check it mechanically.
- Good: requirement IDs in the front matter let the traceability matrix point at the decision.
- Bad: MADR is longer than Nygard's format. We keep each ADR to roughly 80–200 lines and move long material (spike data, schemas) into linked files.

## Pros and cons of the options

| Option | Pros | Cons |
|---|---|---|
| MADR | Considered options and drivers are explicit; widely known; good fit for "alternatives and rationale" | Longer |
| Nygard | Very short | No structured alternatives section; rationale tends to be thin |
| Prose only | No ceremony | Decisions get lost; no status or supersession |

## ADR index

| ADR | Title | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | accepted |
| [0002](0002-storage-and-query-model.md) | Storage and query model | accepted |
| [0003](0003-configuration-artifact-and-overlay-format.md) | Configuration artifact and overlay format | accepted |
| [0004](0004-authorization-engine.md) | Authorization engine (OpenFGA, with spike) | accepted |
| [0005](0005-job-and-workflow-execution.md) | Job and workflow execution | accepted |
| [0006](0006-extension-runtime.md) | Extension runtime (produced by the extension-runtime spike) | accepted |
| [0007](0007-mcp-server-design.md) | MCP server design | accepted |
| [0008](0008-realtime-channel-for-live-editing.md) | Real-time channel for live editing | accepted |
| [0009](0009-rule-expression-language.md) | Rule expression language | accepted |
| [0010](0010-database-migrations-and-roles.md) | Database migrations and roles | accepted |
| [0011](0011-identity-and-development-authentication.md) | Identity and development authentication | accepted |
| [0012](0012-repository-layout-and-module-boundaries.md) | Repository layout and module boundaries | accepted |
| [0013](0013-ai-assistant-model-adapter.md) | AI assistant model adapter | accepted |

ADR 0006 is written by the extension-runtime spike (`spikes/extension-runtime/`), the same way 0004 came from the authorization spikes. The runtime itself ships in Gate 3.

## More information

- Revisit trigger: if ADR count or cross-references make the index hard to maintain, generate this table from front matter in the docs lint job.
- Related: [execution brief, section 7](../spec/execution-brief.md), [assumptions](../decisions/assumptions.md).
