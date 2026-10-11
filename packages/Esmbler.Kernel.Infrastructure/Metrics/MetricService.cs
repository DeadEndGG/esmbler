using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Alerts;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Metrics;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Esmbler.Kernel.Infrastructure.Metrics;

/// <summary>The explorer query a figure counts: open <c>/records/{Entity}</c> with these filters and SLA conditions.</summary>
public sealed record DrillQuery(string Entity, IReadOnlyList<string> Filters, IReadOnlyList<string> Sla, string Label);

/// <summary>
/// One bar or row. <see cref="Drill"/> is null for preview figures, which count no records, and for alert metrics,
/// which open the Alerts page. <see cref="Other"/> marks the bucket that sums every group past the metric's limit.
/// </summary>
public sealed record MetricBucketView(string Key, string Label, double? Value, long Count, DrillQuery? Drill, bool Capped = false, bool Other = false);

/// <summary>
/// A metric evaluated for one viewer. <see cref="Status"/> is <c>ok</c>, <c>restricted</c> (the viewer may not see it;
/// no values), or <c>timeout</c>. <see cref="Count"/> is the number of records the figure covers, capped at
/// <see cref="QueryCompiler.MaxCount"/> like explorer totals.
/// </summary>
public sealed record MetricResult(
    string Id,
    string Label,
    string Status,
    string Measure,
    string? Format,
    string? Unit,
    double? Value,
    long Count,
    bool Capped,
    IReadOnlyList<MetricBucketView> Buckets,
    bool MoreGroups,
    DrillQuery? Drill,
    DateTimeOffset AsOf,
    int ConfigRevision,
    bool Preview = false,
    string Source = MetricSources.Records);

/// <summary>What a figure counts: records (the default, with a drill into the explorer) or alerts about them.</summary>
public static class MetricSources
{
    public const string Records = "records";
    public const string Alerts = "alerts";
}

/// <summary>
/// Figures of a dashboard. <see cref="Empty"/> means the viewer can see no records of the dashboard's entity yet; the
/// figures are then clearly marked previews with sample values and no drill (REQ-WS-006), never real-looking numbers.
/// </summary>
public sealed record DashboardData(string View, int ConfigRevision, IReadOnlyList<MetricResult> Metrics, bool Empty = false);

/// <summary>A group header in the explorer: its label, count and the filter that lists it.</summary>
public sealed record GroupCount(string? Key, string Label, long Count, string Filter, bool Capped = false);

/// <summary>
/// Evaluates metrics (ADR 0019, REQ-WS-004). Every figure is a <see cref="RecordQuery"/> whose WHERE clause comes from
/// <see cref="QueryCompiler.Where"/>, the same code path as the explorer, and every figure carries the query it counted
/// (<see cref="DrillQuery"/>), so a figure and the list it opens agree for the same viewer by construction.
/// </summary>
public sealed class MetricService(Persistence.Database database, ConfigStore configs, AccessService access, IClock clock)
{
    public const int MaxMetricsPerDashboard = 12;

    /// <summary>
    /// Database time one request may use across all its figures. Each statement also stops after 5 s; figures that
    /// do not fit in the budget report <c>timeout</c> instead of holding the connection (threat model, ADR 0019).
    /// </summary>
    public static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(8);

    public Task<DashboardData> DashboardAsync(RequestContext ctx, string viewId, string? timeZone, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var view = artifact.Config.FindView(viewId);
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            if (view is null || view.Kind != ViewKinds.Dashboard || perms.Entity(view.Entity).Read == RecordScopes.None)
            {
                throw KernelException.NotFound($"Dashboard '{viewId}'");
            }

            var ids = view.Layout.Descendants().Where(n => !n.Hidden).Select(n => n.StringProp("metric")).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxMetricsPerDashboard).ToList();
            var session = await OpenAsync(db, ct);
            var results = new List<MetricResult>();
            foreach (var id in ids)
            {
                // Figures the viewer may not see are left out entirely (their tiles are pruned from the view too).
                if (artifact.Config.FindMetric(id) is { } metric && artifact.Config.FindEntity(metric.Entity) is { } entity && !Restricted(artifact.Config, perms, entity, metric))
                {
                    results.Add(await EvaluateAsync(session, artifact.Config, artifact.Revision, perms, metric, timeZone, ct));
                }
            }

            // An empty workspace (for this viewer) gets labeled preview figures instead of a wall of zeros (REQ-WS-006).
            if (results.Count > 0 && results.All(r => r.Status == "ok" && r.Count == 0) && artifact.Config.FindEntity(view.Entity) is { } viewEntity)
            {
                var cfg = artifact.Config;
                var q = new QueryCompiler.Ctx(cfg, viewEntity, perms);
                var where = QueryCompiler.Where(new RecordQuery { Entity = viewEntity.Id }, cfg, viewEntity, perms, q);
                bool any;
                await session.SavepointAsync(ct);
                try
                {
                    await session.LimitAsync(ct);
                    await using var cmd = session.Command($"SELECT EXISTS (SELECT 1 FROM kernel.records r WHERE {where})", q.Parameters);
                    any = (bool)(await cmd.ExecuteScalarAsync(ct))!;
                    await session.ReleaseAsync(ct);
                }
                catch (Exception ex) when (ex is BudgetExceededException or PostgresException { SqlState: PostgresErrorCodes.QueryCanceled })
                {
                    // Out of time: show the real (zero) figures rather than fail the dashboard.
                    await session.RollbackAsync(ct);
                    any = true;
                }

                if (!any)
                {
                    var previews = results.Select(r => Preview(cfg, cfg.FindMetric(r.Id)!, timeZone, artifact.Revision)).ToList();
                    return new DashboardData(view.Id, artifact.Revision, previews, Empty: true);
                }
            }

            return new DashboardData(view.Id, artifact.Revision, results);
        }, ct);

    public Task<MetricResult> EvaluateAsync(RequestContext ctx, string metricId, string? timeZone, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var metric = artifact.Config.FindMetric(metricId) ?? throw KernelException.NotFound($"Metric '{metricId}'");
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            return await EvaluateAsync(await OpenAsync(db, ct), artifact.Config, artifact.Revision, perms, metric, timeZone, ct);
        }, ct);

    /// <summary>
    /// A figure as CSV (REQ-OPS-004): one row per bar or table row (or one row for an ungrouped figure) with its label,
    /// value and record count, evaluated for this viewer like the dashboard. Restricted figures are refused; the export
    /// is audited. Needs <c>records.export</c>; AI agents cannot export.
    /// </summary>
    public Task<string> ExportCsvAsync(RequestContext ctx, string metricId, string? timeZone, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            if (ctx.AgentId is not null)
            {
                throw new KernelException("agent_not_allowed", "AI agents cannot export figures.", 403, null, "Export the figure yourself from the dashboard.");
            }

            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var metric = artifact.Config.FindMetric(metricId) ?? throw KernelException.NotFound($"Metric '{metricId}'");
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct, fresh: true);
            if (!perms.Can(PlatformActions.RecordsExport))
            {
                throw new KernelException("forbidden", "Exporting figures needs the records.export right.", 403, null, "Ask an administrator to export it, or to grant your role records.export.");
            }

            var result = await EvaluateAsync(await OpenAsync(db, ct), artifact.Config, artifact.Revision, perms, metric, timeZone, ct);
            if (result.Status == "restricted")
            {
                throw KernelException.NotFound($"Metric '{metricId}'");
            }

            if (result.Status != "ok")
            {
                throw new KernelException("metric_timeout", $"'{metric.Label}' took too long to count.", 503, metricId, "Try again in a moment.");
            }

            string Number(double? v) => v is { } x ? Math.Round(x, 4).ToString(CultureInfo.InvariantCulture) : string.Empty;
            string Count(long n, bool capped) => capped ? $"{n}+" : n.ToString(CultureInfo.InvariantCulture);
            var unit = result.Unit is { } u ? $" ({u})" : string.Empty;
            var lines = new List<string> { CsvText.Row([result.Buckets.Count > 0 ? "Group" : "Figure", $"Value{unit}", "Records"]) };
            lines.AddRange(result.Buckets.Count > 0
                ? result.Buckets.Select(b => CsvText.Row([b.Label, Number(b.Value), Count(b.Count, b.Capped)]))
                : [CsvText.Row([result.Label, Number(result.Value), Count(result.Count, result.Capped)])]);
            AuditWriter.Add(db, ctx, "metric.export", "metric", metric.Id, artifact.Revision, new { format = "csv", rows = lines.Count - 1 });
            await db.SaveChangesAsync(ct);
            return string.Join("\n", lines) + "\n";
        }, ct);

    /// <summary>Explorer group headers for a query: counts per value of a groupable field, from the same WHERE clause.</summary>
    public Task<IReadOnlyList<GroupCount>> GroupsAsync(RequestContext ctx, RecordQuery query, string field, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<GroupCount>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var entity = artifact.Config.FindEntity(query.Entity) ?? throw KernelException.NotFound($"Entity '{query.Entity}'");
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            var spec = entity.FindField(field);
            var q = new QueryCompiler.Ctx(artifact.Config, entity, perms);
            if (spec is null || QueryCompiler.GroupKey(entity, perms, q, field) is not { } key)
            {
                throw new KernelException("invalid_group", $"{entity.Label} cannot be grouped by '{field}'.", 400, field, "Group by a choice, status, priority, user, group or toggle field you can see.");
            }

            var where = QueryCompiler.Where(query, artifact.Config, entity, perms, q);
            var session = await OpenAsync(db, ct);
            var rows = new List<(string? Key, long Count)>();
            await using (var cmd = session.Command($"SELECT k, n FROM (SELECT {key.Key} AS k, count(*) AS n FROM kernel.records r WHERE {where} GROUP BY 1) g ORDER BY {key.Order.Replace(key.Key, "g.k", StringComparison.Ordinal)} NULLS LAST LIMIT 50", q.Parameters))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    rows.Add((reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetInt64(1)));
                }
            }

            var names = await NamesAsync(db, spec, rows.Select(r => r.Key), ct);
            return rows.Select(r => new GroupCount(r.Key, GroupLabel(artifact.Config, spec, r.Key, names), Math.Min(r.Count, QueryCompiler.MaxCount), GroupFilter(spec, r.Key), r.Count > QueryCompiler.MaxCount)).ToList();
        }, ct);

    private async Task<MetricResult> EvaluateAsync(Session session, EffectiveConfig config, int revision, EffectivePermissions perms, MetricSpec metric, string? timeZone, CancellationToken ct)
    {
        var now = clock.UtcNow;
        MetricResult Empty(string status) => new(metric.Id, metric.Label, status, metric.Measure.Kind, metric.Format, Unit(metric), null, 0, false, [], false, null, now, revision);
        if (config.FindEntity(metric.Entity) is not { } entity || Restricted(config, perms, entity, metric))
        {
            return Empty("restricted");
        }

        if (metric.Alerts is not null)
        {
            return await EvaluateAlertsAsync(session, config, revision, perms, entity, metric, timeZone, now, ct);
        }

        // The records the figure covers, as explorer filters: the metric filter, the resolved date window (absolute
        // instants, so a drill opened later lists the same records), and has() for measured fields.
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(metric.Filter))
        {
            filters.Add(metric.Filter);
        }

        TimeZoneInfo? zone = null;
        if (metric.DateRange is { } range)
        {
            zone = DateBuckets.Zone(range, config, timeZone);
            var (start, end) = DateBuckets.Window(range, zone, now);
            filters.Add(Between(entity, range.Field, start, end, zone));
        }

        foreach (var key in MeasuredFields(metric).Where(k => entity.FindField(k) is { System: false }))
        {
            filters.Add($"has(record.{key})");
        }

        var sla = metric.Sla is { } s ? new[] { new SlaFilter(s.Policy, s.Target, s.State) } : [];
        var query = new RecordQuery { Entity = entity.Id, Filters = filters, Sla = sla };
        var slaText = sla.Select(x => x.ToString()).ToList();
        var drill = new DrillQuery(entity.Id, filters, slaText, metric.Label);

        await session.SavepointAsync(ct);
        try
        {
            var q = new QueryCompiler.Ctx(config, entity, perms);
            var where = QueryCompiler.Where(query, config, entity, perms, q);
            var aggregate = Aggregate(metric, q);
            long count;
            await session.LimitAsync(ct);
            await using (var cmd = session.Command($"SELECT count(*) FROM (SELECT 1 FROM kernel.records r WHERE {where} LIMIT {QueryCompiler.MaxCount + 1}) c", q.Parameters))
            {
                count = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }

            double? value = count;
            if (aggregate is not null)
            {
                await session.LimitAsync(ct);
                await using var cmd = session.Command($"SELECT {aggregate} FROM kernel.records r WHERE {where}", q.Parameters);
                value = await cmd.ExecuteScalarAsync(ct) is { } v and not DBNull ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : null;
            }

            var buckets = new List<MetricBucketView>();
            var more = false;
            if (metric.GroupBy is { Bucket: { } bucketSize } && metric.DateRange is { } bucketRange && zone is not null)
            {
                var parts = DateBuckets.Split(bucketRange, bucketSize, zone, now);
                var bq = new QueryCompiler.Ctx(config, entity, perms);
                var bwhere = QueryCompiler.Where(query, config, entity, perms, bq);
                var bucketFilters = parts.Select(p => Between(entity, metric.GroupBy.Field, p.Start, p.End, zone)).ToList();
                var columns = bucketFilters.Select(f => $"count(*) FILTER (WHERE {bq.Filter(f)})").ToList();
                await session.LimitAsync(ct);
                await using var cmd = session.Command($"SELECT {string.Join(", ", columns)} FROM kernel.records r WHERE {bwhere}", bq.Parameters);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                for (var i = 0; i < parts.Count; i++)
                {
                    // Each bar stops counting where the explorer total it drills into does (QueryCompiler.MaxCount).
                    var n = reader.GetInt64(i);
                    var shown = Math.Min(n, QueryCompiler.MaxCount);
                    buckets.Add(new MetricBucketView(parts[i].Key, parts[i].Label, shown, shown, drill with { Filters = [.. filters, bucketFilters[i]], Label = $"{metric.Label}: {parts[i].Label}" }, n > QueryCompiler.MaxCount));
                }
            }
            else if (metric.GroupBy is { } group && entity.FindField(group.Field) is { } groupField)
            {
                var gq = new QueryCompiler.Ctx(config, entity, perms);
                var gwhere = QueryCompiler.Where(query, config, entity, perms, gq);
                var key = QueryCompiler.GroupKey(entity, perms, gq, group.Field)!.Value.Key;
                var groupAggregate = Aggregate(metric, gq);
                var empty = group.IncludeEmpty ? string.Empty : $" AND {key} IS NOT NULL";
                var rows = new List<(string? Key, long Count, double? Value)>();
                await session.LimitAsync(ct);
                await using (var cmd = session.Command($"SELECT {key} AS k, count(*) AS n{(groupAggregate is null ? string.Empty : ", " + groupAggregate)} FROM kernel.records r WHERE {gwhere}{empty} GROUP BY 1 ORDER BY n DESC, 1 LIMIT {group.Limit + 1}", gq.Parameters))
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var n = reader.GetInt64(1);
                        rows.Add((reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), n, groupAggregate is null ? n : reader.IsDBNull(2) ? null : Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture)));
                    }
                }

                more = rows.Count > group.Limit;
                var names = await NamesAsync(session.Db, groupField, rows.Select(r => r.Key), ct);
                buckets.AddRange(rows.Take(group.Limit).Select(r =>
                {
                    var label = GroupLabel(config, groupField, r.Key, names);
                    var shown = Math.Min(r.Count, QueryCompiler.MaxCount);
                    var value = metric.Measure.Kind == MetricMeasures.Count ? shown : r.Value;
                    return new MetricBucketView(r.Key ?? string.Empty, label, value, shown, drill with { Filters = [.. filters, GroupFilter(groupField, r.Key)], Label = $"{metric.Label}: {label}" }, r.Count > QueryCompiler.MaxCount);
                }));

                // Every group past the limit, summed into "Other" through an explorer filter, so it drills like any bar.
                if (more && OtherFilter(groupField, [.. rows.Take(group.Limit).Select(r => r.Key)], group.IncludeEmpty) is { } other && OtherWhere(config, entity, perms, filters, other, sla) is { } otherQuery)
                {
                    var (oq, owhere) = otherQuery;
                    var otherAggregate = Aggregate(metric, oq);
                    await session.LimitAsync(ct);
                    await using var cmd = session.Command($"SELECT count(*), {otherAggregate ?? "NULL"} FROM (SELECT * FROM kernel.records r WHERE {owhere} LIMIT {QueryCompiler.MaxCount + 1}) r", oq.Parameters);
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    await reader.ReadAsync(ct);
                    var n = reader.GetInt64(0);
                    var shown = Math.Min(n, QueryCompiler.MaxCount);
                    var otherValue = metric.Measure.Kind == MetricMeasures.Count ? shown : reader.IsDBNull(1) ? (double?)null : Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
                    if (n > 0)
                    {
                        buckets.Add(new MetricBucketView(OtherKey, "Other", otherValue, shown, drill with { Filters = [.. filters, other], Label = $"{metric.Label}: Other" }, n > QueryCompiler.MaxCount, Other: true));
                    }
                }
            }

            await session.ReleaseAsync(ct);
            return new MetricResult(metric.Id, metric.Label, "ok", metric.Measure.Kind, metric.Format, Unit(metric), value, Math.Min(count, QueryCompiler.MaxCount), count > QueryCompiler.MaxCount, buckets, more, drill, now, revision);
        }
        catch (Exception ex) when (ex is BudgetExceededException or PostgresException { SqlState: PostgresErrorCodes.QueryCanceled })
        {
            await session.RollbackAsync(ct);
            return Empty("timeout");
        }
    }

    /// <summary>
    /// An alert metric (time to acknowledge, time to resolve, alert counts): alerts about records of the entity that the
    /// viewer can read (the explorer's WHERE clause over the joined record) and whose alerts they see (responders see
    /// every alert, everyone else the alerts their groups own, they own, or are assigned), never suppressed ones.
    /// Alerts not about a record are not counted. Figures open the Alerts page instead of the explorer.
    /// </summary>
    private async Task<MetricResult> EvaluateAlertsAsync(Session session, EffectiveConfig config, int revision, EffectivePermissions perms, EntitySpec entity, MetricSpec metric, string? timeZone, DateTimeOffset now, CancellationToken ct)
    {
        var spec = metric.Alerts!;
        var q = new QueryCompiler.Ctx(config, entity, perms);
        var conditions = new List<string>
        {
            QueryCompiler.Where(new RecordQuery { Entity = entity.Id, Filters = string.IsNullOrWhiteSpace(metric.Filter) ? [] : [metric.Filter] }, config, entity, perms, q),
            $"a.entity_id = {q.Param(entity.Id)}",
            "a.status <> 'suppressed'",
        };
        if (spec.Rules.Count > 0)
        {
            conditions.Add($"a.rule_id = ANY({q.Param(spec.Rules.ToArray())})");
        }

        if (spec.MinSeverity is { } min)
        {
            conditions.Add($"a.severity = ANY({q.Param(EventSeverities.All.SkipWhile(x => x != min).ToArray())})");
        }

        // The Alerts page's visibility (AlertService): responders see every alert, and so do AI agents acting for one
        // (agents lose the right to act, not to read); everyone else sees their own, their groups' and assigned alerts.
        var seesAll = AlertService.CanRespond(perms)
            || (perms.Context.AgentId is not null && AlertService.CanRespond(await access.ForAsync(session.Db, perms.Context with { AgentId = null, Scopes = null }, config, ct)));
        if (!seesAll)
        {
            var me = q.Param(perms.Context.PrincipalId.ToString());
            conditions.Add($"(a.assignee::text = {me} OR a.owner_principal::text = {me} OR a.owner_group::text = ANY({q.Param(perms.Facts.Groups.Select(g => g.ToString()).ToArray())}))");
        }

        string Time(string field) => $"a.{field switch { MetricAlertFields.AcknowledgedAt => "acknowledged_at", MetricAlertFields.ResolvedAt => "resolved_at", _ => "opened_at" }}";
        string Instant(DateTimeOffset i) => $"{q.Param(i.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}::timestamptz";
        TimeZoneInfo? zone = null;
        if (metric.DateRange is { } range)
        {
            zone = DateBuckets.Zone(range, config, timeZone);
            var (start, end) = DateBuckets.Window(range, zone, now);
            conditions.Add($"{Time(range.Field)} >= {Instant(start)} AND {Time(range.Field)} < {Instant(end)}");
        }

        string? aggregate = null;
        if (metric.Measure.Kind == MetricMeasures.Duration)
        {
            var divisor = metric.Measure.Unit switch { "minutes" => 60, "days" => 86_400, _ => 3600 };
            conditions.Add($"{Time(metric.Measure.From!)} IS NOT NULL AND {Time(metric.Measure.To!)} IS NOT NULL");
            aggregate = $"avg(extract(epoch from ({Time(metric.Measure.To!)} - {Time(metric.Measure.From!)})) / {divisor})";
        }

        var from = "kernel.alerts a JOIN kernel.records r ON r.tenant_id = a.tenant_id AND r.environment_id = a.environment_id AND r.id = a.record_id";
        var where = string.Join(" AND ", conditions);
        MetricResult Done(double? value, long count, IReadOnlyList<MetricBucketView> buckets, bool more) =>
            new(metric.Id, metric.Label, "ok", metric.Measure.Kind, metric.Format, Unit(metric), value, Math.Min(count, QueryCompiler.MaxCount), count > QueryCompiler.MaxCount, buckets, more, null, now, revision, Source: MetricSources.Alerts);

        await session.SavepointAsync(ct);
        try
        {
            long count;
            double? value;
            await session.LimitAsync(ct);
            await using (var cmd = session.Command($"SELECT count(*), {aggregate ?? "NULL"} FROM (SELECT a.* FROM {from} WHERE {where} LIMIT {QueryCompiler.MaxCount + 1}) a", q.Parameters))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                count = reader.GetInt64(0);
                value = aggregate is null ? Math.Min(count, QueryCompiler.MaxCount) : reader.IsDBNull(1) ? null : Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
            }

            var buckets = new List<MetricBucketView>();
            var more = false;
            if (metric.GroupBy is { Bucket: { } size } group && metric.DateRange is { } bucketRange && zone is not null)
            {
                var parts = DateBuckets.Split(bucketRange, size, zone, now);
                var columns = parts.SelectMany(p =>
                {
                    var inBucket = $"{Time(group.Field)} >= {Instant(p.Start)} AND {Time(group.Field)} < {Instant(p.End)}";
                    return new[] { $"count(*) FILTER (WHERE {inBucket})", aggregate is null ? "NULL" : $"{aggregate} FILTER (WHERE {inBucket})" };
                }).ToList();
                await session.LimitAsync(ct);
                await using var cmd = session.Command($"SELECT {string.Join(", ", columns)} FROM {from} WHERE {where}", q.Parameters);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                for (var i = 0; i < parts.Count; i++)
                {
                    var n = Math.Min(reader.GetInt64(i * 2), QueryCompiler.MaxCount);
                    double? v = aggregate is null ? n : reader.IsDBNull((i * 2) + 1) ? null : Convert.ToDouble(reader.GetValue((i * 2) + 1), CultureInfo.InvariantCulture);
                    buckets.Add(new MetricBucketView(parts[i].Key, parts[i].Label, v, n, null));
                }
            }
            else if (metric.GroupBy is { } byField)
            {
                var key = byField.Field == MetricAlertFields.Rule ? "a.rule_id" : "a.severity";
                var rows = new List<(string Key, long Count, double? Value)>();
                await session.LimitAsync(ct);
                await using (var cmd = session.Command($"SELECT {key}, count(*) AS n, {aggregate ?? "NULL"} FROM {from} WHERE {where} GROUP BY 1 ORDER BY n DESC, 1 LIMIT {byField.Limit + 1}", q.Parameters))
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var n = reader.GetInt64(1);
                        rows.Add((reader.GetString(0), n, aggregate is null ? Math.Min(n, QueryCompiler.MaxCount) : reader.IsDBNull(2) ? null : Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture)));
                    }
                }

                more = rows.Count > byField.Limit;
                buckets.AddRange(rows.Take(byField.Limit).Select(r => new MetricBucketView(
                    r.Key,
                    byField.Field == MetricAlertFields.Rule ? config.FindAlertRule(r.Key)?.Label ?? r.Key : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(r.Key),
                    r.Value,
                    Math.Min(r.Count, QueryCompiler.MaxCount),
                    null,
                    r.Count > QueryCompiler.MaxCount)));
            }

            await session.ReleaseAsync(ct);
            return Done(value, count, buckets, more);
        }
        catch (Exception ex) when (ex is BudgetExceededException or PostgresException { SqlState: PostgresErrorCodes.QueryCanceled })
        {
            await session.RollbackAsync(ct);
            return new MetricResult(metric.Id, metric.Label, "timeout", metric.Measure.Kind, metric.Format, Unit(metric), null, 0, false, [], false, null, now, revision, Source: MetricSources.Alerts);
        }
    }

    /// <summary>
    /// A preview figure for an empty workspace: sample values shaped like the metric (its date buckets, choice options,
    /// or numbered sample teams and people), flagged <see cref="MetricResult.Preview"/>, with no drill and no record data.
    /// </summary>
    private MetricResult Preview(EffectiveConfig config, MetricSpec metric, string? timeZone, int revision)
    {
        var now = clock.UtcNow;
        var seed = metric.Id.Sum(c => c) % 7;
        int[] pattern = [6, 4, 7, 3, 5, 8, 4, 6, 5, 7, 3, 6];
        double Sample(int i) => pattern[(i + seed) % pattern.Length];
        var buckets = new List<MetricBucketView>();
        var entity = config.FindEntity(metric.Entity);
        if (metric.GroupBy is { Bucket: { } size } && metric.DateRange is { } range)
        {
            var zone = DateBuckets.Zone(range, config, timeZone);
            buckets.AddRange(DateBuckets.Split(range, size, zone, now).Select((b, i) => new MetricBucketView(b.Key, b.Label, Sample(i), (long)Sample(i), null)));
        }
        else if (metric.GroupBy is { } group && entity?.FindField(group.Field) is { } field)
        {
            var labels = field.ChoiceList is { } list && config.FindChoiceList(list) is { } choices
                ? choices.Options.Where(o => !o.Hidden).Select(o => o.Label).ToList()
                : field.Type switch
                {
                    FieldTypes.Toggle => ["Yes", "No"],
                    FieldTypes.User => ["Sample person 1", "Sample person 2", "Sample person 3"],
                    _ => ["Sample team 1", "Sample team 2", "Sample team 3"],
                };
            buckets.AddRange(labels.Take(Math.Min(group.Limit, 5)).Select((l, i) => new MetricBucketView($"preview_{i}", l, Sample(i), (long)Sample(i), null)).OrderByDescending(b => b.Count));
        }

        var count = buckets.Count > 0 ? (long)buckets.Sum(b => b.Count) : (long)Sample(0) + 3;
        double? value = metric.Measure.Kind == MetricMeasures.Count ? count : metric.Measure.Kind == MetricMeasures.Duration ? 4 + (seed / 2.0) : Sample(1);
        return new MetricResult(metric.Id, metric.Label, "ok", metric.Measure.Kind, metric.Format, Unit(metric), value, count, false, buckets, false, null, now, revision, Preview: true, Source: metric.Alerts is null ? MetricSources.Records : MetricSources.Alerts);
    }

    /// <summary>Whether the viewer may not see the figure at all (no values are computed for them); see <see cref="MetricVisibility"/>.</summary>
    public static bool Restricted(SpecCatalog config, EffectivePermissions perms, EntitySpec entity, MetricSpec metric) =>
        MetricVisibility.Restricted(config, perms, entity, metric);

    private static IEnumerable<string> MeasuredFields(MetricSpec metric) =>
        new[] { metric.Measure.Field, metric.Measure.From, metric.Measure.To }.OfType<string>();

    private static string? Unit(MetricSpec metric) => metric.Measure.Kind == MetricMeasures.Duration ? metric.Measure.Unit : null;

    /// <summary>SQL for the measure over the WHERE rows, or null for a plain count.</summary>
    private static string? Aggregate(MetricSpec metric, QueryCompiler.Ctx q)
    {
        var m = metric.Measure;
        switch (m.Kind)
        {
            case MetricMeasures.Sum or MetricMeasures.Avg or MetricMeasures.Min or MetricMeasures.Max:
                return $"{m.Kind}(({q.FieldSql(m.Field!).Sql})::numeric)";
            case MetricMeasures.Duration:
                var divisor = m.Unit switch { "minutes" => 60, "days" => 86_400, _ => 3600 };
                return $"avg(extract(epoch from (({q.FieldSql(m.To!).Sql})::timestamptz - ({q.FieldSql(m.From!).Sql})::timestamptz)) / {divisor})";
            default:
                return null;
        }
    }

    /// <summary>A filter for [start, end): instants for date_time fields, local dates for date fields.</summary>
    private static string Between(EntitySpec entity, string field, DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        if (entity.FindField(field)?.Type == FieldTypes.Date)
        {
            static string Day(DateTimeOffset i, TimeZoneInfo z) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(i, z).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return $"record.{field} >= '{Day(start, zone)}' && record.{field} < '{Day(end, zone)}'";
        }

        return $"record.{field} >= '{RecordsFormat(start)}' && record.{field} < '{RecordsFormat(end)}'";
    }

    private static string RecordsFormat(DateTimeOffset instant) => Kernel.Records.FieldNormalizer.FormatInstant(instant);

    /// <summary>The "Other" bucket's WHERE clause, or null when its filter does not compile (the figure then has no "Other").</summary>
    private static (QueryCompiler.Ctx Ctx, string Where)? OtherWhere(EffectiveConfig config, EntitySpec entity, EffectivePermissions perms, List<string> filters, string other, SlaFilter[] sla)
    {
        try
        {
            var q = new QueryCompiler.Ctx(config, entity, perms);
            return (q, QueryCompiler.Where(new RecordQuery { Entity = entity.Id, Filters = [.. filters, other], Sla = sla }, config, entity, perms, q));
        }
        catch (KernelException)
        {
            return null;
        }
    }

    /// <summary>The key of the bucket that sums the groups past a metric's limit.</summary>
    public const string OtherKey = "__other";

    /// <summary>
    /// The explorer filter for every value not shown as its own group (and no value, unless records without one are not
    /// counted or already have their group). Null when it would not fit the explorer's filter limits (1,000 characters,
    /// such as 25 people or teams): the figure then shows its groups without an "Other" bucket.
    /// </summary>
    public static string? OtherFilter(FieldSpec field, IReadOnlyList<string?> shown, bool includeEmpty)
    {
        var keys = shown.OfType<string>().ToList();
        var parts = new List<string>();
        if (field.Type == FieldTypes.Toggle)
        {
            parts.AddRange(keys.Select(k => $"!({GroupFilter(field, k)})"));
        }
        else if (keys.Count > 0)
        {
            parts.Add($"!(record.{field.Key} in [{string.Join(", ", keys.Select(Quote))}])");
        }

        if (!includeEmpty || shown.Contains(null))
        {
            parts.Add($"record.{field.Key} != null");
        }

        var filter = string.Join(" && ", parts);
        return parts.Count > 0 && filter.Length <= 1000 ? filter : null;
    }

    private static string Quote(string key) => $"'{key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}'";

    /// <summary>The explorer filter for one group value; it compiles to the same SQL value the group key uses.</summary>
    public static string GroupFilter(FieldSpec field, string? key) => key switch
    {
        null => $"record.{field.Key} == null",
        _ when field.Type == FieldTypes.Toggle => $"record.{field.Key} == {(key is "true" or "True" ? "true" : "false")}",
        _ => $"record.{field.Key} == {Quote(key)}",
    };

    private static string GroupLabel(SpecCatalog config, FieldSpec field, string? key, IReadOnlyDictionary<string, string> names)
    {
        if (key is null)
        {
            return $"No {field.Label.ToLowerInvariant()}";
        }

        if (field.Type == FieldTypes.Toggle)
        {
            return key is "true" or "True" ? field.Label : $"Not {field.Label.ToLowerInvariant()}";
        }

        if (field.ChoiceList is { } list && config.FindChoiceList(list)?.Options.Find(o => o.Id == key) is { } option)
        {
            return option.Label;
        }

        return names.GetValueOrDefault(key, key);
    }

    /// <summary>Display names of users and groups (labels only; IDs are already visible to readers of the records).</summary>
    private static async Task<IReadOnlyDictionary<string, string>> NamesAsync(KernelDbContext db, FieldSpec field, IEnumerable<string?> keys, CancellationToken ct)
    {
        var ids = keys.OfType<string>().Select(k => Guid.TryParse(k, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        return field.Type switch
        {
            FieldTypes.User => await db.Principals.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id.ToString(), p => p.DisplayName, ct),
            FieldTypes.Group => await db.Groups.Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id.ToString(), g => g.Name, ct),
            _ => new Dictionary<string, string>(),
        };
    }

    private static async Task<Session> OpenAsync(KernelDbContext db, CancellationToken ct)
    {
        var session = new Session(db, db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), Stopwatch.GetTimestamp() + (long)(RequestBudget.TotalSeconds * Stopwatch.Frequency));
        await using var cmd = session.Command("SET LOCAL statement_timeout = '5s'", []);
        await cmd.ExecuteNonQueryAsync(ct);
        return session;
    }

    private sealed class BudgetExceededException : Exception;

    /// <summary>Raw SQL inside the tenant transaction; one savepoint per figure so a timeout fails only that figure.</summary>
    private sealed class Session(KernelDbContext db, DbConnection conn, DbTransaction tx, long deadline)
    {
        public KernelDbContext Db { get; } = db;

        /// <summary>Gives the next statement what is left of the request budget (at most 5 s), or stops the figure.</summary>
        public async Task LimitAsync(CancellationToken ct)
        {
            var remaining = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (remaining < 100)
            {
                throw new BudgetExceededException();
            }

            await using var cmd = Command($"SET LOCAL statement_timeout = {Math.Min(remaining, 5000)}", []);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public DbCommand Command(string sql, IEnumerable<NpgsqlParameter> parameters)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(p.Clone());
            }

            return cmd;
        }

        public Task SavepointAsync(CancellationToken ct) => Exec("SAVEPOINT metric", ct);

        public Task ReleaseAsync(CancellationToken ct) => Exec("RELEASE SAVEPOINT metric", ct);

        public Task RollbackAsync(CancellationToken ct) => Exec("ROLLBACK TO SAVEPOINT metric", ct);

        private async Task Exec(string sql, CancellationToken ct)
        {
            await using var cmd = Command(sql, []);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
