using System.Diagnostics;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Esmbler.IntegrationTests.Records;

/// <summary>
/// REQ-REC-002, REQ-REC-003 (G1-M5): list queries stay fast with the permission predicate inside SQL at a meaningful
/// share of pilot scale (100,000 tickets in one tenant environment; ASM-003 declares up to 500,000, load-tested in Gate 2).
/// Budget: median under 300 ms per query shape on any runner; p95 is reported (the ASM-003 revisit trigger is
/// p95 > 300 ms on production-like hardware, checked in the Gate 2 load test).
/// </summary>
[Collection(PlatformCollection.Name)]
[Trait("Category", "Benchmark")]
public class QueryBenchmarkTests(PlatformFixture f, ITestOutputHelper output)
{
    private const string Ticket = "core.tickets.ticket";
    private const int Rows = 100_000;

    [Fact]
    public async Task List_queries_meet_the_latency_budget_at_100k_tickets()
    {
        var t = await f.NewTenantAsync("qb");
        var env = t.Environments["prod"];
        var admin = t.Users["admin"];
        var dana = t.Users["dana"].ToString();
        var riley = t.Users["riley"].ToString();
        var sw = Stopwatch.StartNew();
        await f.Get<Database>().InTenantAsync(t.TenantId, env, null, async db =>
        {
            var revision = (await f.Get<ConfigStore>().GetActiveAsync(db, t.TenantId, env, default)).Revision;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO kernel.records (tenant_id, environment_id, id, entity_id, number, data, config_revision, search, created_by, updated_by, created_at, updated_at)
                SELECT {t.TenantId}, {env}, gen_random_uuid(), {Ticket}, 1000000 + g,
                       jsonb_build_object(
                           'title', 'Bench ticket ' || g,
                           'status', (ARRAY['new','assigned','in_progress','on_hold','resolved','closed'])[1 + g % 6],
                           'priority', (ARRAY['p1','p2','p3','p4'])[1 + g % 4],
                           'category', (ARRAY['hardware','software','network','access','other'])[1 + g % 5],
                           'requester', CASE WHEN g % 50 = 0 THEN {dana} ELSE {riley} END),
                       {revision},
                       to_tsvector('simple', 'Bench ticket ' || g || CASE WHEN g % 1000 = 0 THEN ' printer jam' ELSE '' END),
                       {admin}, {admin}, now() - make_interval(secs => g), now() - make_interval(secs => g)
                FROM generate_series(1, {Rows}) g
                """, default);
            await db.Database.ExecuteSqlRawAsync("ANALYZE kernel.records", CancellationToken.None);
            return 0;
        }, default);
        output.WriteLine($"Seeded {Rows} tickets in {sw.ElapsedMilliseconds} ms");

        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        var requester = f.As(t, "dana");
        var shapes = new (string Name, Func<Task<int>> Run)[]
        {
            ("default list (sorted by updated, page 50)", async () => (await records.QueryAsync(agent, new RecordQuery { Entity = Ticket, PageSize = 50 }, default)).Items.Count),
            ("saved view: open by priority", async () => (await records.QueryAsync(agent, new RecordQuery { Entity = Ticket, Filters = ["!(record.status in ['resolved', 'closed', 'cancelled'])"], Sort = new SortSpec("priority", false), PageSize = 50 }, default)).Items.Count),
            ("filter bar: status and category", async () => (await records.QueryAsync(agent, new RecordQuery { Entity = Ticket, Filters = ["record.status == 'on_hold'", "record.category == 'access'"], PageSize = 50 }, default)).Items.Count),
            ("full-text search", async () => (await records.QueryAsync(agent, new RecordQuery { Entity = Ticket, Search = "printer", PageSize = 50 }, default)).Items.Count),
            ("requester: only related tickets", async () => (await records.QueryAsync(requester, new RecordQuery { Entity = Ticket, PageSize = 50 }, default)).Items.Count),
            ("deep page (offset 5,000)", async () => (await records.QueryAsync(agent, new RecordQuery { Entity = Ticket, Offset = 5000, PageSize = 50 }, default)).Items.Count),
        };

        var failures = new List<string>();
        foreach (var (name, run) in shapes)
        {
            await run();
            await run();
            var timings = new List<double>();
            var count = 0;
            for (var i = 0; i < 20; i++)
            {
                var watch = Stopwatch.StartNew();
                count = await run();
                timings.Add(watch.Elapsed.TotalMilliseconds);
            }

            timings.Sort();
            var p50 = timings[timings.Count / 2];
            var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
            output.WriteLine($"{name}: p50 {p50:F0} ms, p95 {p95:F0} ms, {count} rows");
            Assert.True(count > 0, $"{name} returned nothing");
            if (p50 > 300)
            {
                failures.Add($"{name}: p50 {p50:F0} ms");
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}
