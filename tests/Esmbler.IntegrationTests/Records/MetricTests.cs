using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Metrics;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Infrastructure.Sla;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Records;

/// <summary>
/// REQ-WS-003, REQ-WS-004, REQ-OPS-004 (ADR 0019): every figure equals the explorer total of the query it drills into,
/// for every persona; figures follow permissions (restricted, never widened); SLA breaches count from recorded
/// breaches; group counts match; workspaces stay isolated.
/// </summary>
[Collection(PlatformCollection.Name)]
public class MetricTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";
    private const string Dashboard = "core.tickets.dashboard";

    private MetricService Metrics => f.Get<MetricService>();

    private RecordService Records => f.Get<RecordService>();

    private async Task<ProvisionedTenant> TenantWithTicketsAsync(string prefix)
    {
        var t = await f.NewTenantAsync(prefix, "1.6.0");
        var agent = f.As(t, "agent");
        var dana = t.Users["dana"].ToString();
        await Records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "VPN down", ["requester"] = dana, ["priority"] = "p1", ["impact"] = "high", ["category"] = "network" }, null, null, default);
        await Records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "New mouse", ["requester"] = dana, ["category"] = "hardware", ["assignment_group"] = t.Groups["service_desk"].ToString() }, null, null, default);
        await Records.CreateAsync(f.As(t, "jordan"), Ticket, new JsonObject { ["title"] = "Password reset", ["category"] = "access" }, null, null, default);
        var resolved = await Records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Printer jam", ["requester"] = dana, ["category"] = "hardware" }, null, null, default);
        var started = await Records.TransitionAsync(agent, Ticket, resolved.Id, resolved.Revision, "start", [], null, null, default);
        await Records.TransitionAsync(agent, Ticket, resolved.Id, started.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Cleared the jam" }, null, null, default);
        return t;
    }

    private async Task<long> ExplorerTotalAsync(RequestContext ctx, DrillQuery drill) =>
        (await Records.QueryAsync(ctx, new RecordQuery { Entity = drill.Entity, Filters = drill.Filters, Sla = [.. drill.Sla.Select(SlaFilter.Parse)], PageSize = 1 }, default)).Total;

    [Fact]
    public async Task Every_figure_equals_the_explorer_total_it_drills_into_for_every_persona()
    {
        var t = await TenantWithTicketsAsync("met");
        foreach (var persona in new[] { "admin", "agent", "sam", "builder", "dana", "jordan", "quinn" })
        {
            var ctx = f.As(t, persona);
            DashboardData data;
            try
            {
                data = await Metrics.DashboardAsync(ctx, Dashboard, "UTC", default);
            }
            catch (KernelException ex) when (ex.Status == 404)
            {
                continue; // people who cannot read tickets have no dashboard
            }

            foreach (var metric in data.Metrics.Where(m => m.Status == "ok"))
            {
                Assert.Equal(await ExplorerTotalAsync(ctx, metric.Drill!), metric.Count);
                foreach (var bucket in metric.Buckets)
                {
                    Assert.True(await ExplorerTotalAsync(ctx, bucket.Drill!) == bucket.Count, $"{persona}: {metric.Id} / {bucket.Label}");
                }
            }
        }
    }

    [Fact]
    public async Task Requesters_count_only_their_tickets_and_team_figures_are_restricted_for_them()
    {
        var t = await TenantWithTicketsAsync("metr");
        var agentOpen = (await Metrics.EvaluateAsync(f.As(t, "agent"), "core.tickets.metric.open", null, default)).Count;
        var dana = f.As(t, "dana");
        var open = await Metrics.EvaluateAsync(dana, "core.tickets.metric.open", null, default);
        // Dana counts only open tickets she is the requester of (sample data includes some), never Jordan's.
        var hers = await Records.QueryAsync(dana, new RecordQuery { Entity = Ticket, Filters = ["!(record.status in ['resolved', 'closed', 'cancelled'])", $"record.requester == '{t.Users["dana"]}'"], PageSize = 1 }, default);
        Assert.Equal(("ok", (long)hers.Total), (open.Status, open.Count));
        Assert.True(open.Count < agentOpen);

        var workload = await Metrics.EvaluateAsync(dana, "core.tickets.metric.workload_by_team", null, default);
        Assert.Equal("restricted", workload.Status);
        Assert.Empty(workload.Buckets);
        Assert.Null(workload.Value);

        // The dashboard the requester gets has no restricted tiles.
        var view = await f.Get<ExperienceService>().ViewAsync(dana, Dashboard, default);
        var shown = view.View.Layout.Descendants().Select(n => n.StringProp("metric")).OfType<string>().ToList();
        Assert.Contains("core.tickets.metric.open", shown);
        Assert.DoesNotContain("core.tickets.metric.workload_by_team", shown);
        var app = await f.Get<ExperienceService>().AppAsync(dana, default);
        Assert.Contains(app.Dashboards!, d => d.View == Dashboard);
    }

    [Fact]
    public async Task Figures_and_group_counts_over_a_hidden_field_are_refused_and_left_out()
    {
        // Threat model (ADR 0019): a figure never aggregates a field the viewer cannot see, and restricted figures
        // are not even named in dashboard data.
        var t = await TenantWithTicketsAsync("meth");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var applied = await drafts.ApplyBatchAsync(admin,
        [
            new SetFieldAccess { PermissionSet = "core.tickets.ps_agent", Entity = Ticket, Field = "impact", Access = "hidden" },
            new AddMetric { Metric = new MetricSpec { Id = "tenant__metric_by_impact", Label = "By impact", Entity = Ticket, GroupBy = new MetricGroupSpec { Field = "impact" } } },
        ], draft.Version, "ui", default);
        await f.Get<PublishService>().PublishAsync(admin, applied.Draft.ArtifactHash!, applied.Draft.PublishedRevision, default);

        var agent = f.As(t, "agent");
        var byImpact = await Metrics.EvaluateAsync(agent, "tenant__metric_by_impact", null, default);
        Assert.Equal(("restricted", (double?)null, 0), (byImpact.Status, byImpact.Value, byImpact.Buckets.Count));
        Assert.Equal("ok", (await Metrics.EvaluateAsync(admin, "tenant__metric_by_impact", null, default)).Status);

        var group = await Assert.ThrowsAsync<KernelException>(() => Metrics.GroupsAsync(agent, new RecordQuery { Entity = Ticket }, "impact", default));
        Assert.Equal("invalid_group", group.Code);
        await Assert.ThrowsAsync<KernelException>(() => Metrics.GroupsAsync(agent, new RecordQuery { Entity = Ticket, Filters = ["record.impact == 'high'"] }, "priority", default));

        // Requesters' dashboard data leaves out the team figures entirely.
        var data = await Metrics.DashboardAsync(f.As(t, "dana"), Dashboard, null, default);
        Assert.DoesNotContain(data.Metrics, m => m.Id == "core.tickets.metric.workload_by_team" || m.Status == "restricted");
    }

    [Fact]
    public async Task Reports_group_and_filter_by_a_custom_field_and_drill_into_it()
    {
        // REQ-OPS-004: basic reports over custom fields, with the same agreement as base fields.
        var t = await TenantWithTicketsAsync("metc");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var applied = await drafts.ApplyBatchAsync(admin,
        [
            new AddChoiceList { ChoiceList = new ChoiceListSpec { Id = "tenant__vendor_options", Label = "Vendor", Options = [new ChoiceOption { Id = "contoso", Label = "Contoso" }, new ChoiceOption { Id = "fabrikam", Label = "Fabrikam" }] } },
            new AddField { Entity = Ticket, Field = new FieldSpec { Key = "tenant__vendor", Type = FieldTypes.SingleSelect, Label = "Vendor", ChoiceList = "tenant__vendor_options" } },
            new AddMetric { Metric = new MetricSpec { Id = "tenant__metric_by_vendor", Label = "Open by vendor", Entity = Ticket, Filter = "!(record.status in ['resolved', 'closed', 'cancelled']) && has(record.tenant__vendor)", GroupBy = new MetricGroupSpec { Field = "tenant__vendor" } } },
        ], draft.Version, "ui", default);
        await f.Get<PublishService>().PublishAsync(admin, applied.Draft.ArtifactHash!, applied.Draft.PublishedRevision, default);

        var agent = f.As(t, "agent");
        var dana = t.Users["dana"].ToString();
        foreach (var vendor in new[] { "contoso", "contoso", "fabrikam" })
        {
            await Records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = $"Broken {vendor} part", ["requester"] = dana, ["impact"] = "low", ["tenant__vendor"] = vendor }, null, null, default);
        }

        var byVendor = await Metrics.EvaluateAsync(agent, "tenant__metric_by_vendor", null, default);
        Assert.Equal(("ok", 3L), (byVendor.Status, byVendor.Count));
        Assert.Equal([("Contoso", 2L), ("Fabrikam", 1L)], byVendor.Buckets.Select(b => (b.Label, b.Count)));
        foreach (var bucket in byVendor.Buckets)
        {
            Assert.Equal(bucket.Count, await ExplorerTotalAsync(agent, bucket.Drill!));
        }
    }

    [Fact]
    public async Task Changing_who_sees_a_metric_or_undoing_a_permission_change_needs_permission_administration()
    {
        var t = await TenantWithTicketsAsync("metp");
        var drafts = f.Get<DraftService>();
        var admin = f.As(t, "admin");
        var builder = f.As(t, "builder");
        var draft = await drafts.OpenAsync(admin, default);
        var widen = await Assert.ThrowsAsync<KernelException>(() => drafts.ApplyAsync(builder, new UpdateMetric { Metric = "core.tickets.metric.workload_by_team", Set = new MetricPatch { Roles = [] } }, draft.Version, "ui", default));
        Assert.Equal(403, widen.Status);

        var hidden = await drafts.ApplyAsync(admin, new SetFieldAccess { PermissionSet = "core.tickets.ps_requester", Entity = Ticket, Field = "category", Access = "hidden" }, draft.Version, "ui", default);
        var undo = await Assert.ThrowsAsync<KernelException>(() => drafts.SetUndoneAsync(builder, hidden.Seq, true, hidden.Version, default));
        Assert.Equal(403, undo.Status);
        Assert.True((await drafts.SetUndoneAsync(admin, hidden.Seq, true, hidden.Version, default)).Steps.Single().Undone);
    }

    [Fact]
    public async Task Workload_groups_match_the_explorer_group_counts()
    {
        var t = await TenantWithTicketsAsync("metg");
        var agent = f.As(t, "agent");
        var workload = await Metrics.EvaluateAsync(agent, "core.tickets.metric.workload_by_team", null, default);
        var groups = await Metrics.GroupsAsync(agent, new RecordQuery { Entity = Ticket, Filters = workload.Drill!.Filters }, "assignment_group", default);
        foreach (var bucket in workload.Buckets)
        {
            var group = Assert.Single(groups, g => (g.Key ?? string.Empty) == bucket.Key);
            Assert.Equal(bucket.Count, group.Count);
        }

        // Choice fields group in option order, like the explorer sorts them.
        var byPriority = await Metrics.GroupsAsync(agent, new RecordQuery { Entity = Ticket, Filters = workload.Drill!.Filters }, "priority", default);
        string[] order = ["p1", "p2", "p3", "p4"];
        Assert.Equal(byPriority.Select(g => g.Key).OrderBy(k => Array.IndexOf(order, k)), byPriority.Select(g => g.Key));
        Assert.Equal("Critical", byPriority.Single(g => g.Key == "p1").Label);

        Assert.Contains(workload.Buckets, b => b.Label == "Service Desk");
        Assert.Contains(workload.Buckets, b => b.Label == "No assignment group");
    }

    [Fact]
    public async Task Sla_breaches_count_recorded_breaches_and_drill_into_them()
    {
        var t = await TenantWithTicketsAsync("mets");
        var agent = f.As(t, "agent");
        Assert.Equal(0, (await Metrics.EvaluateAsync(agent, "core.tickets.metric.sla_breached", null, default)).Count);

        // The P1 ticket's response clock is due in 30 minutes; make its check due now and run it.
        var db = f.Get<Database>();
        var timer = await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, x => x.SlaTimers.Where(s => s.TargetId == "response" && s.GoalSeconds == 1800).Select(s => s.Id).FirstAsync(), default);
        var past = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.InTenantAsync(t.TenantId, t.Environments["prod"], null, async x =>
        {
            await x.SlaTimers.Where(s => s.Id == timer).ExecuteUpdateAsync(s => s.SetProperty(r => r.DueAt, past).SetProperty(r => r.WarnAt, (DateTimeOffset?)null));
            return await x.Jobs.Where(j => j.DedupKey == SlaService.JobKey(timer) && j.Status == "pending").ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, past));
        }, default);
        var runner = f.Get<JobRunner>();
        while (await runner.RunOnceAsync(default) > 0)
        {
        }

        var breached = await Metrics.EvaluateAsync(agent, "core.tickets.metric.sla_breached", null, default);
        Assert.Equal(1, breached.Count);
        var listed = await Records.QueryAsync(agent, new RecordQuery { Entity = Ticket, Filters = breached.Drill!.Filters, Sla = [.. breached.Drill.Sla.Select(SlaFilter.Parse)] }, default);
        Assert.Equal("VPN down", Assert.Single(listed.Items).Data["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Figures_never_include_another_workspace()
    {
        var a = await TenantWithTicketsAsync("meta");
        var before = (await Metrics.EvaluateAsync(f.As(a, "agent"), "core.tickets.metric.open", null, default)).Count;
        await TenantWithTicketsAsync("metb");
        Assert.Equal(before, (await Metrics.EvaluateAsync(f.As(a, "agent"), "core.tickets.metric.open", null, default)).Count);
        var foreign = await Assert.ThrowsAsync<KernelException>(() => Metrics.DashboardAsync(f.As(f.Globex, "morgan"), "tenant__nope", null, default));
        Assert.Equal(404, foreign.Status);
    }

    [Fact]
    public async Task Averages_and_trends_cover_exactly_the_records_they_drill_into()
    {
        var t = await TenantWithTicketsAsync("metv");
        var agent = f.As(t, "agent");
        var average = await Metrics.EvaluateAsync(agent, "core.tickets.metric.avg_resolution", "UTC", default);
        Assert.Equal(("ok", 1L), (average.Status, average.Count));
        Assert.NotNull(average.Value);
        Assert.True(average.Value >= 0);
        Assert.Equal("hours", average.Unit);

        var trend = await Metrics.EvaluateAsync(agent, "core.tickets.metric.resolved_trend", null, default);
        Assert.Equal(8, trend.Buckets.Count);
        Assert.Equal(1, trend.Buckets.Sum(b => b.Count));
        Assert.Equal(1, trend.Buckets[^1].Count); // resolved this week
    }
}
