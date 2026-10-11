---
title: "ADR 0019: Metrics, dashboards and the connected explorer"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-11
requirements: [REQ-OPS-004, REQ-WS-001, REQ-WS-002, REQ-WS-003, REQ-WS-004, REQ-WS-005, REQ-WS-006, REQ-BIZ-005, REQ-LIVE-003, REQ-MIG-004]
---

# ADR 0019: Metrics, dashboards and the connected explorer

## Context and problem statement

The master spec asks for basic reports over standard and custom fields (REQ-OPS-004). The product owner's review ([connected workspace](../spec/connected-workspace.md)) adds more: named metric definitions shared by dashboards and the explorer (REQ-WS-004), figures that open the explorer on exactly their records and an explorer that keeps its place (REQ-WS-003), surfaces generated from one module definition (REQ-WS-002), and a customization request that is one reviewable change (REQ-WS-005). The Tickets dashboard needs open, unassigned, SLA breaches, workload by team and a resolution trend.

The hard part is trust. A dashboard that says "12 unassigned" must list exactly 12 tickets when clicked, for every viewer, and must never reveal records or field values the viewer cannot read.

How are metrics defined, where are they computed, how does a figure become an explorer query, and how do we keep aggregates from leaking data?

## Decision drivers

- A figure and the list it opens agree for the same viewer, by construction rather than by testing two code paths against each other.
- Aggregates obey record and field permissions; role gating can hide a figure but never widen what someone reads.
- Metrics are configuration: drafted, validated, published, upgraded and edited by the AI builder like everything else.
- One slow figure does not break a dashboard or hold database resources for long.
- Pilot scale (around 100,000 tickets per tenant) with no new infrastructure.
- Tenant isolation through RLS, as everywhere else.

## Considered options

1. **Metrics as spec objects evaluated live through the explorer's own query compiler; every figure carries its drill query.**
2. A separate aggregate store or materialized views refreshed in the background.
3. Client-side aggregation over list API pages.
4. An OLAP engine or analytics database (columnar store, cube).

## Decision outcome

**Chosen option 1.**

### Model

- `MetricSpec` (`packages/Esmbler.Sdk/Specs/Metrics.cs`): an entity, a `filter` in the list filter language (over `record` and `user`), a `measure` (`count`, `sum`, `avg`, `min`, `max` of a number field, or `duration`: the average time between two date-time fields in minutes, hours or days), an optional `groupBy` (a choice, status, priority, user, group or toggle field, or a date field in `day`, `week` or `month` buckets; `limit` 1–25, default 12; `includeEmpty`), an optional rolling `dateRange` (the last N days, weeks or months on a date field, aligned to a business calendar's time zone, a fixed time zone, or the viewer's), an optional `sla` condition (`policy[/target]`, state `breached`), `roles` and a display `format`.
- `ViewKinds.Dashboard`: a dashboard is a view whose layout holds only `layout.*` and `display.*` nodes.
- Elements: `display.kpi_tile` (one ungrouped metric), `display.bar_chart` and `display.metric_table` (grouped metrics), and `spec_object.metric` for the builder. `collection.data_table` 1.1.0 adds `groupBy` and `groupOptions`; `collection.saved_views` 1.1.0 lets an item reference a metric instead of repeating its filter.
- Overlay operations: `metric.add`, `metric.update` (module or tenant metrics, with expected-value conflict detection) and `metric.remove` (tenant metrics only, refused while a tile or saved view uses one). The SDK now has 37 operations. Changing a metric's `roles` counts as a permission change: it needs `permissions.admin` and agents cannot make it, even though roles never widen record access.

### Validation

`ConfigValidator.ValidateMetric` checks the entity, the measure and its field types, groupable types, date ranges (1–366 units, at most 52 buckets), calendars, time zones, SLA policies and targets, roles and format. A metric filter must compile and must use only what the SQL compiler supports (`Kernel/Records/QueryFilterSupport`), so a published metric never fails at run time. Tiles must match their metric (a KPI tile needs an ungrouped metric, a chart or table a grouped one). A metric that reads a field some permission set hides gets the warning `metric.restricted_field`: people with that permission set see the figure as restricted. `DependencyGraph` adds metric edges (entity, filter fields, measured, grouped and dated fields, calendar, SLA policy, roles, and the tiles and saved views that bind each metric), so removing a field a metric uses is reported. `ConfigDiff` now also covers metrics, SLA policies, calendars, catalog items, approval policies and event types.

### One query compiler for figures and lists

`QueryCompiler.Where` builds the WHERE clause for lists and metrics alike: entity, not deleted, the viewer's permission predicate, filters, SLA conditions and search. `MetricService` wraps it in `count(*)` or the measure's aggregate. Because the same function produces both, a figure equals the explorer total of its drill query for the same viewer. Counts stop at `QueryCompiler.MaxCount` (10,000) for both, shown as "10,000+". Each bar, table row and explorer group header stops at the same cap and carries its own `capped` flag, so only the group that stopped counting shows "+" (G2-M9b).

SLA breaches are a query condition too (`RecordQuery.Sla`, written `policy[/target]:breached` in URLs). It compiles to `EXISTS` over `kernel.sla_timers` for a non-cancelled timer with a recorded breach, so a breach counts once recorded, even after the ticket stops its clock (ADR 0018).

### Drill-down is the exact query

Every result carries a `DrillQuery`: the entity, the filters and SLA conditions it counted, and a label. Each bar or table row carries its own drill (the metric's filters plus the group or bucket filter). Group filters compile to the same SQL value the `GROUP BY` uses, so a group's count equals its list. A rolling date window is resolved to absolute instants (or local dates for date fields) at evaluation time, so a drill opened later lists the same records instead of a window that has moved on.

The server re-applies the viewer's permissions to every drill: the URL carries filters, never results or rights.

### Restricted figures are removed, never widened

A figure is restricted, and computed for nobody, when the viewer cannot read the entity, an agent lacks `records:read`, the viewer holds none of the metric's roles (the tenant administrator always passes), or the metric reads a field hidden from the viewer. `ExperienceService` removes restricted tiles and metric saved views from the view it sends, prunes `groupBy` and `groupOptions` on fields hidden from the viewer from data tables, and lists only dashboards with at least one visible figure in the bootstrap. Dashboard data leaves restricted figures out entirely; asking for one metric directly (`GET /api/metrics/{id}`) returns `status: restricted` with no values.

### Evaluation limits

`GET /api/dashboards/{view}/data` evaluates up to 12 metrics in one tenant transaction with `statement_timeout` 5 s. Each figure runs in its own savepoint, so a figure that times out returns `status: timeout` and the others still render. A dashboard request also has an overall budget of 8 s (`MetricService.RequestBudget`; each statement gets what is left, at most 5 s): figures not yet evaluated when it passes report `timeout`. `/api/dashboards`, `/api/metrics` and `/api/records/{entity}/groups` are rate limited per person (`Esmbler:MetricRequestsPerMinute`, 120 a minute by default), and a list or groups request takes at most 20 filters (`too_many_filters`). Date buckets come from `Kernel/Metrics/DateBuckets`: weeks start on Monday, windows include the current partial unit, and local midnights convert to UTC per bucket, so a daylight-saving change makes one day 23 or 25 hours long instead of shifting every later bucket.

### Explorer state lives in the URL

The list page keeps its state in the URL: `view`, `filter[]`, `sla[]`, `label` (the drill chip), `q`, `sort`, `dir`, `offset`, `group` and `f.<field>` filter-bar values. A drill is a link, can be shared, and the browser's back button works. `core/explorer-state.ts` remembers the last explorer URL and scroll position per entity for the session, so returning from a record lands on the same list and row. Group headers count through `GET /api/records/{entity}/groups` (the same WHERE clause, at most 50 groups). Column choices are kept in the browser (`localStorage` key `esm.columns.<tenant>.<env>.<user>.<view>`), keyed by workspace, environment, person and view.

### One request is one change

`DraftService.ApplyBatchAsync` applies several overlay operations as one change: validated together, all recorded or none, one draft version (migration `0011_draft_batches.sql` adds `kernel.draft_commands.batch_id`). Since G2-M9b, any single step can be undone or redone alone when no later step depends on it, as the master spec's "undo any single step" requires (REQ-LIVE-003); `SetUndoneAsync(..., wholeChange: true)` undoes or redoes the whole change (ASM-065). Every MCP build tool applies its operations as one batch. `add_field` gains `required_when`, `list_column`, `list_filter` and `list_group`, so "add a Vendor field, required for hardware, and let me group by it" is one reviewable step set (REQ-WS-005). A new field is offered to metrics by type through `describe_metric_fields`, which lists only fields the caller can see; a metric that reads it is restricted for viewers the field is hidden from. Undoing or redoing a step that changes permissions, or any step of a batch that contains one, needs `permissions.admin`, like applying it; a change with a permission step is never split by someone without that right. The builder panel shows a batch as one change with "Undo change" on its first step and "Undo" on each of its other steps. MCP `undo_step` undoes the whole change unless `only_this_step` is set ([ADR 0007](0007-mcp-server-design.md)).

### Read paths

- REST: `GET /api/dashboards/{view}/data?tz`, `GET /api/metrics/{id}?tz`, `GET /api/records/{entity}/groups?field&filter&sla&search`; the list `GET /api/records/{entity}` accepts `sla[]` and `group`.
- MCP: `list_metrics`, `describe_metric_fields` (config:read); `add_metric`, `update_metric`, `remove_metric`, `add_dashboard_tile` (config:edit); `evaluate_metric` (records:read, output wrapped as untrusted data). See [ADR 0007](0007-mcp-server-design.md).
- Web: `/dashboards/:view` (`pages/dashboard.page.ts`); home opens the first dashboard the person can see, otherwise their first list. `renderer/metrics.ts` renders KPI tiles, bar charts (each bar a keyboard-reachable link, with a table toggle) and metric tables.

### Tickets 1.6.0

Metrics `open`, `unassigned`, `sla_breached`, `avg_resolution`, `workload_by_team`, `resolved_trend` and `open_by_category` (the last four for agents and admins), the `core.tickets.dashboard` Overview, metric-backed saved views and table grouping options. See the [Tickets module](../architecture/modules/tickets.md#release-160-overview-dashboard).

### Follow-ups (G2-M9c)

- **At-risk SLAs.** An SLA condition takes the state `at_risk` (`policy[/target]:at_risk` in URLs): a running or paused timer with a recorded warning and no breach (ASM-152). Like breaches it compiles to `EXISTS` over `kernel.sla_timers`, so the figure and its list agree.
- **"Other".** A grouped metric with more groups than its limit adds an "Other" bucket that sums the rest. Its drill is an explorer filter (`!(record.f in [...])`, and `record.f != null` when empty values are not counted or have their own group), evaluated by the same compiler, so it agrees with its list like any bar; when that filter would exceed the explorer's 1,000-character limit the figure shows its groups without "Other" (ASM-061).
- **Alert metrics.** `MetricSpec.Alerts` (rules, minimum severity) makes a metric count alerts about the entity's records: `kernel.alerts` joined to `kernel.records` under `QueryCompiler.Where` for the viewer, so an alert counts only if its record is readable, plus the Alerts page's visibility (responders and agents acting for them see all; others their own, their groups' or assigned). Durations are wall-clock time. Measures are count or duration between `opened_at`, `acknowledged_at` and `resolved_at` (time to acknowledge, time to resolve); groups are severity, rule or date buckets. Alert figures carry `source: alerts` and no drill; their tiles open the Alerts page (ASM-153, [KI-067](../known-issues.md)). This closes [KI-041](../known-issues.md).
- **CSV export.** `GET /api/records/{entity}/export` writes the explorer's result for the person (same WHERE clause and order, at most 10,000 rows, visible fields with labels) and `GET /api/metrics/{id}/export` a figure's rows. Both need the `records.export` action, are audited before the first byte (`records.export`, `metric.export`), escape formula cells (`CsvText`) and are limited to ten a minute per person (ASM-154, T-103).
- **Dashboards as configuration.** Overlay operations `view.add` (a tenant dashboard: `tenant__` IDs, layout and display nodes only) and `view.remove` (tenant dashboards only); the SDK now has 54 operations. `Kernel/Configuration/DashboardLayout` keeps rows on the 12-unit grid as tiles come and go (at most four a row, re-spanned evenly, ASM-155). `DashboardBuilderService` and `/api/drafts/current/dashboards` (options, create, add tile, remove tile, remove dashboard) back the dashboard page's Edit mode, where builders add tiles from existing metrics or a new count over any groupable field they can see, tenant fields included (ASM-156); every action is one draft batch, published through review. MCP adds `add_dashboard`, and `add_dashboard_tile` accepts a row or no parent.
- **Tickets 1.10.0.** Metrics `sla_at_risk`, `alert_mtta` and `alert_mttr` on the Overview, a tighter P2 resolution goal and new holidays (ASM-158, ASM-159); clocks started before the upgrade keep their snapshot.

### Flow

```mermaid
sequenceDiagram
    participant W as Web (dashboard)
    participant A as API
    participant M as MetricService
    participant Q as QueryCompiler.Where
    participant P as PostgreSQL (RLS)
    W->>A: GET /api/dashboards/{view}/data?tz
    A->>M: DashboardAsync(viewer)
    M->>M: restricted? (roles, hidden fields, records:read)
    M->>Q: metric filter + date window + SLA, viewer permissions
    Q-->>M: WHERE clause
    M->>P: SAVEPOINT; count / aggregate / groups (5 s)
    P-->>M: values
    M-->>W: value, groups, DrillQuery per figure
    W->>A: GET /api/records/{entity}?filter[]&sla[] (the drill)
    A->>Q: same WHERE clause for the same viewer
    Q-->>W: total equals the figure
```

### Consequences

- Good: a figure and its list agree by construction, and permissions apply to aggregates exactly as to lists.
- Good: metrics are configuration the AI builder edits, upgrades preserve, and reviews show.
- Good: no new infrastructure; figures are live.
- Good: drill links are plain explorer URLs that can be shared and bookmarked.
- Bad: every dashboard load runs up to 12 live queries; there is no cache yet, so a large tenant pays per view (an open dashboard refreshes every 60 s while its tab is visible).
- Bad: rolling windows are evaluated at request time, so two viewers in different time zones can see different daily buckets for the same metric unless it names a calendar or time zone.
- Bad: totals and figures stop at 10,000 ("10,000+"); explorer group headers stop at 50 groups, and metrics at 25 plus an "Other" bucket (which is left out when its filter would be too long, ASM-061).
- Neutral: heavy analytics (history over years, cross-entity joins) belong to an analytics store (G4-M6), which can reuse the same metric specs.

## Pros and cons of the options

| Option | Pros | Cons |
| --- | --- | --- |
| 1. Spec metrics through the explorer's query compiler | Exact agreement with lists; permissions inside SQL; no new infrastructure; live | Live query cost per view; bounded by timeouts and limits |
| 2. Aggregate store or materialized views | Cheap reads | Stale figures that disagree with the list; per-viewer permissions cannot be precomputed without one aggregate per permission shape; refresh jobs per tenant |
| 3. Client-side aggregation | No server work | Needs every record on the client, capped by paging; hidden-field and scope rules duplicated in the browser |
| 4. OLAP engine or analytics database | Fast large-scale analytics | Another datastore with its own tenancy and permission model; replication lag; out of scope for the pilot (G4-M6) |

## Security notes

- **Aggregate inference.** A count or average could reveal records a viewer cannot open. Figures count only records the viewer can read, because the permission predicate is inside the same WHERE clause the list uses, and RLS scopes tenant and environment.
- **Hidden fields.** A metric whose filter, measure, grouping or date range reads a field hidden from the viewer is restricted for that viewer; no value is computed. Drill and list filters on hidden fields are refused (`invalid_filter`), and hidden fields cannot be group keys.
- **Role gating never widens access.** `roles` only hides a figure from people outside those roles; inside them, the figure still counts only what each person can read. Changing `roles` is still a permission change (`permissions.admin`; agents refused), because it decides who sees a figure.
- **Agents.** `evaluate_metric` needs `records:read`, which the default builder delegation does not have, and the agent sees only what its user can read; results are wrapped as untrusted data.
- **Drill URLs are untrusted input.** Filters in a URL are compiled with the same limits as any list filter (64 nodes, 1,000 characters) and evaluated under the viewer's permissions; SLA conditions must name a policy of that entity.
- **Hidden group keys.** Data-table `groupBy` and `groupOptions` on fields hidden from the viewer are pruned from the view the client receives.
- **Cost.** At most 12 figures a dashboard, 25 groups a metric, 52 buckets, 50 explorer groups, 20 filters a list or groups request, a 5 s statement timeout per figure, an overall dashboard deadline of about 8 s, a per-person rate limit on the metric endpoints and a 10,000 count cap.
- Threat model entry: [T-71](../security/threat-model.md#tenant-isolation).

## More information

- Related: [ADR 0002](0002-storage-and-query-model.md) (query model), [ADR 0003](0003-configuration-artifact-and-overlay-format.md) (overlay operations), [ADR 0007](0007-mcp-server-design.md) (MCP tools), [ADR 0018](0018-sla-and-business-hours.md) (SLA timers), [connected workspace](../spec/connected-workspace.md).
- Assumptions: ASM-058 to ASM-068 in [assumptions](../decisions/assumptions.md) (ASM-065 revised in G2-M9b).
- Guided workspace creation and labeled preview data on empty workspaces shipped with G2-M9b ([ADR 0020](0020-self-service-workspace-creation.md)): when the viewer can see no records of the dashboard's entity, `DashboardData.Empty` is set and every figure is a preview (`MetricResult.Preview`) with sample values and no drill.
- Shipped in G2-M9c (see Follow-ups above): the dashboard builder and `view.add`, the "Other" bucket, the at-risk SLA state, alert metrics and CSV export. Still open: drill-down for alert figures ([KI-067](../known-issues.md)) and drag and drop in the dashboard builder ([KI-068](../known-issues.md)).
- Verification:
  - Unit: `Configuration.MetricSpecTests`, `Metrics.DateBucketsTests`, `Metrics.QueryFilterSupportTests`.
  - Integration: `Records.MetricTests` (`Every_figure_equals_the_explorer_total_it_drills_into_for_every_persona`, `Reports_group_and_filter_by_a_custom_field_and_drill_into_it`, `Requesters_count_only_their_tickets_and_team_figures_are_restricted_for_them`, `Figures_and_group_counts_over_a_hidden_field_are_refused_and_left_out`, `Changing_who_sees_a_metric_or_undoing_a_permission_change_needs_permission_administration`, `Workload_groups_match_the_explorer_group_counts`, `Sla_breaches_count_recorded_breaches_and_drill_into_them`, `Figures_never_include_another_workspace`, `Averages_and_trends_cover_exactly_the_records_they_drill_into`), `Configuration.UpgradeTests.Upgrading_to_dashboards_keeps_customizations_and_running_sla_clocks`, `Configuration.ConfigLifecycleTests.A_batch_is_one_change_applied_whole_and_undone_together` (one step alone, then the whole change), `Mcp.McpServerTests.Agent_defines_a_metric_and_puts_it_on_the_dashboard` (also updates and removes a metric), `Api.SecurityHardeningTests.Figure_requests_are_rate_limited_per_person_and_lists_take_at_most_twenty_filters`.
  - G2-M9c: `Records.MetricTests` (`Groups_past_the_limit_sum_into_an_other_bucket_that_drills_like_any_bar`, `At_risk_counts_open_timers_with_a_recorded_warning_until_they_breach`, `Time_to_acknowledge_and_resolve_alerts_counts_only_alerts_the_viewer_sees_about_records_they_read`), `Configuration.DashboardBuilderTests`, `Configuration.UpgradeTests.Upgrading_to_a_changed_sla_policy_and_calendar_keeps_running_clocks_on_their_snapshot`, `Api.ExportTests`, `Mcp.McpServerTests.Agent_creates_a_dashboard_and_lays_tiles_out_in_rows`, unit `Configuration.MetricSpecTests` (dashboards, alert and at-risk validation, Tickets 1.10.0), Playwright `reporting.spec.ts`.
  - Web unit: `core/drill.spec.ts` (including per-bar caps).
  - Playwright: `dashboard.spec.ts`, dashboard audits in `a11y.spec.ts` (light and dark).
