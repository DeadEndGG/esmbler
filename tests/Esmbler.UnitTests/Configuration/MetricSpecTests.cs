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
        Assert.Contains(result.Errors, e => e.Code == "config.bad_filter");
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
    public void Release_1_10_0_adds_service_level_figures_and_changes_a_policy_and_its_calendar()
    {
        var result = Compile("1.10.0");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        var cfg = result.Config;
        Assert.Equal(MetricSlaStates.AtRisk, cfg.FindMetric("core.tickets.metric.sla_at_risk")!.Sla!.State);
        Assert.NotNull(cfg.FindMetric("core.tickets.metric.alert_mtta")!.Alerts);
        Assert.Equal(MetricAlertFields.ResolvedAt, cfg.FindMetric("core.tickets.metric.alert_mttr")!.Measure.To);
        Assert.Equal(10, cfg.FindView("core.tickets.dashboard")!.Layout.Descendants().Count(n => n.StringProp("metric") is not null));

        // The policy and calendar change shows in the upgrade's base changes.
        var changes = ConfigDiff.Compare(Compile("1.9.0").Config, cfg);
        Assert.Contains(changes, c => c.Id == "core.tickets.sla.ticket");
        Assert.Contains(changes, c => c.Id == "core.tickets.calendar.business_hours");
        var p2 = cfg.FindSlaPolicy("core.tickets.sla.ticket")!.FindTarget("resolution")!.Goals.Single(g => g.When == "record.priority == 'p2'");
        Assert.Equal(480, p2.Minutes);
    }

    private static ViewSpec NewDashboard(string id = "tenant__dash_vendors") => new()
    {
        Id = id,
        Kind = ViewKinds.Dashboard,
        Entity = Ticket,
        Label = "Vendors",
        Layout = new NodeSpec
        {
            Id = id,
            Type = "layout.page",
            Props = new() { ["title"] = "Vendors" },
            Children =
            [
                new NodeSpec
                {
                    Id = "tenant__n_s",
                    Type = "layout.section",
                    Children =
                    [
                        new NodeSpec
                        {
                            Id = "tenant__n_r",
                            Type = "layout.row",
                            Children = [new NodeSpec { Id = "tenant__n_c", Type = "layout.column", Props = new() { ["span"] = 12 }, Children = [new NodeSpec { Id = "tenant__n_t", Type = "display.bar_chart", Props = new() { ["metric"] = "tenant__p1_by_category" } }] }],
                        },
                    ],
                },
            ],
        },
    };

    [Fact]
    public void Tenants_add_and_remove_their_own_dashboards()
    {
        // REQ-WS-005: a new dashboard is one reviewable change with its metric and tiles.
        var result = Compile("1.10.0", new AddMetric { Metric = Vendors() }, new AddView { View = NewDashboard() });
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
        Assert.Equal(ViewKinds.Dashboard, result.Config.FindView("tenant__dash_vendors")!.Kind);
        Assert.Contains(ConfigDiff.Compare(Compile("1.10.0").Config, result.Config), c => c.Kind == "view" && c.Id == "tenant__dash_vendors" && c.Change == "added");
        Assert.Contains("tenant__n_t", DependencyGraph.Build(result.Config).ImpactOf("tenant__p1_by_category"));

        // Removing it frees its metric; tiles must match their metric like on module dashboards.
        var removed = Compile("1.10.0", new AddMetric { Metric = Vendors() }, new AddView { View = NewDashboard() }, new RemoveView { View = "tenant__dash_vendors" }, new RemoveMetric { Metric = "tenant__p1_by_category" });
        Assert.True(removed.Succeeded, string.Join("\n", removed.Errors.Select(e => e.Message)));
        var kpi = NewDashboard();
        kpi.Layout.Children[0].Children[0].Children[0].Children[0].Type = "display.kpi_tile";
        Assert.False(Compile("1.10.0", new AddMetric { Metric = Vendors() }, new AddView { View = kpi }).Succeeded);
    }

    [Theory]
    [InlineData("core.tickets.dash_x", "dashboard", "overlay.namespace")]
    [InlineData("tenant__list_x", "list", "overlay.unsupported")]
    [InlineData("tenant__dash_x", "dashboard", "overlay.duplicate")]
    public void Added_views_are_tenant_dashboards_with_new_ids(string id, string kind, string code)
    {
        var view = NewDashboard(id);
        view.Kind = kind;
        var ops = new List<OverlayOp> { new AddMetric { Metric = Vendors() }, new AddView { View = view } };
        if (code == "overlay.duplicate")
        {
            ops.Insert(1, new AddView { View = NewDashboard(id) });
        }

        Assert.Contains(Compile("1.10.0", [.. ops]).Errors, e => e.Code == code);
    }

    [Fact]
    public void A_new_dashboard_can_start_empty()
    {
        var empty = NewDashboard();
        empty.Layout.Children[0].Children.Clear();
        var result = Compile("1.10.0", new AddView { View = empty });
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Module_dashboards_cannot_be_removed()
    {
        Assert.Contains(Compile("1.10.0", new RemoveView { View = "core.tickets.dashboard" }).Errors, e => e.Code == "overlay.base_immutable");
    }

    [Fact]
    public void At_risk_and_alert_metrics_are_validated()
    {
        // At risk is an SLA state like breached; anything else is refused with the states as the fix.
        var atRisk = new MetricSpec { Id = "tenant__at_risk", Label = "At risk", Entity = Ticket, Sla = new MetricSlaSpec { Policy = "core.tickets.sla.ticket", State = MetricSlaStates.AtRisk } };
        Assert.True(Compile("1.9.0", new AddMetric { Metric = atRisk }).Succeeded);
        atRisk.Sla.State = "late";
        Assert.Contains(Compile("1.9.0", new AddMetric { Metric = atRisk }).Errors, e => e.Code == "config.bad_sla" && e.Suggestion == "breached, at_risk");

        // Time to acknowledge over alerts, grouped by severity, dated by when alerts opened.
        var mtta = new MetricSpec
        {
            Id = "tenant__mtta",
            Label = "Time to acknowledge",
            Entity = Ticket,
            Filter = "record.priority == 'p1'",
            Alerts = new MetricAlertSpec { Rules = ["core.tickets.alert.sla_breached"], MinSeverity = EventSeverities.High },
            Measure = new MetricMeasureSpec { Kind = MetricMeasures.Duration, From = MetricAlertFields.OpenedAt, To = MetricAlertFields.AcknowledgedAt, Unit = "minutes" },
            GroupBy = new MetricGroupSpec { Field = MetricAlertFields.Severity },
            DateRange = new MetricDateRangeSpec { Field = MetricAlertFields.OpenedAt, Last = 30 },
        };
        var ok = Compile("1.9.0", new AddMetric { Metric = mtta });
        Assert.True(ok.Succeeded, string.Join("\n", ok.Errors.Select(e => e.Message)));
        Assert.Contains("tenant__mtta", DependencyGraph.Build(ok.Config).ImpactOf("core.tickets.alert.sla_breached"));

        var bad = SpecJson.Clone(mtta);
        bad.Alerts = new MetricAlertSpec { Rules = ["core.tickets.alert.sla_breach"], MinSeverity = "urgent" };
        bad.Measure = new MetricMeasureSpec { Kind = MetricMeasures.Duration, From = "created_at", To = MetricAlertFields.ResolvedAt };
        bad.GroupBy = new MetricGroupSpec { Field = "category" };
        bad.DateRange = new MetricDateRangeSpec { Field = "created_at", Last = 30 };
        bad.Sla = new MetricSlaSpec { Policy = "core.tickets.sla.ticket" };
        var errors = Compile("1.9.0", new AddMetric { Metric = bad }).Errors;
        Assert.Contains(errors, e => e.Code == "config.not_found" && e.Suggestion == "core.tickets.alert.sla_breached");
        Assert.Contains(errors, e => e.Code == "config.bad_severity");
        Assert.Contains(errors, e => e.Code == "config.bad_measure");
        Assert.Contains(errors, e => e.Code == "config.bad_group");
        Assert.Contains(errors, e => e.Code == "config.bad_date_range");
        Assert.Contains(errors, e => e.Code == "config.bad_sla");

        var sum = SpecJson.Clone(mtta);
        sum.Measure = new MetricMeasureSpec { Kind = MetricMeasures.Sum, Field = "effort_hours" };
        Assert.Contains(Compile("1.9.0", new AddMetric { Metric = sum }).Errors, e => e.Code == "config.bad_measure");
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
