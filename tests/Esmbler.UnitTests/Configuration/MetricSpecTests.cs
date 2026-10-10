using Esmbler.Kernel.Configuration;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using static Esmbler.UnitTests.Configuration.CompilerFixture;

namespace Esmbler.UnitTests.Configuration;

/// <summary>REQ-WS-004, REQ-WS-002, REQ-OPS-004: reusable metric definitions, dashboards and their validation (ADR 0019).</summary>
public class MetricSpecTests
{
    private const string Ticket = "core.tickets.ticket";

    private static MetricSpec Vendors() => new()
    {
        Id = "tenant__p1_by_category",
        Label = "P1 by category",
        Entity = Ticket,
        Filter = "record.priority == 'p1'",
        GroupBy = new MetricGroupSpec { Field = "category" },
    };

    [Fact]
    public void Release_1_6_0_ships_an_overview_dashboard_built_from_shared_metrics()
    {
        var result = Compile("1.6.0");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        var cfg = result.Config;
        Assert.Equal(7, cfg.Metrics.Count);
        var dashboard = cfg.FindView("core.tickets.dashboard")!;
        Assert.Equal(ViewKinds.Dashboard, dashboard.Kind);
        Assert.Equal(7, dashboard.Layout.Descendants().Count(n => n.StringProp("metric") is not null));

        // The explorer's saved views use the same definitions as the tiles.
        var views = cfg.FindView("core.tickets.list")!.Layout.Descendants().Single(n => n.Type == "collection.saved_views");
        Assert.Contains(views.Props["views"]!.AsArray(), v => v!["metric"]?.GetValue<string>() == "core.tickets.metric.unassigned");

        // Removing a field shows the figures it feeds.
        var graph = DependencyGraph.Build(cfg);
        Assert.Contains("core.tickets.metric.workload_by_team", graph.ImpactOf($"{Ticket}/assignment_group"));
        Assert.Contains("core.tickets.dashboard.workload", graph.ImpactOf("core.tickets.metric.workload_by_team"));
    }

    [Fact]
    public void Tenants_add_metrics_and_tiles_and_change_default_metrics()
    {
        var tile = new InsertNode
        {
            View = "core.tickets.dashboard",
            Parent = "core.tickets.dashboard.c_category",
            Node = new NodeSpec { Id = "tenant__n_p1", Type = "display.bar_chart", Props = new() { ["metric"] = "tenant__p1_by_category" } },
        };
        var result = Compile("1.6.0", new AddMetric { Metric = Vendors() }, tile, new UpdateMetric { Metric = "core.tickets.metric.open", Set = new MetricPatch { Label = "Still open" } });
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        Assert.Equal("Still open", result.Config.FindMetric("core.tickets.metric.open")!.Label);
        Assert.Contains(ConfigDiff.Compare(Compile("1.6.0").Config, result.Config), c => c.Kind == "metric" && c.Id == "tenant__p1_by_category");
    }

    [Fact]
    public void Shown_and_default_metrics_cannot_be_removed()
    {
        string Last(params OverlayOp[] ops) => Compile("1.6.0", ops).Composition.Outcomes[^1].Status;
        Assert.Equal(OpStatus.Failed, Last(new RemoveMetric { Metric = "core.tickets.metric.open" }));
        Assert.Equal(OpStatus.Applied, Last(new AddMetric { Metric = Vendors() }, new RemoveMetric { Metric = "tenant__p1_by_category" }));
        var tile = new InsertNode
        {
            View = "core.tickets.dashboard",
            Parent = "core.tickets.dashboard.c_category",
            Node = new NodeSpec { Id = "tenant__n_p1", Type = "display.bar_chart", Props = new() { ["metric"] = "tenant__p1_by_category" } },
        };
        Assert.Equal(OpStatus.Failed, Last(new AddMetric { Metric = Vendors() }, tile, new RemoveMetric { Metric = "tenant__p1_by_category" }));
    }

    [Fact]
    public void Metrics_are_validated_with_suggestions()
    {
        var bad = Vendors();
        bad.Filter = "prior.status == 'new'";
        bad.GroupBy = new MetricGroupSpec { Field = "title" };
        bad.Measure = new MetricMeasureSpec { Kind = "median" };
        bad.Roles = ["core.tickets.agnt"];
        bad.Sla = new MetricSlaSpec { Policy = "core.tickets.sla.request" };
        var result = Compile("1.6.0", new AddMetric { Metric = bad });
        Assert.Contains(result.Errors, e => e.Code == "config.bad_filter" || e.Message.Contains("prior", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Code == "config.bad_group");
        Assert.Contains(result.Errors, e => e.Code == "config.bad_measure");
        Assert.Contains(result.Errors, e => e.Code == "config.not_found" && e.Suggestion == "core.tickets.agent");
        Assert.Contains(result.Errors, e => e.Code == "config.bad_sla");

        var sum = Vendors();
        sum.GroupBy = null;
        sum.Measure = new MetricMeasureSpec { Kind = MetricMeasures.Sum, Field = "title" };
        Assert.Contains(Compile("1.6.0", new AddMetric { Metric = sum }).Errors, e => e.Code == "config.bad_measure");

        var buckets = Vendors();
        buckets.GroupBy = new MetricGroupSpec { Field = "created_at", Bucket = "day" };
        buckets.DateRange = new MetricDateRangeSpec { Field = "created_at", Last = 90 };
        Assert.Contains(Compile("1.6.0", new AddMetric { Metric = buckets }).Errors, e => e.Code == "config.too_large");

        // estimated_effort is hidden from requesters: allowed, with a warning that they see the figure as restricted.
        var effort = Vendors();
        effort.GroupBy = null;
        effort.Measure = new MetricMeasureSpec { Kind = MetricMeasures.Avg, Field = "effort_hours" };
        effort.Filter = "has(record.estimated_effort)";
        Assert.Contains(Compile("1.6.0", new AddMetric { Metric = effort }).Diagnostics, d => d.Code == "metric.restricted_field");
    }

    [Fact]
    public void Dashboards_show_figures_and_tiles_match_their_metric()
    {
        Assert.Contains(Compile("1.6.0", new InsertNode
        {
            View = "core.tickets.dashboard",
            Parent = "core.tickets.dashboard.c_category",
            Node = new NodeSpec { Id = "tenant__n_table", Type = "collection.data_table", Props = new() { ["entity"] = Ticket } },
        }).Errors, e => e.Code is "layout.bad_dashboard" or "layout.bad_parent");

        Assert.Contains(Compile("1.6.0", new InsertNode
        {
            View = "core.tickets.dashboard",
            Parent = "core.tickets.dashboard.c_category",
            Node = new NodeSpec { Id = "tenant__n_kpi", Type = "display.kpi_tile", Props = new() { ["metric"] = "core.tickets.metric.workload_by_team" } },
        }).Errors, e => e.Code == "layout.bad_metric");

        Assert.Contains(Compile("1.6.0", new InsertNode
        {
            View = "core.tickets.dashboard",
            Parent = "core.tickets.dashboard.c_category",
            Node = new NodeSpec { Id = "tenant__n_kpi", Type = "display.kpi_tile", Props = new() { ["metric"] = "core.tickets.metric.opn" } },
        }).Errors, e => e.Code == "config.not_found" && e.Suggestion == "core.tickets.metric.open");
    }
}
