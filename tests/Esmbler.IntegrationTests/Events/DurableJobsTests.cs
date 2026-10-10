using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Esmbler.IntegrationTests.Events;

/// <summary>
/// G2-M1, REQ-EVT-005/006, REQ-TEN-006, REQ-ACC-11: durable jobs and timers. One tenant's backlog cannot starve another
/// tenant's jobs or events; failed work backs off and dead-letters; operators replay with an audit trail; timers are
/// idempotent and cancellable; automation depth and fan-out are bounded; long jobs resume from their checkpoint.
/// </summary>
[Collection(PlatformCollection.Name)]
[Trait("Requirement", "REQ-ACC-11")]
public class DurableJobsTests(PlatformFixture f)
{
    private sealed class OkHandler : IJobHandler
    {
        public string Kind => "test.ok";

        public string? RequiredAction => null;

        public Task RunAsync(JobContext job, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FailHandler : IJobHandler
    {
        public string Kind => "test.fail";

        public string? RequiredAction => null;

        public int Abandoned { get; private set; }

        public Task RunAsync(JobContext job, CancellationToken ct) => throw new InvalidOperationException("Downstream service unavailable.");

        public Task OnAbandonedAsync(JobContext job, string reason, CancellationToken ct)
        {
            Abandoned++;
            return Task.CompletedTask;
        }
    }

    private sealed class CheckpointHandler : IJobHandler
    {
        public string Kind => "test.checkpoint";

        public string? RequiredAction => null;

        public JsonNode? Resumed { get; private set; }

        public async Task RunAsync(JobContext job, CancellationToken ct)
        {
            if (job.Checkpoint is null)
            {
                await job.SaveCheckpointAsync(new JsonObject { ["batch"] = 3 }, ct);
                throw new InvalidOperationException("Worker lost mid-way.");
            }

            Resumed = job.Checkpoint;
        }
    }

    private readonly FailHandler fail = new();
    private readonly CheckpointHandler checkpoint = new();

    private JobRunner Runner() => new(
        f.Get<Database>(),
        f.Services.GetServices<IJobHandler>().Concat([new OkHandler(), fail, checkpoint]),
        f.Get<AccessService>(),
        f.Get<ConfigStore>(),
        NullLogger<JobRunner>.Instance);

    private Task<Guid?> ScheduleAsync(Kernel.Infrastructure.Services.ProvisionedTenant t, JobRequest request) =>
        f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => JobScheduler.ScheduleAsync(db, t.TenantId, t.Environments["prod"], request, default), default);

    private Task<List<JobRow>> JobsAsync(Kernel.Infrastructure.Services.ProvisionedTenant t, string kind) =>
        f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Jobs.Where(j => j.Kind == kind).ToListAsync(), default);

    [Fact]
    public async Task A_flooded_tenant_does_not_delay_another_tenants_jobs()
    {
        var a = await f.NewTenantAsync("fa");
        var b = await f.NewTenantAsync("fb");
        await f.Get<Database>().InTenantAsync(a.TenantId, a.Environments["prod"], null, async db =>
        {
            for (var i = 0; i < 300; i++)
            {
                await JobScheduler.ScheduleAsync(db, a.TenantId, a.Environments["prod"], new JobRequest("test.ok", []), default);
            }

            return 0;
        }, default);
        for (var i = 0; i < 5; i++)
        {
            await ScheduleAsync(b, new JobRequest("test.ok", []));
        }

        var runner = Runner();
        var rounds = 0;
        while ((await JobsAsync(b, "test.ok")).Any(j => j.Status != "succeeded") && rounds < 60)
        {
            await runner.RunOnceAsync(default);
            rounds++;
        }

        var bJobs = await JobsAsync(b, "test.ok");
        Assert.All(bJobs, j => Assert.Equal("succeeded", j.Status));
        var aDone = (await JobsAsync(a, "test.ok")).Count(j => j.Status == "succeeded");
        Assert.True(aDone <= rounds, $"tenant A ran {aDone} jobs in {rounds} rounds; B should not wait for A's backlog");
        Assert.True(aDone < 300);
    }

    [Fact]
    public async Task A_flooded_tenant_does_not_delay_another_tenants_events()
    {
        var a = await f.NewTenantAsync("ea");
        var b = await f.NewTenantAsync("eb");
        await InsertEventsAsync(a, 1500);
        var bIds = await InsertEventsAsync(b, 5);

        var dispatcher = f.Get<OutboxDispatcher>();
        for (var i = 0; i < 20; i++)
        {
            await dispatcher.DispatchOnceAsync(default);
            var bPending = await f.Get<Database>().InTenantAsync(b.TenantId, b.Environments["prod"], null, db => db.Outbox.CountAsync(o => bIds.Contains(o.Id) && o.Status != "dispatched"), default);
            if (bPending == 0)
            {
                break;
            }
        }

        var bDone = await f.Get<Database>().InTenantAsync(b.TenantId, b.Environments["prod"], null, db => db.Outbox.CountAsync(o => bIds.Contains(o.Id) && o.Status == "dispatched"), default);
        var aDone = await f.Get<Database>().InTenantAsync(a.TenantId, a.Environments["prod"], null, db => db.Outbox.CountAsync(o => o.Type == "test.noop" && o.Status == "dispatched"), default);
        Assert.Equal(5, bDone);
        Assert.True(aDone < 1000, $"B finished only after {aDone} of A's events");
    }

    [Fact]
    public async Task Failing_jobs_back_off_dead_letter_and_can_be_replayed_by_an_administrator()
    {
        var t = await f.NewTenantAsync("dl");
        var env = t.Environments["prod"];
        var id = (await ScheduleAsync(t, new JobRequest("test.fail", []) { MaxAttempts = 2 }))!.Value;
        var runner = Runner();

        await RunUntilAsync(runner, t, id, "pending", attemptsAtLeast: 1);
        var afterFirst = (await JobsAsync(t, "test.fail")).Single();
        Assert.True(afterFirst.AvailableAt > DateTimeOffset.UtcNow.AddSeconds(2), "first retry waits (backoff)");
        await f.Get<Database>().InTenantAsync(t.TenantId, env, null, db => db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, DateTimeOffset.UtcNow), default), default);
        await RunUntilAsync(runner, t, id, "dead", attemptsAtLeast: 2);
        Assert.Equal(1, fail.Abandoned);

        var operations = f.Get<OperationsService>();
        var dead = await operations.DeadLettersAsync(f.As(t, "admin"), default);
        Assert.Contains(dead, d => d.Id == id && d.LastError!.Contains("unavailable", StringComparison.Ordinal));
        var stats = await operations.StatsAsync(f.As(t, "admin"), default);
        Assert.Equal(1, stats.Single(s => s.Queue == "jobs").Dead);

        var denied = await Assert.ThrowsAsync<KernelException>(() => operations.ReplayAsync(f.As(t, "agent"), "jobs", id, default));
        Assert.Equal(403, denied.Status);
        await operations.ReplayAsync(f.As(t, "admin"), "jobs", id, default);
        var replayed = (await JobsAsync(t, "test.fail")).Single();
        Assert.Equal(("pending", 0), (replayed.Status, replayed.Attempts));
        var audited = await f.Get<Database>().InTenantAsync(t.TenantId, env, null, db => db.AuditLog.AnyAsync(a => a.Operation == "operations.replay" && a.TargetId == id.ToString()), default);
        Assert.True(audited);
    }

    [Fact]
    public async Task Timers_are_idempotent_per_key_and_cancellable()
    {
        var t = await f.NewTenantAsync("tm");
        var at = DateTimeOffset.UtcNow.AddHours(4);
        var first = await ScheduleAsync(t, new JobRequest("test.ok", new JsonObject { ["v"] = 1 }) { DedupKey = "sla:ticket-1", RunAt = at });
        var again = await ScheduleAsync(t, new JobRequest("test.ok", new JsonObject { ["v"] = 2 }) { DedupKey = "sla:ticket-1", RunAt = at.AddHours(1) });
        Assert.Equal(first, again);
        Assert.Equal(1, (await JobsAsync(t, "test.ok")).Single().Payload.Length > 0 ? JsonNode.Parse((await JobsAsync(t, "test.ok")).Single().Payload)!["v"]!.GetValue<int>() : 0);

        var moved = await ScheduleAsync(t, new JobRequest("test.ok", new JsonObject { ["v"] = 3 }) { DedupKey = "sla:ticket-1", RunAt = at.AddHours(2), ReplaceExisting = true });
        Assert.Equal(first, moved);
        var job = (await JobsAsync(t, "test.ok")).Single();
        Assert.Equal(3, JsonNode.Parse(job.Payload)!["v"]!.GetValue<int>());
        Assert.True(job.AvailableAt > at.AddHours(1.5));

        var cancelled = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => JobScheduler.CancelAsync(db, "sla:ticket-1", default), default);
        Assert.Equal(1, cancelled);
        Assert.Equal("cancelled", (await JobsAsync(t, "test.ok")).Single().Status);
        var fresh = await ScheduleAsync(t, new JobRequest("test.ok", []) { DedupKey = "sla:ticket-1" });
        Assert.NotEqual(first, fresh);
    }

    [Fact]
    public async Task Automation_depth_and_fan_out_are_bounded_and_audited()
    {
        var t = await f.NewTenantAsync("gv");
        Assert.Null(await ScheduleAsync(t, new JobRequest("test.ok", []) { Depth = JobScheduler.MaxDepth + 1 }));

        var cause = Guid.NewGuid();
        var last = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, async db =>
        {
            Guid? id = null;
            for (var i = 0; i <= JobScheduler.MaxFanOut; i++)
            {
                id = await JobScheduler.ScheduleAsync(db, t.TenantId, t.Environments["prod"], new JobRequest("test.ok", []) { CausationId = cause }, default);
            }

            return id;
        }, default);
        Assert.Null(last);
        Assert.Equal(JobScheduler.MaxFanOut, (await JobsAsync(t, "test.ok")).Count);
        var audits = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.Where(a => a.Operation.StartsWith("automation.")).Select(a => a.Operation).ToListAsync(), default);
        Assert.Contains("automation.depth_exceeded", audits);
        Assert.Contains("automation.fanout_exceeded", audits);
    }

    [Fact]
    public async Task A_retried_job_resumes_from_its_checkpoint()
    {
        var t = await f.NewTenantAsync("ck");
        var id = (await ScheduleAsync(t, new JobRequest("test.checkpoint", [])))!.Value;
        var runner = Runner();
        await RunUntilAsync(runner, t, id, "pending", attemptsAtLeast: 1);
        await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, DateTimeOffset.UtcNow), default), default);
        await RunUntilAsync(runner, t, id, "succeeded", attemptsAtLeast: 2);
        Assert.Equal(3, checkpoint.Resumed!["batch"]!.GetValue<int>());
    }

    [Fact]
    public async Task Queue_operations_are_for_administrators()
    {
        var t = await f.NewTenantAsync("oq");
        var ex = await Assert.ThrowsAsync<KernelException>(() => f.Get<OperationsService>().StatsAsync(f.As(t, "dana"), default));
        Assert.Equal(403, ex.Status);
    }

    private async Task RunUntilAsync(JobRunner runner, Kernel.Infrastructure.Services.ProvisionedTenant t, Guid id, string status, int attemptsAtLeast)
    {
        for (var i = 0; i < 60; i++)
        {
            var job = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Jobs.FirstAsync(j => j.Id == id), default);
            if (job.Status == status && job.Attempts >= attemptsAtLeast)
            {
                return;
            }

            await runner.RunOnceAsync(default);
        }

        Assert.Fail($"job {id} did not reach {status}");
    }

    private Task<List<Guid>> InsertEventsAsync(Kernel.Infrastructure.Services.ProvisionedTenant t, int count) =>
        f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, async db =>
        {
            var ids = new List<Guid>();
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                var envelope = new EventEnvelope { EventId = id, Type = "test.noop", TenantId = t.TenantId, EnvironmentId = t.Environments["prod"], OccurredAt = now, CorrelationId = Guid.NewGuid(), Actor = "system" };
                db.Outbox.Add(new OutboxRow { Id = id, TenantId = t.TenantId, EnvironmentId = t.Environments["prod"], Type = "test.noop", Event = JsonSerializer.Serialize(envelope, SpecJson.Options), AvailableAt = now.AddMilliseconds(-count + i), CreatedAt = now });
                ids.Add(id);
            }

            await db.SaveChangesAsync();
            return ids;
        }, default);
}
