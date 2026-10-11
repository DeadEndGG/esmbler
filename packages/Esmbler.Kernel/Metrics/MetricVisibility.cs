using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Metrics;

/// <summary>
/// Who may see a figure (ADR 0019): pure, so dashboards, the experience bootstrap and persona previews decide the same
/// way. A restricted figure is computed for nobody and removed from the viewer's dashboards.
/// </summary>
public static class MetricVisibility
{
    /// <summary>
    /// True when the viewer cannot read the entity, is an agent without <c>records:read</c>, holds none of the metric's
    /// roles (the tenant administrator always passes), or the metric reads a field hidden from the viewer.
    /// </summary>
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

        // Alert metrics measure, date and group by alert fields; only their filter reads the record.
        var reads = metric.Alerts is not null ? [] : MeasuredFields(metric).Concat(new[] { metric.GroupBy?.Field, metric.DateRange?.Field }.OfType<string>()).ToList();
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

        return reads.Any(k => k != "id" && perms.FieldAccess(entity, k) == FieldAccessLevels.Hidden);
    }

    /// <summary>The fields a measure reads (sum/avg/min/max field, duration ends).</summary>
    public static IEnumerable<string> MeasuredFields(MetricSpec metric) =>
        new[] { metric.Measure.Field, metric.Measure.From, metric.Measure.To }.OfType<string>();
}
