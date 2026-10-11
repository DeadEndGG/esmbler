using System.Net;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Metrics;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Api;

/// <summary>
/// REQ-OPS-004 (ADR 0019): CSV export of explorer results and figures. A file holds exactly the records the explorer
/// counts for the person (same WHERE clause), never fields hidden from them, escapes formulas, is audited before it is
/// written, and is refused to AI agents; a figure exports its bars as the dashboard shows them.
/// </summary>
[Collection(PlatformCollection.Name)]
public class ExportTests(PlatformFixture platform) : IClassFixture<ApiTestsFactoryHolder>
{
    private const string Ticket = "core.tickets.ticket";

    private ApiFactory Factory => ApiTestsFactoryHolder.Get(platform);

    private static string Slug(ProvisionedTenant t) => PlatformFixture.Slugs[t.TenantId];

    private static List<string> Lines(string csv) => [.. csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];

    [Fact]
    public async Task Explorer_export_holds_exactly_the_listed_records_with_visible_fields_only()
    {
        var t = await platform.NewTenantAsync("exp", "1.10.0");
        var records = platform.Get<RecordService>();
        var agent = platform.As(t, "agent");
        var dana = t.Users["dana"].ToString();
        await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "=HYPERLINK(\"http://evil.example\")", ["requester"] = dana, ["priority"] = "p2", ["category"] = "hardware" }, null, null, default);
        await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Printer, jammed", ["requester"] = dana, ["priority"] = "p2", ["category"] = "hardware" }, null, null, default);

        var filter = "record.category == 'hardware' && record.priority == 'p2'";
        var client = await Factory.SignInAsync($"agent@{Slug(t)}.example", Slug(t));
        var response = await client.GetAsync($"/api/records/{Ticket}/export?filter={Uri.EscapeDataString(filter)}&sort=title");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment;", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        var lines = Lines(await response.Content.ReadAsStringAsync());

        // Same records as the explorer total for the same filter; choice and people show as labels; formulas are escaped.
        var listed = await records.QueryAsync(agent, new RecordQuery { Entity = Ticket, Filters = [filter], PageSize = 1 }, default);
        Assert.Equal(listed.Total + 1, lines.Count);
        var header = lines[0].Split(',');
        Assert.Equal("Number", header[0]);
        Assert.Contains("Title", header);
        Assert.Contains(lines, l => l.Contains("\"'=HYPERLINK(\"\"http://evil.example\"\")\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("\"Printer, jammed\"", StringComparison.Ordinal) && l.Contains("Hardware", StringComparison.Ordinal) && l.Contains("Dana", StringComparison.Ordinal));

        // A requester's file has only their tickets and never the fields hidden from them.
        var requester = await Factory.SignInAsync($"dana@{Slug(t)}.example", Slug(t));
        var mine = Lines(await (await requester.GetAsync($"/api/records/{Ticket}/export")).Content.ReadAsStringAsync());
        var danaTotal = (await records.QueryAsync(platform.As(t, "dana"), new RecordQuery { Entity = Ticket, PageSize = 1 }, default)).Total;
        Assert.Equal(danaTotal + 1, mine.Count);
        Assert.DoesNotContain("Labels", mine[0].Split(','));
        Assert.Contains("Labels", header);

        // Filters on hidden fields are refused like in the list; the export is audited before it is written.
        Assert.Equal(HttpStatusCode.BadRequest, (await requester.GetAsync($"/api/records/{Ticket}/export?filter={Uri.EscapeDataString("has(record.labels)")}")).StatusCode);
        var audit = await platform.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().Where(a => a.Operation == "records.export").ToListAsync(), default);
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, a => a.Actor == t.Users["agent"] && a.Details!.Contains("hardware", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Figure_export_lists_each_bar_for_the_viewer_and_agents_cannot_export()
    {
        var t = await platform.NewTenantAsync("expm", "1.10.0");
        var client = await Factory.SignInAsync($"agent@{Slug(t)}.example", Slug(t));
        var response = await client.GetAsync("/api/metrics/core.tickets.metric.open_by_category/export?tz=UTC");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = Lines(await response.Content.ReadAsStringAsync());
        var figure = await platform.Get<MetricService>().EvaluateAsync(platform.As(t, "agent"), "core.tickets.metric.open_by_category", "UTC", default);
        Assert.Equal("Group,Value,Records", lines[0]);
        Assert.Equal(figure.Buckets.Select(b => $"{b.Label},{b.Count},{b.Count}"), lines.Skip(1));

        // An ungrouped figure is one row; a figure restricted to the viewer does not exist for them.
        var open = Lines(await (await client.GetAsync("/api/metrics/core.tickets.metric.open/export")).Content.ReadAsStringAsync());
        Assert.Equal(["Figure,Value,Records", $"Open,{(await platform.Get<MetricService>().EvaluateAsync(platform.As(t, "agent"), "core.tickets.metric.open", null, default)).Count},{open[1].Split(',')[2]}"], open);
        var requester = await Factory.SignInAsync($"dana@{Slug(t)}.example", Slug(t));
        Assert.Equal(HttpStatusCode.NotFound, (await requester.GetAsync("/api/metrics/core.tickets.metric.workload_by_team/export")).StatusCode);

        var agentCtx = platform.As(t, "admin", agent: Guid.NewGuid());
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => platform.Get<MetricService>().ExportCsvAsync(agentCtx, "core.tickets.metric.open", null, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => platform.Get<RecordService>().PrepareExportAsync(agentCtx, new RecordQuery { Entity = Ticket }, default))).Status);
        Assert.Contains(await platform.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().Where(a => a.Operation == "metric.export").Select(a => a.TargetId).ToListAsync(), default), id => id == "core.tickets.metric.open_by_category");
    }
}
