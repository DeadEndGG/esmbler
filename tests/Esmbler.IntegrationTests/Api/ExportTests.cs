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
/// counts for the person (same WHERE clause), only fields they can see, escapes formulas, is audited before it is
/// written, and needs the records.export right (refused to AI agents); a figure exports its bars as the dashboard shows them.
/// </summary>
[Collection(PlatformCollection.Name)]
public class ExportTests(PlatformFixture platform) : IClassFixture<ApiTestsFactoryHolder>
{
    private const string Ticket = "core.tickets.ticket";

    private ApiFactory Factory => ApiTestsFactoryHolder.Get(platform);

    private static string Slug(ProvisionedTenant t) => PlatformFixture.Slugs[t.TenantId];

    private static List<string> Lines(string csv) => [.. csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];

    [Fact]
    public async Task Explorer_export_holds_exactly_the_listed_records_and_needs_the_export_right()
    {
        var t = await platform.NewTenantAsync("exp", "1.10.0");
        var records = platform.Get<RecordService>();
        var agent = platform.As(t, "agent");
        var dana = t.Users["dana"].ToString();
        await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "=HYPERLINK(\"http://evil.example\")", ["requester"] = dana, ["priority"] = "p2", ["category"] = "hardware" }, null, null, default);
        await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Printer, jammed", ["requester"] = dana, ["priority"] = "p2", ["category"] = "hardware" }, null, null, default);

        var filter = "record.category == 'hardware' && record.priority == 'p2'";
        var client = await Factory.SignInAsync($"admin@{Slug(t)}.example", Slug(t));
        var response = await client.GetAsync($"/api/records/{Ticket}/export?filter={Uri.EscapeDataString(filter)}&sort=title");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment;", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        var lines = Lines(await response.Content.ReadAsStringAsync());

        // Same records as the explorer total for the same filter; choice and people show as labels; formulas are escaped.
        var admin = platform.As(t, "admin");
        var listed = await records.QueryAsync(admin, new RecordQuery { Entity = Ticket, Filters = [filter], PageSize = 1 }, default);
        Assert.Equal(listed.Total + 1, lines.Count);
        var header = lines[0].Split(',');
        Assert.Equal("Number", header[0]);
        Assert.Contains("Title", header);
        Assert.DoesNotContain("Estimated effort", header); // hidden from forms and lists for everyone
        Assert.Contains(lines, l => l.Contains("\"'=HYPERLINK(\"\"http://evil.example\"\")\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("\"Printer, jammed\"", StringComparison.Ordinal) && l.Contains("Hardware", StringComparison.Ordinal) && l.Contains("Dana", StringComparison.Ordinal));

        // Exporting is its own right (records.export): agents and requesters who can read the list cannot export it.
        foreach (var persona in new[] { "agent", "dana" })
        {
            var reader = await Factory.SignInAsync($"{persona}@{Slug(t)}.example", Slug(t));
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/records/{Ticket}/export")).StatusCode);
        }

        // Bad filters are refused like in the list; the export is audited before it is written.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/records/{Ticket}/export?filter={Uri.EscapeDataString("record.nope == 1")}")).StatusCode);
        var audit = await platform.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().Where(a => a.Operation == "records.export").ToListAsync(), default);
        var entry = Assert.Single(audit);
        Assert.Equal(t.Users["admin"], entry.Actor);
        Assert.Contains("hardware", entry.Details!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Figure_export_lists_each_bar_and_needs_the_export_right()
    {
        var t = await platform.NewTenantAsync("expm", "1.10.0");
        var client = await Factory.SignInAsync($"admin@{Slug(t)}.example", Slug(t));
        var response = await client.GetAsync("/api/metrics/core.tickets.metric.open_by_category/export?tz=UTC");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = Lines(await response.Content.ReadAsStringAsync());
        var metrics = platform.Get<MetricService>();
        var figure = await metrics.EvaluateAsync(platform.As(t, "admin"), "core.tickets.metric.open_by_category", "UTC", default);
        Assert.Equal("Group,Value,Records", lines[0]);
        Assert.Equal(figure.Buckets.Select(b => $"{b.Label},{b.Count},{b.Count}"), lines.Skip(1));

        // An ungrouped figure is one row; people without records.export cannot export figures they can see.
        var open = Lines(await (await client.GetAsync("/api/metrics/core.tickets.metric.open/export")).Content.ReadAsStringAsync());
        var count = (await metrics.EvaluateAsync(platform.As(t, "admin"), "core.tickets.metric.open", null, default)).Count;
        Assert.Equal(["Figure,Value,Records", $"Open,{count},{count}"], open);
        var agent = await Factory.SignInAsync($"agent@{Slug(t)}.example", Slug(t));
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/metrics/core.tickets.metric.open/export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/metrics/core.tickets.metric.nope/export")).StatusCode);

        var agentCtx = platform.As(t, "admin", agent: Guid.NewGuid());
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => platform.Get<MetricService>().ExportCsvAsync(agentCtx, "core.tickets.metric.open", null, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => platform.Get<RecordService>().PrepareExportAsync(agentCtx, new RecordQuery { Entity = Ticket }, default))).Status);
        Assert.Contains(await platform.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().Where(a => a.Operation == "metric.export").Select(a => a.TargetId).ToListAsync(), default), id => id == "core.tickets.metric.open_by_category");
    }

    [Fact]
    public async Task Exports_never_include_another_workspace_or_fields_hidden_from_the_exporter()
    {
        var t = await platform.NewTenantAsync("expi", "1.10.0");
        var other = await platform.NewTenantAsync("expj", "1.10.0");
        var records = platform.Get<RecordService>();
        await records.CreateAsync(platform.As(other, "agent"), Ticket, new JsonObject { ["title"] = $"Other workspace {Guid.NewGuid():N}", ["requester"] = other.Users["dana"].ToString() }, null, null, default);

        // Builders hold records.export through the Tickets administrators' permission set; hide a field from that set.
        var admin = platform.As(t, "admin");
        var drafts = platform.Get<Kernel.Infrastructure.Configuration.DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var applied = await drafts.ApplyAsync(admin, new Sdk.Overlay.SetFieldAccess { PermissionSet = "core.tickets.ps_admin", Entity = Ticket, Field = "impact", Access = "hidden" }, draft.Version, "ui", default);
        await platform.Get<Kernel.Infrastructure.Configuration.PublishService>().PublishAsync(admin, applied.Draft.ArtifactHash!, applied.Draft.PublishedRevision, default);

        var builder = await Factory.SignInAsync($"builder@{Slug(t)}.example", Slug(t));
        var lines = Lines(await (await builder.GetAsync($"/api/records/{Ticket}/export")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("Impact", lines[0].Split(','));
        Assert.Equal((await records.QueryAsync(platform.As(t, "builder"), new RecordQuery { Entity = Ticket, PageSize = 1 }, default)).Total + 1, lines.Count);
        Assert.DoesNotContain(lines, l => l.Contains("Other workspace", StringComparison.Ordinal));
        Assert.Contains("Impact", Lines(await (await (await Factory.SignInAsync($"admin@{Slug(t)}.example", Slug(t))).GetAsync($"/api/records/{Ticket}/export")).Content.ReadAsStringAsync())[0].Split(','));
    }
}
