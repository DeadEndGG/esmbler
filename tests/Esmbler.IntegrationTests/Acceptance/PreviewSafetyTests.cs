using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Acceptance;

/// <summary>
/// REQ-ACC-09: simulating rules, previewing draft pages and sample records, and the publish readiness check have no
/// side effects. No records, events, notifications or jobs are written; nothing leaves the preview.
/// </summary>
[Collection(PlatformCollection.Name)]
[Trait("Requirement", "REQ-ACC-09")]
public class PreviewSafetyTests(PlatformFixture f)
{
    [Fact]
    public async Task Simulation_and_preview_have_no_side_effects()
    {
        var t = await f.NewTenantAsync("ps");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var v = draft.Version;
        foreach (var op in Configuration.ConfigLifecycleTests.Customization())
        {
            v = (await drafts.ApplyAsync(admin, op, v, "ui", default)).Version;
        }

        var before = await CountsAsync(t);
        var preview = f.Get<PreviewService>();
        var simulation = await preview.SimulateAsync(admin, "core.tickets.ticket", new JsonObject { ["title"] = "Payroll", ["category"] = "access", ["status"] = "resolved", ["priority"] = "p1" }, default);
        Assert.Contains("tenant__justification", simulation.States.Required);
        await preview.ViewAsync(admin, "core.tickets.record", default);
        await preview.SampleAsync(admin, "core.tickets.ticket", default);
        await f.Get<PublishService>().RequestAsync(admin, default); // readiness check and request; still nothing operational
        var after = await CountsAsync(t);

        Assert.Equal(before.Records, after.Records);
        Assert.Equal(before.Outbox, after.Outbox);
        Assert.Equal(before.Notifications, after.Notifications);
        Assert.Equal(before.Jobs, after.Jobs);
    }

    private Task<(int Records, int Outbox, int Notifications, int Jobs)> CountsAsync(Kernel.Infrastructure.Services.ProvisionedTenant t) =>
        f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, async db =>
            (await db.Records.CountAsync(), await db.Outbox.CountAsync(), await db.Notifications.CountAsync(), await db.Jobs.CountAsync()), default);
}
