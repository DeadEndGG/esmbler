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
                    Assert.True(await ExplorerTotalAsync(ctx, bucket.Drill) == bucket.Count, $"{persona}: {metric.Id} / {bucket.Label}");
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
