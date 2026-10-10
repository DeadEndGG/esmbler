using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Esmbler.IntegrationTests.Events;

/// <summary>
/// G2-M17, REQ-EVT-008, REQ-EVT-009 (ADR 0017): domain events declared by modules and published by transitions and
/// edge-triggered rules, v2 envelopes with source and severity, per-consumer failure isolation with dead letters and
/// replay by consumer, and outbox retention.
/// </summary>
[Collection(PlatformCollection.Name)]
public class EventPlatformTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";

    private RecordService Records => f.Get<RecordService>();

    private async Task DrainAsync()
    {
        var dispatcher = f.Get<OutboxDispatcher>();
        while (await dispatcher.DispatchOnceAsync(default) > 0)
        {
        }
    }

    private async Task<List<EventEnvelope>> EventsAsync(ProvisionedTenant t, Guid recordId, string type) =>
        (await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Outbox.Where(o => o.RecordId == recordId && o.Type == type).OrderBy(o => o.CreatedAt).Select(o => o.Event).ToListAsync(), default))
        .Select(e => JsonSerializer.Deserialize<EventEnvelope>(e, SpecJson.Options)!).ToList();

    [Fact]
    public async Task Escalating_a_critical_ticket_publishes_a_critical_escalated_event_caused_by_the_record_change()
    {
        var t = await f.NewTenantAsync("esc", "1.4.0");
        var agent = f.As(t, "agent");
        var ticket = await Records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Core switch down", ["requester"] = t.Users["dana"].ToString(), ["priority"] = "p1", ["impact"] = "high" }, null, null, default);
        var started = await Records.TransitionAsync(agent, Ticket, ticket.Id, ticket.Revision, "start", [], null, null, default);

        var missing = await Assert.ThrowsAsync<Esmbler.Kernel.ValidationFailedException>(() => Records.TransitionAsync(agent, Ticket, ticket.Id, started.Revision, "escalate", [], null, null, default));
        Assert.Contains(missing.Errors, e => e.Field == "escalation_reason");
        Assert.Empty(await EventsAsync(t, ticket.Id, "core.tickets.ticket.escalated"));

        var escalated = await Records.TransitionAsync(agent, Ticket, ticket.Id, started.Revision, "escalate", new JsonObject { ["escalation_reason"] = "Needs the network team" }, null, null, default);
        Assert.True(escalated.Data["escalated"]!.GetValue<bool>());

        var e = Assert.Single(await EventsAsync(t, ticket.Id, "core.tickets.ticket.escalated"));
        Assert.Equal(EventSeverities.Critical, e.Severity);
        Assert.Equal("core.tickets", e.Source);
        Assert.Equal(1, e.TypeVersion);
        Assert.Equal(2, e.SchemaVersion);
        Assert.Equal(escalated.Revision, e.RecordRevision);
        Assert.Equal("p1", e.Payload["priority"]!.GetValue<string>());
        Assert.Equal("in_progress", e.Payload["from"]!.GetValue<string>());
        Assert.Equal("Needs the network team", e.Payload["reason"]!.GetValue<string>());

        // The domain event is caused by the record event of the same write.
        var transitioned = (await EventsAsync(t, ticket.Id, EventTypes.RecordTransitioned)).Single(r => r.RecordRevision == escalated.Revision);
        Assert.Equal(transitioned.EventId, e.CausationId);
        Assert.Equal(transitioned.CorrelationId, e.CorrelationId);

        // Resolving publishes the module's resolved event with the escalation flag.
        await Records.TransitionAsync(agent, Ticket, ticket.Id, escalated.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Replaced the line card" }, null, null, default);
        var resolved = Assert.Single(await EventsAsync(t, ticket.Id, "core.tickets.ticket.resolved"));
        Assert.True(resolved.Payload["escalated"]!.GetValue<bool>());
        Assert.Equal(EventSeverities.Info, resolved.Severity);
    }

    [Fact]
    public async Task A_rule_publishes_its_event_only_when_its_condition_becomes_true()
    {
        var t = await f.NewTenantAsync("edge", "1.4.0");
        var agent = f.As(t, "agent");
        var ticket = await Records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Printer jam", ["requester"] = t.Users["dana"].ToString() }, null, null, default);
        Assert.Empty(await EventsAsync(t, ticket.Id, "core.tickets.ticket.priority_raised"));

        var raised = await Records.UpdateAsync(agent, Ticket, ticket.Id, ticket.Revision, new JsonObject { ["priority"] = "p1", ["impact"] = "high" }, null, null, default);
        var e = Assert.Single(await EventsAsync(t, ticket.Id, "core.tickets.ticket.priority_raised"));
        Assert.Equal(("p1", "p3"), (e.Payload["priority"]!.GetValue<string>(), e.Payload["previous"]!.GetValue<string>()));
        Assert.Equal(EventSeverities.Critical, e.Severity);

        // Still critical: no new event. Down and up again: one more.
        var edited = await Records.UpdateAsync(agent, Ticket, ticket.Id, raised.Revision, new JsonObject { ["title"] = "Printer jam on floor 3" }, null, null, default);
        Assert.Single(await EventsAsync(t, ticket.Id, "core.tickets.ticket.priority_raised"));
        var lowered = await Records.UpdateAsync(agent, Ticket, ticket.Id, edited.Revision, new JsonObject { ["priority"] = "p2" }, null, null, default);
        await Records.UpdateAsync(agent, Ticket, ticket.Id, lowered.Revision, new JsonObject { ["priority"] = "p1" }, null, null, default);
        Assert.Equal(2, (await EventsAsync(t, ticket.Id, "core.tickets.ticket.priority_raised")).Count);

        // A ticket created as critical publishes the event on create.
        var critical = await Records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Site outage", ["requester"] = t.Users["dana"].ToString(), ["priority"] = "p1", ["impact"] = "high" }, null, null, default);
        var onCreate = Assert.Single(await EventsAsync(t, critical.Id, "core.tickets.ticket.priority_raised"));
        Assert.Null(onCreate.Payload["previous"]);
    }

    [Fact]
    public async Task Configuration_publishes_are_v2_envelopes_from_the_platform()
    {
        var t = await f.NewTenantAsync("cfgev", "1.4.0");
        var json = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Outbox.Where(o => o.Type == EventTypes.ConfigPublished).OrderByDescending(o => o.CreatedAt).Select(o => o.Event).FirstAsync(), default);
        var e = JsonSerializer.Deserialize<EventEnvelope>(json, SpecJson.Options)!;
        Assert.Equal("core.platform", e.Source);
        Assert.Equal(2, e.SchemaVersion);
        Assert.NotNull(e.Payload["revision"]);
        Assert.False(string.IsNullOrEmpty(e.Payload["content_hash"]?.GetValue<string>()));
    }

    [Fact]
    public async Task A_configuration_notice_written_before_envelope_v2_still_dispatches()
    {
        var t = await f.NewTenantAsync("legacy", "1.4.0");
        var env = t.Environments["prod"];
        var db = f.Get<Database>();
        var id = Guid.NewGuid();
        await db.InTenantAsync(t.TenantId, env, null, async ctx =>
        {
            ctx.Outbox.Add(new OutboxRow { Id = id, TenantId = t.TenantId, EnvironmentId = env, Type = EventTypes.ConfigPublished, Event = """{"type":"config.published","revision":3,"source":"ui","contentHash":"abc"}""", AvailableAt = DateTimeOffset.UtcNow.AddSeconds(-1), CreatedAt = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
            return 0;
        }, default);
        await DrainAsync();
        var row = await db.InTenantAsync(t.TenantId, env, null, ctx => ctx.Outbox.AsNoTracking().FirstAsync(o => o.Id == id), default);
        Assert.Equal("dispatched", row.Status);
        var read = EventWriter.Read(row);
        Assert.Equal((1, 3), (read.SchemaVersion, read.Payload["revision"]!.GetValue<int>()));
    }

    private sealed class FlakyConsumer : IEventConsumer
    {
        public bool Fail { get; set; } = true;

        public int Handled { get; private set; }

        public string Name => "test.flaky";

        public bool Handles(EventEnvelope envelope) => envelope.Type == "test.isolation";

        public Task HandleAsync(EventEnvelope envelope, KernelDbContext db, CancellationToken ct)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Webhook endpoint unavailable.");
            }

            Handled++;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingConsumer : IEventConsumer
    {
        public int Handled { get; private set; }

        public string Name => "test.counting";

        public bool Handles(EventEnvelope envelope) => envelope.Type == "test.isolation";

        public Task HandleAsync(EventEnvelope envelope, KernelDbContext db, CancellationToken ct)
        {
            Handled++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_failing_consumer_retries_and_dead_letters_alone_and_is_replayed_by_consumer()
    {
        var t = await f.NewTenantAsync("iso", "1.4.0");
        var env = t.Environments["prod"];
        var db = f.Get<Database>();
        await DrainAsync(); // leave nothing else for the test dispatcher below

        var eventId = Guid.NewGuid();
        var envelope = new EventEnvelope { EventId = eventId, Type = "test.isolation", TenantId = t.TenantId, EnvironmentId = env, OccurredAt = DateTimeOffset.UtcNow, CorrelationId = Guid.NewGuid(), Actor = "system", Payload = [] };
        await db.InTenantAsync(t.TenantId, env, null, async ctx =>
        {
            EventWriter.Add(ctx, envelope);
            await ctx.SaveChangesAsync();
            return 0;
        }, default);

        var flaky = new FlakyConsumer();
        var counting = new CountingConsumer();
        var dispatcher = new OutboxDispatcher(db, [flaky, counting], NullLogger<OutboxDispatcher>.Instance);
        async Task<OutboxRow> OutboxAsync() => await db.InTenantAsync(t.TenantId, env, null, ctx => ctx.Outbox.AsNoTracking().FirstAsync(o => o.Id == eventId), default);
        async Task<ConsumerFailureRow?> FailureAsync() => await db.InTenantAsync(t.TenantId, env, null, ctx => ctx.ConsumerFailures.AsNoTracking().FirstOrDefaultAsync(c => c.EventId == eventId), default);
        async Task RunNowAsync()
        {
            await db.InTenantAsync(t.TenantId, env, null, ctx => ctx.Outbox.Where(o => o.Id == eventId).ExecuteUpdateAsync(s => s.SetProperty(o => o.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(-1))), default);
            await dispatcher.DispatchOnceAsync(default);
        }

        // First try: the healthy consumer commits; the failing one is recorded and the event backs off.
        await dispatcher.DispatchOnceAsync(default);
        Assert.Equal(1, counting.Handled);
        var first = await FailureAsync();
        Assert.Equal((1, false), (first!.Attempts, first.Dead));
        var pending = await OutboxAsync();
        Assert.Equal("pending", pending.Status);
        Assert.Contains("test.flaky", pending.LastError, StringComparison.Ordinal);
        Assert.True(pending.AvailableAt > DateTimeOffset.UtcNow, "the event backs off");

        // Retries run only the failing consumer.
        await RunNowAsync();
        Assert.Equal(1, counting.Handled);
        Assert.Equal(2, (await FailureAsync())!.Attempts);

        // At the last attempt the consumer dead-letters alone and the event counts as dispatched.
        await db.InTenantAsync(t.TenantId, env, null, ctx => ctx.ConsumerFailures.Where(c => c.EventId == eventId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, OutboxDispatcher.MaxAttempts - 1)), default);
        await RunNowAsync();
        var dead = await FailureAsync();
        Assert.True(dead!.Dead);
        Assert.Equal("dispatched", (await OutboxAsync()).Status);
        Assert.Equal(1, counting.Handled);

        var operations = f.Get<OperationsService>();
        var letters = await operations.DeadLettersAsync(f.As(t, "admin"), default);
        var letter = Assert.Single(letters, d => d.Queue == "consumers");
        Assert.Equal(dead.Id, letter.Id);
        Assert.Contains("unavailable", letter.LastError, StringComparison.Ordinal);
        Assert.Equal(1, (await operations.StatsAsync(f.As(t, "admin"), default)).Single(s => s.Queue == "events").Failed);

        // Other workspaces cannot replay it; an administrator replays just that consumer.
        var foreign = await Assert.ThrowsAsync<Esmbler.Kernel.KernelException>(() => operations.ReplayAsync(f.As(f.Globex, "morgan"), "consumers", dead.Id, default));
        Assert.Equal(404, foreign.Status);
        flaky.Fail = false;
        await operations.ReplayAsync(f.As(t, "admin"), "consumers", dead.Id, default);
        await dispatcher.DispatchOnceAsync(default);
        Assert.Equal((1, 1), (flaky.Handled, counting.Handled));
        Assert.Null(await FailureAsync());
        Assert.Equal("dispatched", (await OutboxAsync()).Status);
        var audited = await db.InTenantAsync(t.TenantId, env, null, ctx => ctx.AuditLog.AnyAsync(a => a.Operation == "operations.replay" && a.TargetId == dead.Id.ToString()), default);
        Assert.True(audited);
    }

    [Fact]
    public async Task Retention_removes_only_old_dispatched_events_and_their_marks()
    {
        var t = await f.NewTenantAsync("ret", "1.4.0");
        var env = t.Environments["prod"];
        var db = f.Get<Database>();
        var old = DateTimeOffset.UtcNow.AddDays(-30);
        Guid Seed(KernelDbContext ctx, string status, DateTimeOffset? dispatchedAt)
        {
            var id = EventWriter.Add(ctx, new EventEnvelope { EventId = Guid.NewGuid(), Type = "test.retention", TenantId = t.TenantId, EnvironmentId = env, OccurredAt = old, CorrelationId = Guid.NewGuid(), Actor = "system", Payload = [] });
            var row = ctx.Outbox.Local.Single(o => o.Id == id);
            row.Status = status;
            row.DispatchedAt = dispatchedAt;
            row.CreatedAt = old;
            return id;
        }

        var (oldDone, recentDone, oldDead, deadConsumer) = await db.InTenantAsync(t.TenantId, env, null, async ctx =>
        {
            var ids = (Seed(ctx, "dispatched", old), Seed(ctx, "dispatched", DateTimeOffset.UtcNow), Seed(ctx, "dead", null), Seed(ctx, "dispatched", old));
            ctx.ConsumerFailures.Add(new ConsumerFailureRow { TenantId = t.TenantId, EnvironmentId = env, Id = Guid.NewGuid(), Consumer = "test", EventId = ids.Item4, EventType = "test.retention", Attempts = 10, Dead = true, FirstFailedAt = old, LastFailedAt = old });
            ctx.Inbox.Add(new InboxRow { Consumer = "test", EventId = ids.Item1, TenantId = t.TenantId, EnvironmentId = env, ProcessedAt = old });
            ctx.Inbox.Add(new InboxRow { Consumer = "test", EventId = ids.Item3, TenantId = t.TenantId, EnvironmentId = env, ProcessedAt = old });
            await ctx.SaveChangesAsync();
            return ids;
        }, default);

        Assert.True(await f.Get<OutboxDispatcher>().PurgeAsync(14, default) >= 1);
        var (left, marks) = await db.InTenantAsync(t.TenantId, env, null, async ctx =>
            (await ctx.Outbox.Where(o => o.Type == "test.retention").Select(o => o.Id).ToListAsync(), await ctx.Inbox.Where(i => i.Consumer == "test").Select(i => i.EventId).ToListAsync()), default);
        Assert.DoesNotContain(oldDone, left);
        Assert.Contains(recentDone, left);
        Assert.Contains(oldDead, left);
        Assert.Contains(deadConsumer, left); // its dead letter waits for an operator
        Assert.Equal([oldDead], marks);
    }
}
