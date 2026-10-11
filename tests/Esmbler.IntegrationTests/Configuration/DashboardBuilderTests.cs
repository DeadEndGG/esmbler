using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Metrics;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;

namespace Esmbler.IntegrationTests.Configuration;

/// <summary>
/// REQ-WS-005, REQ-WS-002 (ADR 0019): the dashboard builder outside the AI. A builder adds a dashboard and a field, charts
/// the new field without the AI, and publishes it as one reviewable draft; rows stay on the 12-unit grid as tiles come
/// and go; people without configuration rights cannot change dashboards; module dashboards cannot be removed.
/// </summary>
[Collection(PlatformCollection.Name)]
public class DashboardBuilderTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";

    private DashboardBuilderService Builder => f.Get<DashboardBuilderService>();

    [Fact]
    public async Task A_builder_charts_a_new_field_on_a_new_dashboard_without_the_ai()
    {
        var t = await f.NewTenantAsync("dbb", "1.10.0");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var v = (await drafts.OpenAsync(admin, default)).Version;
        v = (await drafts.ApplyBatchAsync(admin,
        [
            new AddChoiceList { ChoiceList = new ChoiceListSpec { Id = "tenant__vendor_options", Label = "Vendor", Options = [new ChoiceOption { Id = "contoso", Label = "Contoso" }, new ChoiceOption { Id = "fabrikam", Label = "Fabrikam" }] } },
            new AddField { Entity = Ticket, Field = new FieldSpec { Key = "tenant__vendor", Type = FieldTypes.SingleSelect, Label = "Vendor", ChoiceList = "tenant__vendor_options" } },
        ], v, "ui", default)).Version;

        var (created, view) = await Builder.CreateAsync(admin, "Hardware vendors", Ticket, v, "ui", default);
        v = created.Version;
        var options = await Builder.OptionsAsync(admin, view, default);
        Assert.True(options.Added);
        Assert.Empty(options.Rows);
        Assert.Contains(options.Groupable, g => g.Key == "tenant__vendor"); // the new field is offered at once

        // A new grouped figure over the new field (metric and tile in one change), then two KPIs side by side.
        var chart = await Builder.AddTileAsync(admin, view, new DashboardTileRequest("bar", null, null, null, "Open by vendor", "tenant__vendor", true, v), default);
        Assert.Equal(2, chart.Steps.Count);
        Assert.NotNull(chart.Batch);
        v = chart.Version;
        v = (await Builder.AddTileAsync(admin, view, new DashboardTileRequest("kpi", "core.tickets.metric.open", null, "Open", null, null, false, v), default)).Version;
        var kpiRow = (await Builder.OptionsAsync(admin, view, default)).Rows[1].Id;
        v = (await Builder.AddTileAsync(admin, view, new DashboardTileRequest("kpi", "core.tickets.metric.sla_at_risk", kpiRow, null, null, null, false, v), default)).Version;
        var rows = (await Builder.OptionsAsync(admin, view, default)).Rows;
        Assert.Equal([1, 2], rows.Select(r => r.Tiles.Count));
        var (config, _, _, _) = await f.Get<PreviewService>().WorkingConfigAsync(admin, default);
        Assert.Equal([6, 6], config.FindView(view)!.Layout.Find(kpiRow)!.Children.Select(c => (int)c.Props["span"]!));

        // Wrong kinds and full rows are refused with a fix.
        var vendorMetric = rows[0].Tiles[0].Metric!;
        Assert.Contains("grouped", (await Assert.ThrowsAsync<KernelException>(() => Builder.AddTileAsync(admin, view, new DashboardTileRequest("kpi", vendorMetric, null, null, null, null, false, v), default))).Message, StringComparison.Ordinal);
        Assert.Equal("invalid_group", (await Assert.ThrowsAsync<KernelException>(() => Builder.AddTileAsync(admin, view, new DashboardTileRequest("bar", null, null, null, "By title", "title", false, v), default))).Code);

        // Removing one KPI re-spans the row; publishing makes the dashboard visible with figures that drill.
        v = (await Builder.RemoveTileAsync(admin, view, rows[1].Tiles[1].Id, v, default)).Version;
        (config, _, _, _) = await f.Get<PreviewService>().WorkingConfigAsync(admin, default);
        Assert.Equal([12], config.FindView(view)!.Layout.Find(kpiRow)!.Children.Select(c => (int)c.Props["span"]!));
        var state = await drafts.CurrentAsync(admin, default);
        await f.Get<PublishService>().PublishAsync(admin, state!.ArtifactHash!, state.PublishedRevision, default);

        var agent = f.As(t, "agent");
        var records = f.Get<RecordService>();
        await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Contoso dock", ["requester"] = t.Users["dana"].ToString(), ["tenant__vendor"] = "contoso" }, null, null, default);
        Assert.Contains((await f.Get<ExperienceService>().AppAsync(agent, default)).Dashboards!, d => d.View == view);
        var data = await f.Get<MetricService>().DashboardAsync(agent, view, "UTC", default);
        var byVendor = data.Metrics.Single(m => m.Id == vendorMetric);
        var contoso = Assert.Single(byVendor.Buckets, b => b.Label == "Contoso");
        Assert.Equal(contoso.Count, (await records.QueryAsync(agent, new RecordQuery { Entity = Ticket, Filters = contoso.Drill!.Filters, PageSize = 1 }, default)).Total);
    }

    [Fact]
    public async Task Only_builders_change_dashboards_and_module_dashboards_stay()
    {
        var t = await f.NewTenantAsync("dbr", "1.10.0");
        var admin = f.As(t, "admin");
        var v = (await f.Get<DraftService>().OpenAsync(admin, default)).Version;
        foreach (var persona in new[] { "agent", "dana" })
        {
            var refused = await Assert.ThrowsAsync<KernelException>(() => Builder.CreateAsync(f.As(t, persona), "Mine", Ticket, v, "ui", default));
            Assert.Equal(403, refused.Status);
        }

        // Adding and removing tiles is configuration editing too; other workspaces' dashboards do not exist here.
        foreach (var persona in new[] { "agent", "dana" })
        {
            Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Builder.AddTileAsync(f.As(t, persona), "core.tickets.dashboard", new DashboardTileRequest("kpi", "core.tickets.metric.open", null, null, null, null, false, v), default))).Status);
            Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Builder.RemoveTileAsync(f.As(t, persona), "core.tickets.dashboard", "core.tickets.dashboard.kpi_open", v, default))).Status);
        }

        var other = await f.NewTenantAsync("dbo", "1.10.0");
        var otherAdmin = f.As(other, "admin");
        var (otherCreated, otherView) = await Builder.CreateAsync(otherAdmin, "Theirs", Ticket, (await f.Get<DraftService>().OpenAsync(otherAdmin, default)).Version, "ui", default);
        Assert.Equal(404, (await Assert.ThrowsAsync<KernelException>(() => Builder.OptionsAsync(admin, otherView, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<KernelException>(() => Builder.AddTileAsync(admin, otherView, new DashboardTileRequest("kpi", "core.tickets.metric.open", null, null, null, null, false, v), default))).Status);
        Assert.True(otherCreated.Version > 0);

        // Bad rows, full rows, names and metrics are refused with a fix.
        var row = (await Builder.OptionsAsync(admin, "core.tickets.dashboard", default)).Rows[0];
        Assert.Equal("row_full", (await Assert.ThrowsAsync<KernelException>(() => Builder.AddTileAsync(admin, "core.tickets.dashboard", new DashboardTileRequest("kpi", "core.tickets.metric.open", row.Id, null, null, null, false, v), default))).Code);
        Assert.Equal("invalid_row", (await Assert.ThrowsAsync<KernelException>(() => Builder.AddTileAsync(admin, "core.tickets.dashboard", new DashboardTileRequest("kpi", "core.tickets.metric.open", "core.tickets.dashboard.kpi_open", null, null, null, false, v), default))).Code);
        Assert.Equal("invalid_label", (await Assert.ThrowsAsync<KernelException>(() => Builder.AddTileAsync(admin, "core.tickets.dashboard", new DashboardTileRequest("kpi", null, null, null, " ", null, false, v), default))).Code);
        Assert.Equal("invalid_label", (await Assert.ThrowsAsync<KernelException>(() => Builder.CreateAsync(admin, "", Ticket, v, "ui", default))).Code);
        Assert.Equal("invalid_metric", (await Assert.ThrowsAsync<KernelException>(() => Builder.AddTileAsync(admin, "core.tickets.dashboard", new DashboardTileRequest("kpi", "core.tickets.metric.nope", null, null, null, null, false, v), default))).Code);

        Assert.Equal("overlay.base_immutable", (await Assert.ThrowsAsync<KernelException>(() => Builder.RemoveAsync(admin, "core.tickets.dashboard", v, default))).Code);
        var (created, view) = await Builder.CreateAsync(f.As(t, "builder"), "Team view", Ticket, v, "ui", default);
        var removed = await Builder.RemoveAsync(f.As(t, "builder"), view, created.Version, default);
        var (config, _, _, _) = await f.Get<PreviewService>().WorkingConfigAsync(admin, default);
        Assert.Null(config.FindView(view));
        Assert.True(removed.Version > created.Version);
    }
}
