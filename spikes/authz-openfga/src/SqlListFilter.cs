using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace AuthzSpike;

/// <summary>
/// Compiles the engine's view of one principal into a SQL predicate over the tickets table.
/// Engine calls: ListObjects(user, member, group) + Check(read_all/edit_all on entity_type:ticket)
/// + one Check per enumerable attribute value for the CEL-guarded relation (priority_editor).
/// </summary>
public sealed record CompiledTicketFilter(string User, IReadOnlyList<string> Groups, bool ReadAll, bool EditAll, IReadOnlyList<int> EditablePriorities)
{
    public string ViewSql => ReadAll || EditAll
        ? "TRUE"
        : "(requester_id = @u OR assignee_id = @u OR watcher_ids @> ARRAY[@u] OR owner_group_id = ANY(@groups))";

    public string EditSql
    {
        get
        {
            if (EditablePriorities.Count == 0) return "FALSE";
            var baseSql = EditAll ? "TRUE" : "(assignee_id = @u OR owner_group_id = ANY(@groups))";
            var prio = EditablePriorities.Count == 4 ? "TRUE" : "priority = ANY(@prios)";
            return $"({baseSql} AND {prio})";
        }
    }

    public void Bind(NpgsqlCommand cmd)
    {
        cmd.Parameters.AddWithValue("u", User);
        cmd.Parameters.Add(new NpgsqlParameter("groups", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = Groups.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("prios", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = EditablePriorities.ToArray() });
    }

    public static async Task<CompiledTicketFilter> CompileAsync(IAuthorizationEngine engine, AuthzScope scope, string userId, CancellationToken ct = default)
    {
        var user = $"user:{userId}";
        // Throws if the engine is unavailable: callers fail closed (no list) rather than fall back.
        var groups = await engine.ListObjectsAsync(scope, user, "member", "group", ct: ct);
        var checks = new List<CheckQuery>
        {
            new(user, "read_all", "entity_type:ticket"),
            new(user, "edit_all", "entity_type:ticket"),
        };
        checks.AddRange(Enumerable.Range(1, 4).Select(p => new CheckQuery(user, "priority_editor", "entity_type:ticket",
            Attributes: new Dictionary<string, object> { ["priority"] = p })));
        var d = await engine.BatchCheckAsync(scope, checks, ct);
        if (d.Any(x => x.Reason is DecisionReason.EngineUnavailable or DecisionReason.EngineError))
            throw new InvalidOperationException("authorization engine unavailable: fail closed");
        var prios = Enumerable.Range(1, 4).Where(p => d[1 + p].Allowed).ToList();
        return new CompiledTicketFilter(userId, groups.Select(g => g["group:".Length..]).ToList(), d[0].Allowed, d[1].Allowed, prios);
    }
}

public static class TicketDb
{
    public const string TenantA = "acme-prod";
    public const string TenantB = "globex-prod";

    public static async Task<double> SeedAsync(NpgsqlDataSource ds, int ticketsA, int ticketsB)
    {
        var sw = Stopwatch.StartNew();
        await using var conn = await ds.OpenConnectionAsync();
        var sql = """
            DROP TABLE IF EXISTS tickets;
            CREATE TABLE tickets(
              tenant_id      text        NOT NULL,
              id             bigint      NOT NULL,
              priority       int         NOT NULL,
              requester_id   text        NOT NULL,
              assignee_id    text,
              owner_group_id text,
              watcher_ids    text[]      NOT NULL DEFAULT '{}',
              internal_cost  numeric(12,2),
              updated_at     timestamptz NOT NULL,
              title          text        NOT NULL,
              PRIMARY KEY (tenant_id, id));
            SELECT setseed(0.42);
            INSERT INTO tickets
            SELECT t.tenant, g,
              CASE WHEN random() < 0.02 THEN 1 ELSE 2 + floor(random()*3)::int END,
              CASE WHEN g % 997 = 0 THEN 'alice' ELSE 'u' || floor(random()*2000)::int END,
              CASE WHEN g % 1009 = 0 THEN 'bob' WHEN g % 10 = 0 THEN NULL ELSE 'u' || floor(random()*2000)::int END,
              CASE WHEN g % 50 = 0 THEN 'l1-support' WHEN g % 50 = 1 THEN 'network-team' ELSE 'g' || floor(random()*48)::int END,
              CASE WHEN g % 1013 = 0 THEN ARRAY['dave', 'u' || floor(random()*2000)::int]
                   WHEN g % 3 = 0 THEN ARRAY['u' || floor(random()*2000)::int] ELSE '{}' END,
              round((random()*5000)::numeric, 2),
              now() - (g || ' seconds')::interval,
              'Ticket ' || g
            FROM (VALUES ('acme-prod', @na), ('globex-prod', @nb)) AS t(tenant, n), generate_series(1, t.n) g;
            CREATE INDEX ON tickets (tenant_id, requester_id);
            CREATE INDEX ON tickets (tenant_id, assignee_id);
            CREATE INDEX ON tickets (tenant_id, owner_group_id);
            CREATE INDEX ON tickets USING gin (watcher_ids);
            CREATE INDEX ON tickets (tenant_id, updated_at DESC);
            ANALYZE tickets;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("na", ticketsA);
        cmd.Parameters.AddWithValue("nb", ticketsB);
        cmd.CommandTimeout = 600;
        await cmd.ExecuteNonQueryAsync();
        return sw.Elapsed.TotalSeconds;
    }

    public static async Task<List<TicketRow>> SampleAsync(NpgsqlDataSource ds, string tenant, int random, string[] relatedUsers, string[] relatedGroups)
    {
        await using var conn = await ds.OpenConnectionAsync();
        var sql = """
            (SELECT * FROM tickets WHERE tenant_id = @t ORDER BY md5(id::text) LIMIT @n)
            UNION
            (SELECT * FROM tickets WHERE tenant_id = @t AND (requester_id = ANY(@users) OR assignee_id = ANY(@users)
                 OR watcher_ids && @users OR owner_group_id = ANY(@groups)) ORDER BY md5(id::text) LIMIT @n)
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("n", random);
        cmd.Parameters.AddWithValue("users", relatedUsers);
        cmd.Parameters.AddWithValue("groups", relatedGroups);
        var rows = new List<TicketRow>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rows.Add(new TicketRow(
                r.GetInt64(r.GetOrdinal("id")),
                r.GetInt32(r.GetOrdinal("priority")),
                r.GetString(r.GetOrdinal("requester_id")),
                r.IsDBNull(r.GetOrdinal("assignee_id")) ? null : r.GetString(r.GetOrdinal("assignee_id")),
                r.IsDBNull(r.GetOrdinal("owner_group_id")) ? null : r.GetString(r.GetOrdinal("owner_group_id")),
                r.GetFieldValue<string[]>(r.GetOrdinal("watcher_ids"))));
        }
        return rows;
    }

    public static async Task<HashSet<long>> FilterIdsAsync(NpgsqlDataSource ds, string tenant, CompiledTicketFilter f, string predicate, IEnumerable<long> ids)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand($"SELECT id FROM tickets WHERE tenant_id = @t AND id = ANY(@ids) AND {predicate}", conn);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        f.Bind(cmd);
        var set = new HashSet<long>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetInt64(0));
        return set;
    }

    /// <summary>Returns (median ms for first page of 50, median ms for count(*), count).</summary>
    public static async Task<(double pageMs, double countMs, long count)> TimeListAsync(NpgsqlDataSource ds, string tenant, CompiledTicketFilter f, int runs = 7)
    {
        await using var conn = await ds.OpenConnectionAsync();
        var page = new List<double>();
        var cnt = new List<double>();
        long count = 0;
        for (var i = 0; i < runs; i++)
        {
            await using (var cmd = new NpgsqlCommand($"SELECT id, title FROM tickets WHERE tenant_id = @t AND {f.ViewSql} ORDER BY updated_at DESC LIMIT 50", conn))
            {
                cmd.Parameters.AddWithValue("t", tenant);
                f.Bind(cmd);
                var sw = Stopwatch.StartNew();
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) { }
                page.Add(sw.Elapsed.TotalMilliseconds);
            }
            await using (var cmd = new NpgsqlCommand($"SELECT count(*) FROM tickets WHERE tenant_id = @t AND {f.ViewSql}", conn))
            {
                cmd.Parameters.AddWithValue("t", tenant);
                f.Bind(cmd);
                var sw = Stopwatch.StartNew();
                count = (long)(await cmd.ExecuteScalarAsync())!;
                cnt.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        page.Sort();
        cnt.Sort();
        return (page[runs / 2], cnt[runs / 2], count);
    }
}
