using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Sdk.Events;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Events;

/// <summary>REQ-EVT-001/002/004, REQ-ACC-06: outbox delivery, duplicate suppression, and stale-revision rejection.</summary>
[Collection(PlatformCollection.Name)]
public class EventDeliveryTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";

    private async Task DrainAsync()
    {
        var dispatcher = f.Get<OutboxDispatcher>();
        while (await dispatcher.DispatchOnceAsync(default) > 0)
        {
        }
    }

    [Fact]
    public async Task Resolving_notifies_the_requester_once_even_when_redelivered()
    {
        var records = f.Get<RecordService>();
        var sam = f.As(f.Acme, "sam");
        var t = await records.CreateAsync(sam, Ticket, new JsonObject { ["title"] = "Docking station dead", ["requester"] = f.Acme.Users["riley"].ToString() }, null, null, default);
        var started = await records.TransitionAsync(sam, Ticket, t.Id, t.Revision, "start", [], null, null, default);
        await records.TransitionAsync(sam, Ticket, t.Id, started.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Replaced the dock" }, null, null, default);
        await DrainAsync();

        var db = f.Get<Database>();
        int Count(KernelDbContext ctx) => ctx.Notifications.Count(n => n.TemplateId == "core.tickets.notify.resolved" && n.Recipient == f.Acme.Users["riley"] && n.Subject.Contains("Docking station dead"));
        Assert.Equal(1, await db.InTenantAsync(f.Acme.TenantId, f.Acme.Environments["prod"], null, ctx => Task.FromResult(Count(ctx)), default));

        // Redeliver every event of this record: the inbox suppresses duplicate effects.
        await db.InTenantAsync(f.Acme.TenantId, f.Acme.Environments["prod"], null, ctx => ctx.Outbox.Where(o => o.RecordId == t.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "pending").SetProperty(o => o.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(-1))), default);
        await DrainAsync();
        Assert.Equal(1, await db.InTenantAsync(f.Acme.TenantId, f.Acme.Environments["prod"], null, ctx => Task.FromResult(Count(ctx)), default));

        var body = await db.InTenantAsync(f.Acme.TenantId, f.Acme.Environments["prod"], null, ctx => ctx.Notifications.Where(n => n.Recipient == f.Acme.Users["riley"] && n.Subject.Contains("Docking")).Select(n => n.Body).FirstAsync(), default);
        Assert.Contains("Replaced the dock", body);
    }

    [Fact]
    public async Task Projection_ignores_stale_and_duplicate_revisions()
    {
        var projection = new RecordStateProjection();
        var db = f.Get<Database>();
        var record = Guid.NewGuid();
        EventEnvelope Event(int revision, string to) => new()
        {
            EventId = Guid.NewGuid(),
            Type = EventTypes.RecordTransitioned,
            TenantId = f.Acme.TenantId,
            EnvironmentId = f.Acme.Environments["prod"],
            Entity = Ticket,
            RecordId = record,
            RecordRevision = revision,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
            Actor = "system",
            Payload = new JsonObject { ["to"] = to },
        };

        async Task Apply(EventEnvelope e) => await db.InTenantAsync(f.Acme.TenantId, f.Acme.Environments["prod"], null, ctx => projection.HandleAsync(e, ctx, default), default);
        await Apply(Event(3, "resolved"));
        await Apply(Event(2, "in_progress")); // arrives late
        await Apply(Event(3, "resolved"));    // duplicate
        var state = await db.InTenantAsync(f.Acme.TenantId, f.Acme.Environments["prod"], null, ctx => ctx.Projections.FirstAsync(p => p.RecordId == record), default);
        Assert.Equal(3, state.RecordRevision);
        Assert.Contains("resolved", state.State);

        await Apply(Event(5, "closed")); // revision 4 missing
        state = await db.InTenantAsync(f.Acme.TenantId, f.Acme.Environments["prod"], null, ctx => ctx.Projections.FirstAsync(p => p.RecordId == record), default);
        Assert.True(JsonNode.Parse(state.State)!["gap"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Record_change_audit_and_event_commit_together()
    {
        var records = f.Get<RecordService>();
        var sam = f.As(f.Acme, "sam");
        var t = await records.CreateAsync(sam, Ticket, new JsonObject { ["title"] = "Atomicity check", ["requester"] = f.Acme.Users["dana"].ToString() }, null, null, default);
        var db = f.Get<Database>();
        var (audits, events) = await db.InTenantAsync(f.Acme.TenantId, f.Acme.Environments["prod"], null, async ctx =>
            (await ctx.AuditLog.CountAsync(a => a.TargetId == t.Id.ToString()), await ctx.Outbox.CountAsync(o => o.RecordId == t.Id)), default);
        Assert.Equal(1, audits);
        Assert.Equal(1, events);

        // REQ-EVT-003: the envelope carries IDs, tenant and environment, revisions, schema version, actor and correlation.
        var json = await db.InTenantAsync(f.Acme.TenantId, f.Acme.Environments["prod"], null, ctx => ctx.Outbox.Where(o => o.RecordId == t.Id).Select(o => o.Event).FirstAsync(), default);
        var envelope = System.Text.Json.JsonSerializer.Deserialize<Esmbler.Sdk.Events.EventEnvelope>(json, Esmbler.Sdk.Specs.SpecJson.Options)!;
        Assert.Equal(Esmbler.Sdk.Events.EventTypes.RecordCreated, envelope.Type);
        Assert.NotEqual(Guid.Empty, envelope.EventId);
        Assert.Equal(f.Acme.TenantId, envelope.TenantId);
        Assert.Equal(f.Acme.Environments["prod"], envelope.EnvironmentId);
        Assert.Equal(Ticket, envelope.Entity);
        Assert.Equal(t.Id, envelope.RecordId);
        Assert.Equal(t.Revision, envelope.RecordRevision);
        Assert.Equal(t.ConfigRevision, envelope.ConfigRevision);
        Assert.Equal(2, envelope.SchemaVersion);
        Assert.Equal("core.tickets", envelope.Source);
        Assert.NotNull(envelope.RecordedAt);
        Assert.NotEqual(Guid.Empty, envelope.CorrelationId);
        Assert.False(string.IsNullOrEmpty(envelope.Actor));

        // A failed write leaves no audit entry and no event behind.
        await Assert.ThrowsAnyAsync<Exception>(() => records.UpdateAsync(sam, Ticket, t.Id, t.Revision, new JsonObject { ["title"] = "x" }, null, null, default));
        var (audits2, events2) = await db.InTenantAsync(f.Acme.TenantId, f.Acme.Environments["prod"], null, async ctx =>
            (await ctx.AuditLog.CountAsync(a => a.TargetId == t.Id.ToString()), await ctx.Outbox.CountAsync(o => o.RecordId == t.Id)), default);
        Assert.Equal((1, 1), (audits2, events2));
    }
}
