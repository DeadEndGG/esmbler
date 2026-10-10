using System.Data.Common;
using System.Globalization;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Metrics;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Esmbler.Kernel.Infrastructure.Metrics;

/// <summary>The explorer query a figure counts: open <c>/records/{Entity}</c> with these filters and SLA conditions.</summary>
public sealed record DrillQuery(string Entity, IReadOnlyList<string> Filters, IReadOnlyList<string> Sla, string Label);

public sealed record MetricBucketView(string Key, string Label, double? Value, long Count, DrillQuery Drill);

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
    int ConfigRevision);

public sealed record DashboardData(string View, int ConfigRevision, IReadOnlyList<MetricResult> Metrics);

/// <summary>A group header in the explorer: its label, count and the filter that lists it.</summary>
public sealed record GroupCount(string? Key, string Label, long Count, string Filter);

/// <summary>
/// Evaluates metrics (ADR 0019, REQ-WS-004). Every figure is a <see cref="RecordQuery"/> whose WHERE clause comes from
/// <see cref="QueryCompiler.Where"/>, the same code path as the explorer, and every figure carries the query it counted
/// (<see cref="DrillQuery"/>), so a figure and the list it opens agree for the same viewer by construction.
/// </summary>
public sealed class MetricService(Persistence.Database database, ConfigStore configs, AccessService access, IClock clock)
{
    public const int MaxMetricsPerDashboard = 12;

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
                if (artifact.Config.FindMetric(id) is { } metric)
                {
                    results.Add(await EvaluateAsync(session, artifact.Config, artifact.Revision, perms, metric, timeZone, ct));
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
            await using (var cmd = session.Command($"SELECT {key.Key} AS k, count(*) FROM kernel.records r WHERE {where} GROUP BY 1 ORDER BY {key.Order.Replace(key.Key, "k", StringComparison.Ordinal)} NULLS LAST LIMIT 50", q.Parameters))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    rows.Add((reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetInt64(1)));
                }
            }

            var names = await NamesAsync(db, spec, rows.Select(r => r.Key), ct);
            return rows.Select(r => new GroupCount(r.Key, GroupLabel(artifact.Config, spec, r.Key, names), r.Count, GroupFilter(spec, r.Key))).ToList();
        }, ct);

    private async Task<MetricResult> EvaluateAsync(Session session, EffectiveConfig config, int revision, EffectivePermissions perms, MetricSpec metric, string? timeZone, CancellationToken ct)
    {
        var now = clock.UtcNow;
        MetricResult Empty(string status) => new(metric.Id, metric.Label, status, metric.Measure.Kind, metric.Format, Unit(metric), null, 0, false, [], false, null, now, revision);
        if (config.FindEntity(metric.Entity) is not { } entity || Restricted(config, perms, entity, metric))
        {
            return Empty("restricted");
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
            await using (var cmd = session.Command($"SELECT count(*) FROM (SELECT 1 FROM kernel.records r WHERE {where} LIMIT {QueryCompiler.MaxCount + 1}) c", q.Parameters))
            {
                count = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }

            double? value = count;
            if (aggregate is not null)
            {
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
                await using var cmd = session.Command($"SELECT {string.Join(", ", columns)} FROM kernel.records r WHERE {bwhere}", bq.Parameters);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                for (var i = 0; i < parts.Count; i++)
                {
                    var n = reader.GetInt64(i);
                    buckets.Add(new MetricBucketView(parts[i].Key, parts[i].Label, n, n, drill with { Filters = [.. filters, bucketFilters[i]], Label = $"{metric.Label}: {parts[i].Label}" }));
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
                    return new MetricBucketView(r.Key ?? string.Empty, label, r.Value, r.Count, drill with { Filters = [.. filters, GroupFilter(groupField, r.Key)], Label = $"{metric.Label}: {label}" });
                }));
            }

            await session.ReleaseAsync(ct);
            return new MetricResult(metric.Id, metric.Label, "ok", metric.Measure.Kind, metric.Format, Unit(metric), value, Math.Min(count, QueryCompiler.MaxCount), count > QueryCompiler.MaxCount, buckets, more, drill, now, revision);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            await session.RollbackAsync(ct);
            return Empty("timeout");
        }
    }

    /// <summary>Whether the viewer may not see the figure at all (no values are computed for them).</summary>
    public static bool Restricted(SpecCatalog config, EffectivePermissions perms, EntitySpec entity, MetricSpec metric)
    {
        if (perms.Entity(entity.Id).Read == RecordScopes.None || !perms.Context.HasScope(AgentScopes.RecordsRead))
        {
            return true;
        }

        if (metric.Roles.Count > 0 && !metric.Roles.Any(perms.Facts.Roles.ContainsKey) && !perms.Facts.Roles.ContainsKey(PlatformModule.TenantAdminRole))
        {
            return true;
        }

        var reads = MeasuredFields(metric).Concat(new[] { metric.GroupBy?.Field, metric.DateRange?.Field }.OfType<string>()).ToList();
        if (metric.Filter is { } filter)
        {
            try
            {
                reads.AddRange(Expressions.Expression.Compile(filter).References.Where(r => r.StartsWith("record.", StringComparison.Ordinal)).Select(r => r["record.".Length..]));
            }
            catch (Expressions.ExpressionException)
            {
                return true;
            }
        }

        _ = config;
        return reads.Any(k => k != "id" && perms.FieldAccess(entity, k) == FieldAccessLevels.Hidden);
    }

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

    /// <summary>The explorer filter for one group value; it compiles to the same SQL value the group key uses.</summary>
    public static string GroupFilter(FieldSpec field, string? key) => key switch
    {
        null => $"record.{field.Key} == null",
        _ when field.Type == FieldTypes.Toggle => $"record.{field.Key} == {(key is "true" or "True" ? "true" : "false")}",
        _ => $"record.{field.Key} == '{key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}'",
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
        var session = new Session(db, db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction());
        await using var cmd = session.Command("SET LOCAL statement_timeout = '5s'", []);
        await cmd.ExecuteNonQueryAsync(ct);
        return session;
    }

    /// <summary>Raw SQL inside the tenant transaction; one savepoint per figure so a timeout fails only that figure.</summary>
    private sealed class Session(KernelDbContext db, DbConnection conn, DbTransaction tx)
    {
        public KernelDbContext Db { get; } = db;

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
