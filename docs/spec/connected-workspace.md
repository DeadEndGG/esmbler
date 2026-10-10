---
title: Connected workspace and guided creation (requirements addendum)
status: accepted
owner: product
last_reviewed: 2026-10-10
requirements: [REQ-WS-001, REQ-WS-002, REQ-WS-003, REQ-WS-004, REQ-WS-005, REQ-WS-006, REQ-ACC-15]
---

# Connected workspace and guided creation (requirements addendum)

The product owner reviewed the design on 2026-10-10 and asked for the creation experience to be explicit. This addendum turns that review into requirements. It adds to the [master spec](master-spec.md) and the [execution brief](execution-brief.md); it does not replace them, and they win on conflicts. The requirements are tracked in the [traceability matrix](traceability.md) like any other.

## The experience

An admin describes what they want, for example "Create an IT ticketing system with priorities, categories, assignments, an explorer, and a dashboard showing workload and SLA issues."

1. **Start from a complete workspace.** The builder picks the matching starter (Ticketing) and prepares a draft. It asks only about decisions that have no sensible default, such as which team receives new tickets.
2. **Connected pieces come together.** One reviewable draft holds:

   | Surface | Contents |
   | --- | --- |
   | Ticket form and detail page | Fields, status transitions, conversations, attachments, assignment, activity |
   | Ticket explorer | Search, filters, sorting, configurable columns, pagination, saved views |
   | Dashboard | Open tickets, unassigned tickets, SLA breaches, workload by team, resolution trend |
   | Navigation | Overview, Tickets, and settings for authorized people |
   | Permissions | Access for agents, managers and requesters |

   Nobody connects a chart or a table to an API by hand.
3. **The surfaces behave as one application.** Selecting "12 unassigned tickets" on the dashboard opens the explorer with exactly that filter. Opening a ticket and coming back keeps the explorer's filters and position.
4. **Customizing is one change.** "Add a Vendor field, make it required for hardware tickets, and let me group tickets by vendor" produces one reviewable change: the field, its place on the form, the validation rule and the explorer grouping. The field becomes available in the dashboard builder with the filters and groupings its type and permissions allow; it does not have to appear on every screen.
5. **Preview and publish together.** Preview as an agent or a requester, try sample tickets, check the explorer and the dashboard, then publish the connected configuration in one step.

## Requirements

| ID | Requirement |
| --- | --- |
| REQ-WS-001 | **Guided creation.** A first-class "Create a workspace" flow starts from a starter (for example Ticketing), asks only questions without defaults, and produces one draft with forms, detail pages, explorer, dashboard, navigation and permissions. Advanced settings stay available but are not the starting point. |
| REQ-WS-002 | **Generated connections.** Forms, explorers, dashboards, navigation and permissions are generated from the same module definition and stay connected when it changes; no surface is wired to an API by hand. |
| REQ-WS-003 | **Drill-down and return.** Every dashboard figure that counts records opens the explorer filtered to exactly those records. The explorer keeps filters, sort, columns and position when the person opens a record and returns. |
| REQ-WS-004 | **Reusable metric definitions.** Metrics such as "Open", "Unassigned", "Overdue" and "Average resolution time" are named definitions with a filter, a measure, a date range and permissions. Dashboards and the explorer use the same definitions, so a figure and the explorer's result count agree for the same viewer and scope. |
| REQ-WS-005 | **One change across surfaces.** A customization request (for example a new field with a rule and an explorer grouping) is one reviewable draft step set covering form placement, validation, explorer configuration and the field's availability to the dashboard builder, filtered by its type and permissions. |
| REQ-WS-006 | **Honest empty states.** A new workspace shows guidance and clearly labeled preview data, never figures that look real but are not. |
| REQ-ACC-15 | **Acceptance.** A new admin creates the whole ticketing workspace, adds a custom field, and drills from a dashboard figure into its tickets, without writing code or knowing the database schema. |

## Where this lands in the plan

- What already exists: a complete Tickets module is installed at provisioning with record and list pages, permissions, workflow, rules, catalog, approvals and sample data; the list page has search, filters, sorting, pagination and saved views; the AI builder edits fields, layout, rules and workflows as one reviewable draft with live preview and persona preview.
- What is missing: dashboards and metric definitions, drill-down from figures into the explorer, explorer state on return, configurable explorer columns and grouping, a guided creation flow, and labeled preview data on empty workspaces.
- The work is scheduled in [progress](../progress.md) as G2-M9 (metrics, dashboards and the connected explorer, which also covers "basic reports") and G2-M9b (guided creation), right after SLAs (G2-M4) because the dashboard needs SLA breaches.
