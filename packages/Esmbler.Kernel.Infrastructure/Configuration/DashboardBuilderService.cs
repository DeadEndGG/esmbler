using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Infrastructure.Configuration;

public sealed record DashboardMetricOption(string Id, string Label, bool Grouped, string Source, bool Added);

public sealed record DashboardTileOption(string Id, string Type, string? Metric, string? Title);

public sealed record DashboardRowOption(string Id, IReadOnlyList<DashboardTileOption> Tiles);

public sealed record DashboardFieldOption(string Key, string Label, string Type);

/// <summary>What the dashboard builder offers for one dashboard of the open draft (or the published configuration).</summary>
public sealed record DashboardOptions(string View, string Label, string Entity, bool Added, IReadOnlyList<DashboardRowOption> Rows, IReadOnlyList<DashboardMetricOption> Metrics, IReadOnlyList<DashboardFieldOption> Groupable, int? DraftVersion);

/// <summary>A tile to add: an existing metric, or a new count of the dashboard's records (optionally grouped and open only).</summary>
public sealed record DashboardTileRequest(string Kind, string? Metric, string? Row, string? Title, string? NewLabel, string? GroupBy, bool OpenOnly, int ExpectedVersion);

/// <summary>
/// The dashboard builder outside the AI (REQ-WS-005, ADR 0019): new dashboards, tiles from existing metrics or from a
/// new metric over any groupable field the builder can see (tenant fields included), and removing tiles. Every change
/// is a draft change set (one batch), reviewed and published like any other; nothing here bypasses the draft.
/// </summary>
public sealed class DashboardBuilderService(PreviewService preview, DraftService drafts)
{
    public async Task<DashboardOptions> OptionsAsync(RequestContext ctx, string viewId, CancellationToken ct)
    {
        var (config, perms, draftVersion, _) = await preview.WorkingConfigAsync(ctx, ct);
        var view = Dashboard(config, viewId);
        var entity = config.FindEntity(view.Entity) ?? throw KernelException.NotFound($"Entity '{view.Entity}'");
        var section = view.Layout.Children.FirstOrDefault(c => c.Type == "layout.section");
        var rows = (section?.Children ?? []).Where(r => r.Type == "layout.row").Select(r => new DashboardRowOption(
            r.Id,
            [.. r.Descendants().Where(n => n.Type.StartsWith("display.", StringComparison.Ordinal)).Select(n => new DashboardTileOption(n.Id, n.Type, n.StringProp("metric"), n.StringProp("title") ?? n.StringProp("label")))])).ToList();
        var metrics = config.Metrics.Where(m => m.Entity == entity.Id).Select(m => new DashboardMetricOption(
            m.Id, m.Label, m.GroupBy is not null, m.Alerts is null ? "records" : "alerts", Namespaces.IsTenantOwned(m.Id))).ToList();
        // Offered by type and by what the builder may see (REQ-WS-005), as describe_metric_fields does for the AI.
        var groupable = entity.Fields.Where(f => !f.Hidden && ConfigValidator.IsGroupable(f) && perms.FieldAccess(entity, f.Key) != FieldAccessLevels.Hidden)
            .Select(f => new DashboardFieldOption(f.Key, f.Label, f.Type)).ToList();
        return new DashboardOptions(view.Id, view.Label, entity.Id, Namespaces.IsTenantOwned(view.Id), rows, metrics, groupable, draftVersion);
    }

    public async Task<(BatchResult Result, string View)> CreateAsync(RequestContext ctx, string label, string entityId, int expectedVersion, CancellationToken ct)
    {
        var name = (label ?? string.Empty).Trim();
        if (name.Length is 0 or > 60)
        {
            throw new KernelException("invalid_label", "Name the dashboard (1–60 characters).", 400, "label");
        }

        var (config, perms, _, _) = await preview.WorkingConfigAsync(ctx, ct);
        if (config.FindEntity(entityId) is null || perms.Entity(entityId).Read == RecordScopes.None)
        {
            throw KernelException.NotFound($"Entity '{entityId}'");
        }

        var view = DashboardLayout.NewDashboard(name, entityId);
        return (await drafts.ApplyBatchAsync(ctx, [new AddView { View = view }], expectedVersion, "ui", ct), view.Id);
    }

    public async Task<BatchResult> AddTileAsync(RequestContext ctx, string viewId, DashboardTileRequest request, CancellationToken ct)
    {
        var (config, perms, _, _) = await preview.WorkingConfigAsync(ctx, ct);
        var view = Dashboard(config, viewId);
        var ops = new List<OverlayOp>();
        var metricId = request.Metric;
        if (metricId is null)
        {
            // A new count of the dashboard's records, so a field added minutes ago can be charted without the AI.
            var label = (request.NewLabel ?? string.Empty).Trim();
            if (label.Length is 0 or > 60)
            {
                throw new KernelException("invalid_label", "Name the new figure (1–60 characters).", 400, "newLabel");
            }

            var entity = config.FindEntity(view.Entity)!;
            if (request.GroupBy is { } field && (entity.FindField(field) is not { } groupField || !ConfigValidator.IsGroupable(groupField) || perms.FieldAccess(entity, field) == FieldAccessLevels.Hidden))
            {
                throw new KernelException("invalid_group", $"{entity.Label} cannot be grouped by '{field}'.", 400, "groupBy", "Group by a choice, status, priority, user, group or toggle field you can see.");
            }

            var open = config.WorkflowFor(entity.Id) is { } workflow
                ? workflow.States.Where(s => s.Category is "resolved" or "closed" or "cancelled").Select(s => s.Id).ToList()
                : [];
            metricId = $"{Namespaces.TenantKeyFromLabel(label)}_{Guid.NewGuid().ToString("N")[..6]}";
            ops.Add(new AddMetric
            {
                Metric = new MetricSpec
                {
                    Id = metricId,
                    Label = label,
                    Entity = entity.Id,
                    Filter = request.OpenOnly && open.Count > 0 ? $"!(record.status in [{string.Join(", ", open.Select(s => $"'{s}'"))}])" : null,
                    GroupBy = request.GroupBy is null ? null : new MetricGroupSpec { Field = request.GroupBy },
                },
            });
        }
        else if (config.FindMetric(metricId) is not { } existing || existing.Entity != view.Entity)
        {
            throw new KernelException("invalid_metric", $"'{metricId}' is not a metric of this dashboard's records.", 400, "metric", "Pick a metric from the list, or define a new figure.");
        }

        var props = new System.Text.Json.Nodes.JsonObject { ["metric"] = metricId };
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            props[request.Kind == "kpi" ? "label" : "title"] = request.Title.Trim();
        }

        var tile = new NodeSpec { Id = Namespaces.NewTenantNodeId(), Type = DashboardLayout.TileType(request.Kind), Props = props };
        ops.AddRange(DashboardLayout.AddTile(view, tile, request.Row));
        return await drafts.ApplyBatchAsync(ctx, ops, request.ExpectedVersion, "ui", ct);
    }

    public async Task<BatchResult> RemoveTileAsync(RequestContext ctx, string viewId, string tileId, int expectedVersion, CancellationToken ct)
    {
        var (config, _, _, _) = await preview.WorkingConfigAsync(ctx, ct);
        return await drafts.ApplyBatchAsync(ctx, DashboardLayout.RemoveTile(Dashboard(config, viewId), tileId), expectedVersion, "ui", ct);
    }

    public async Task<BatchResult> RemoveAsync(RequestContext ctx, string viewId, int expectedVersion, CancellationToken ct)
    {
        var (config, _, _, _) = await preview.WorkingConfigAsync(ctx, ct);
        return await drafts.ApplyBatchAsync(ctx, [new RemoveView { View = Dashboard(config, viewId).Id }], expectedVersion, "ui", ct);
    }

    private static ViewSpec Dashboard(EffectiveConfig config, string viewId) =>
        config.FindView(viewId) is { Kind: ViewKinds.Dashboard } view ? view : throw KernelException.NotFound($"Dashboard '{viewId}'");
}
