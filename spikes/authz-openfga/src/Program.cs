// Esmbler Gate 0 authorization spike: OpenFGA + OpenFga.Sdk (.NET) with the Tickets permission model.
// Throwaway code. See ../README.md for how to run and the observed results.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AuthzSpike;
using Npgsql;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Exceptions;
using OpenFga.Sdk.Model;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var fgaUrl = Arg("--fga") ?? "http://localhost:18080";
var pgConn = Arg("--pg");                       // optional: enables SQL predicate conformance + list timing
var dockerName = Arg("--docker-pause");         // optional: container name to pause for the timeout fail-closed test
var failClosedOnly = args.Contains("--fail-closed-only");
var ticketsPerTenant = int.Parse(Arg("--tickets") ?? "500000", CultureInfo.InvariantCulture);

var results = new List<(string name, bool pass, string detail)>();
void Assert(string name, bool cond, string detail = "")
{
    results.Add((name, cond, detail));
    Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -- " + detail : "")}");
}
void Section(string s) => Console.WriteLine($"\n=== {s} ===");
void Metric(string s) => Console.WriteLine($"  [metric] {s}");

// One shared HttpClient: per-request StoreId/AuthorizationModelId means one client serves every tenant store.
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
var client = new OpenFgaClient(new ClientConfiguration { ApiUrl = fgaUrl, RetryParams = new RetryParams { MaxRetry = 1, MinWaitInMs = 50 } }, http);
IAuthorizationEngine engine = new OpenFgaAuthorizationEngine(client);

if (failClosedOnly)
{
    await FailClosedAgainstStoppedEngine();
    return Summary();
}

// ---------------------------------------------------------------------------------------------
Section("Setup: store per tenant-environment + model");
var modelJson = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "model", "tickets.json"));
var modelReq = JsonSerializer.Deserialize<ClientWriteAuthorizationModelRequest>(modelJson)!;

async Task<AuthzScope> ProvisionAsync(string tenantEnv)
{
    var store = await client.CreateStore(new ClientCreateStoreRequest { Name = tenantEnv });
    var model = await client.WriteAuthorizationModel(modelReq, new ClientWriteOptions { StoreId = store.Id });
    return new AuthzScope(tenantEnv, store.Id, model.AuthorizationModelId);
}

var sw = Stopwatch.StartNew();
var a = await ProvisionAsync(TicketDb.TenantA);
Metric($"first store+model provision: {sw.Elapsed.TotalMilliseconds:F1} ms (store={a.StoreId}, model={a.ModelId})");
var b = await ProvisionAsync(TicketDb.TenantB);

const int extraStores = 100;
var prov = await Timing.MeasureAsync(extraStores, 1, i => ProvisionAsync($"bulk-{i}-sandbox"));
Metric($"provision {extraStores} stores (CreateStore+WriteAuthorizationModel, sequential): mean {prov.mean:F2} ms, p95 {prov.p95:F2} ms, total {prov.total:F0} ms");

// Stored tuples = memberships, role assignments, tenant policy. NO record attribute tuples.
var stored = new List<ClientTupleKey>
{
    T("user:carol", "member", "group:network-team"),
    T("group:network-team#member", "member", "group:l1-support"),   // nested group
    T("user:hank", "member", "group:l1-support"),
    T("user:frank", "member", "group:service-desk-managers"),
    T("group:service-desk-managers#member", "assignee", "role:ticket_read_all"), // role assigned to group
    T("user:kim", "assignee", "role:ticket_edit_all"),
    T("user:hank", "assignee", "role:major_incident_manager"),
    T("user:gina", "assignee", "role:finance"),
    T("user:ivan", "assignee", "role:tenant_admin"),
    T("user:judy", "assignee", "role:security_admin"),
    // tenant policy (would be compiled from versioned permission-set specs)
    T("role:ticket_read_all#assignee", "read_all", "entity_type:ticket"),
    T("role:ticket_edit_all#assignee", "edit_all", "entity_type:ticket"),
    new() { User = "user:*", Relation = "priority_editor", Object = "entity_type:ticket", Condition = new RelationshipCondition { Name = "not_major_incident", Context = new Dictionary<string, object>() } },
    T("role:major_incident_manager#assignee", "priority_editor", "entity_type:ticket"),
    T("role:finance#assignee", "reader", "field:ticket.internal_cost"),
    T("role:tenant_admin#assignee", "config_publish", $"tenant:{TicketDb.TenantA}"),
    T("role:security_admin#assignee", "permissions_admin", $"tenant:{TicketDb.TenantA}"),
    // AI agent delegation and scopes
    T("user:carol", "acts_for", "agent:triage-bot"),
    T("agent:triage-bot", "agent_read_scope", "entity_type:ticket"),
};
await client.Write(new ClientWriteRequest(stored, []), new ClientWriteOptions { StoreId = a.StoreId, AuthorizationModelId = a.ModelId });
Metric($"wrote {stored.Count} policy/membership tuples to {a.TenantEnvironment}");

var t1 = new TicketRow(1, 3, "alice", "bob", "l1-support", ["dave"]);
var t2 = new TicketRow(2, 1, "alice", "bob", "l1-support", []);

async Task<bool> Can(AuthzScope s, string user, string rel, TicketRow t, bool hc = false) =>
    (await engine.CheckAsync(s, new CheckQuery($"user:{user}", rel, t.Object, t.ToRecordTuples(), t.Attributes, hc))).Allowed;
async Task<bool> Has(AuthzScope s, string user, string rel, string obj) =>
    (await engine.CheckAsync(s, new CheckQuery(user, rel, obj))).Allowed;

// ---------------------------------------------------------------------------------------------
Section("Record level (contextual tuples derived from the ticket row)");
foreach (var (u, exp) in new[] { ("alice", true), ("bob", true), ("dave", true), ("carol", true), ("hank", true), ("frank", true), ("kim", true), ("erin", false), ("gina", false) })
    Assert($"view  T1(P3) {u,-6} => {exp}", await Can(a, u, "can_view", t1) == exp);
foreach (var (u, exp) in new[] { ("bob", true), ("carol", true), ("kim", true), ("alice", false), ("dave", false), ("frank", false), ("erin", false) })
    Assert($"edit  T1(P3) {u,-6} => {exp}", await Can(a, u, "can_edit", t1) == exp);
Assert("no stored record tuples: alice view T1 WITHOUT contextual tuples => false",
    !await Has(a, "user:alice", "can_view", t1.Object));

// ---------------------------------------------------------------------------------------------
Section("ABAC: CEL condition priority != 1 unless major_incident_manager");
foreach (var (u, exp) in new[] { ("bob", false), ("carol", false), ("kim", false), ("hank", true) })
    Assert($"edit  T2(P1) {u,-6} => {exp}", await Can(a, u, "can_edit", t2) == exp);
Assert("view  T2(P1) bob still => true (view not priority-guarded)", await Can(a, "bob", "can_view", t2));
var missingCtx = await engine.CheckAsync(a, new CheckQuery("user:bob", "can_edit", t2.Object, t2.ToRecordTuples(), Attributes: null));
Assert("edit T2 with MISSING priority context => denied (not an exception)", !missingCtx.Allowed, $"{missingCtx.Reason} {missingCtx.Detail}");
var hankMissing = await engine.CheckAsync(a, new CheckQuery("user:hank", "can_edit", t2.Object, t2.ToRecordTuples(), Attributes: null));
Metric($"hank edit T2 with missing context => {hankMissing.Allowed} ({hankMissing.Reason}) -- MIM path does not need the parameter");

// ---------------------------------------------------------------------------------------------
Section("Field level");
Assert("field internal_cost reader gina(finance) => true", await Has(a, "user:gina", "reader", "field:ticket.internal_cost"));
Assert("field internal_cost reader bob => false", !await Has(a, "user:bob", "reader", "field:ticket.internal_cost"));

Section("Action level");
Assert("config.publish ivan(tenant_admin) => true", await Has(a, "user:ivan", "config_publish", $"tenant:{TicketDb.TenantA}"));
Assert("permissions.admin ivan(tenant_admin) => false (separate from publish)", !await Has(a, "user:ivan", "permissions_admin", $"tenant:{TicketDb.TenantA}"));
Assert("permissions.admin judy(security_admin) => true", await Has(a, "user:judy", "permissions_admin", $"tenant:{TicketDb.TenantA}"));
Assert("config.publish judy => false", !await Has(a, "user:judy", "config_publish", $"tenant:{TicketDb.TenantA}"));

// ---------------------------------------------------------------------------------------------
Section("AI agent acting for a user: user perms AND agent scope AND delegation (one BatchCheck)");
async Task<bool> AgentCan(string agent, string onBehalfOf, string rel, TicketRow t)
{
    var scopeRel = rel == "can_view" ? "agent_read_scope" : "agent_edit_scope";
    var d = await engine.BatchCheckAsync(a,
    [
        new($"user:{onBehalfOf}", rel, t.Object, t.ToRecordTuples(), t.Attributes),
        new($"agent:{agent}", scopeRel, "entity_type:ticket"),
        new($"user:{onBehalfOf}", "acts_for", $"agent:{agent}"),
    ]);
    return d.All(x => x.Allowed);
}
Assert("triage-bot for carol view T1 => true", await AgentCan("triage-bot", "carol", "can_view", t1));
Assert("triage-bot for carol edit T1 => false (no agent_edit_scope though carol can edit)", !await AgentCan("triage-bot", "carol", "can_edit", t1));
Assert("triage-bot for erin view T1 => false (erin cannot view)", !await AgentCan("triage-bot", "erin", "can_view", t1));
Assert("triage-bot for alice view T1 => false (alice did not delegate)", !await AgentCan("triage-bot", "alice", "can_view", t1));

// ---------------------------------------------------------------------------------------------
Section("Tenant isolation: separate store per tenant-environment");
Assert("tenant B: carol view T1 (same row tuples) => false (memberships live in A's store)", !await Can(b, "carol", "can_view", t1));
Assert("tenant B: ListObjects(carol, member, group) => empty", (await engine.ListObjectsAsync(b, "user:carol", "member", "group")).Count == 0);
var crossModel = await engine.CheckAsync(b with { ModelId = a.ModelId }, new CheckQuery("user:carol", "member", "group:l1-support"));
Assert("tenant B store with tenant A's model id => error, denied", !crossModel.Allowed && crossModel.Reason == DecisionReason.EngineError, crossModel.Detail ?? "");
var readB = await client.Read(new ClientReadRequest(), new ClientReadOptions { StoreId = b.StoreId });
Assert("tenant B: Read() returns zero tuples", readB.Tuples.Count == 0);

// ---------------------------------------------------------------------------------------------
Section("Contextual tuple limits");
var manyWatchers = new TicketRow(3, 3, "alice", null, null, Enumerable.Range(0, 99).Select(i => $"w{i}").ToList()); // 2 + 99 = 101 tuples
var many = await engine.CheckAsync(a, new CheckQuery("user:w5", "can_view", manyWatchers.Object, manyWatchers.ToRecordTuples(), manyWatchers.Attributes));
Metric($"Check with {manyWatchers.ToRecordTuples().Count} contextual tuples => allowed={many.Allowed} reason={many.Reason} {many.Detail}");
var ok100 = manyWatchers with { Watchers = manyWatchers.Watchers.Take(98).ToList() };
var hundred = await engine.CheckAsync(a, new CheckQuery("user:w5", "can_view", ok100.Object, ok100.ToRecordTuples(), ok100.Attributes));
Assert("Check with exactly 100 contextual tuples works", hundred.Allowed, $"{hundred.Reason} {hundred.Detail}");
Assert("Check with 101 contextual tuples is rejected (=> watchers must be capped or watcher check done in SQL/kernel)", !many.Allowed && many.Reason == DecisionReason.EngineError);

// ---------------------------------------------------------------------------------------------
Section("Revocation (delete nested-group membership, check immediately)");
Assert("before: carol view T1 (HIGHER_CONSISTENCY) => true", await Can(a, "carol", "can_view", t1, hc: true));
sw.Restart();
await client.DeleteTuples([new ClientTupleKeyWithoutCondition { User = "group:network-team#member", Relation = "member", Object = "group:l1-support" }],
    new ClientWriteOptions { StoreId = a.StoreId, AuthorizationModelId = a.ModelId });
var afterHc = await Can(a, "carol", "can_view", t1, hc: true);
var revokeMs = sw.Elapsed.TotalMilliseconds;
Assert("after delete: carol view T1 (HIGHER_CONSISTENCY) => false", !afterHc, $"delete+check {revokeMs:F1} ms");
// With OPENFGA_CHECK_QUERY_CACHE_ENABLED this may still be true until the cache TTL expires (bounded staleness).
Metric($"after delete: carol view T1 (MINIMIZE_LATENCY) => {await Can(a, "carol", "can_view", t1)} (true = stale cached answer)");
Assert("after delete: ListObjects(carol, member, group) = [network-team]",
    (await engine.ListObjectsAsync(a, "user:carol", "member", "group", higherConsistency: true)).SequenceEqual(["group:network-team"]));
await client.WriteTuples([T("group:network-team#member", "member", "group:l1-support")], new ClientWriteOptions { StoreId = a.StoreId, AuthorizationModelId = a.ModelId });
Assert("restored: carol view T1 => true", await Can(a, "carol", "can_view", t1, hc: true));

// ---------------------------------------------------------------------------------------------
Section("Latency (local docker, memory datastore, single client)");
var nCheck = 1000;
var seq = await Timing.MeasureAsync(nCheck, 1, _ => Can(a, "carol", "can_view", t1));
Metric($"Check can_view (nested group via contextual owner_group), {nCheck} sequential: mean {seq.mean:F2} ms p50 {seq.p50:F2} p95 {seq.p95:F2} p99 {seq.p99:F2}; {nCheck / (seq.total / 1000):F0}/s");
// Same relationships stored as real tuples (dual-write model) for comparison with contextual tuples.
var stored9001 = new TicketRow(9001, 3, "alice", "bob", "l1-support", ["dave"]);
await client.WriteTuples(stored9001.ToRecordTuples().Select(t => T(t.User, t.Relation, t.Object)).ToList(), new ClientWriteOptions { StoreId = a.StoreId, AuthorizationModelId = a.ModelId });
var st = await Timing.MeasureAsync(nCheck, 1, _ => engine.CheckAsync(a, new CheckQuery("user:carol", "can_view", stored9001.Object, null, stored9001.Attributes)));
Metric($"Check can_view with STORED record tuples (no contextual), {nCheck} sequential: mean {st.mean:F2} ms p50 {st.p50:F2} p95 {st.p95:F2}");
var direct = await Timing.MeasureAsync(nCheck, 1, _ => Can(a, "alice", "can_view", t1));
Metric($"Check can_view direct requester (alice, contextual), {nCheck} sequential: mean {direct.mean:F2} ms p50 {direct.p50:F2} p95 {direct.p95:F2}");
var neg = await Timing.MeasureAsync(nCheck, 1, _ => Can(a, "erin", "can_view", t1));
Metric($"Check can_view negative (erin), {nCheck} sequential: mean {neg.mean:F2} ms p50 {neg.p50:F2} p95 {neg.p95:F2}");
var edit = await Timing.MeasureAsync(nCheck, 1, _ => Can(a, "hank", "can_edit", t2));
Metric($"Check can_edit P1 with CEL (hank), {nCheck} sequential: mean {edit.mean:F2} ms p50 {edit.p50:F2} p95 {edit.p95:F2}");
var par = await Timing.MeasureAsync(5000, 32, _ => Can(a, "carol", "can_view", t1));
Metric($"Check can_view, 5000 at concurrency 32: mean {par.mean:F2} ms p95 {par.p95:F2}; throughput {5000 / (par.total / 1000):F0}/s");
var batchItems = Enumerable.Range(0, 50).Select(i => new CheckQuery("user:carol", "can_view", $"ticket:{100 + i}",
    new TicketRow(100 + i, 3, "x", null, i % 2 == 0 ? "l1-support" : "g7", []).ToRecordTuples(), null)).ToList();
var batch = await Timing.MeasureAsync(100, 1, _ => engine.BatchCheckAsync(a, batchItems));
Metric($"BatchCheck 50 items (a page of rows), 100 sequential: mean {batch.mean:F2} ms p95 {batch.p95:F2}");
var lo = await Timing.MeasureAsync(nCheck, 1, _ => engine.ListObjectsAsync(a, "user:carol", "member", "group"));
Metric($"ListObjects(carol, member, group) {nCheck} sequential: mean {lo.mean:F2} ms p50 {lo.p50:F2} p95 {lo.p95:F2}");

// Larger membership graph: 200 groups, user in 40 of them (half via nesting).
var bulk = new List<ClientTupleKey>();
for (var i = 0; i < 200; i++) bulk.Add(T($"user:filler{i}", "member", $"group:bulk{i}"));
for (var i = 0; i < 20; i++) bulk.Add(T("user:wide", "member", $"group:bulk{i}"));
for (var i = 20; i < 40; i++) bulk.Add(T($"group:bulk{i - 20}#member", "member", $"group:bulk{i}"));
// Gotcha: a single Write call is one transaction capped at 100 tuples (server limit). For bulk loads
// disable the transaction mode and let the SDK chunk (each chunk is its own transaction).
await client.Write(new ClientWriteRequest(bulk, []), new ClientWriteOptions
{
    StoreId = a.StoreId,
    AuthorizationModelId = a.ModelId,
    Transaction = new TransactionOptions { Disable = true, MaxPerChunk = 100, MaxParallelRequests = 4 },
});
var wideGroups = await engine.ListObjectsAsync(a, "user:wide", "member", "group");
var loWide = await Timing.MeasureAsync(200, 1, _ => engine.ListObjectsAsync(a, "user:wide", "member", "group"));
Metric($"ListObjects(wide, member, group) -> {wideGroups.Count} groups (20 direct, 20 nested), 200 sequential: mean {loWide.mean:F2} ms p95 {loWide.p95:F2}");
var compile = await Timing.MeasureAsync(200, 1, _ => CompiledTicketFilter.CompileAsync(engine, a, "carol"));
Metric($"Compile SQL filter (1 ListObjects + 1 BatchCheck of 6): mean {compile.mean:F2} ms p95 {compile.p95:F2}");

// ---------------------------------------------------------------------------------------------
if (pgConn is not null)
{
    Section($"SQL predicate compilation + conformance (Postgres, {ticketsPerTenant:N0} tickets/tenant)");
    await using var ds = NpgsqlDataSource.Create(pgConn);
    var seedS = await TicketDb.SeedAsync(ds, ticketsPerTenant, ticketsPerTenant / 5);
    Metric($"seeded {ticketsPerTenant:N0} + {ticketsPerTenant / 5:N0} tickets in {seedS:F1} s");

    string[] testUsers = ["alice", "bob", "carol", "dave", "erin", "frank", "hank", "kim", "gina"];
    var sample = await TicketDb.SampleAsync(ds, TicketDb.TenantA, 300, ["alice", "bob", "dave"], ["l1-support", "network-team"]);
    var mismatches = 0;
    var compared = 0;
    foreach (var u in testUsers)
    {
        var f = await CompiledTicketFilter.CompileAsync(engine, a, u);
        foreach (var (rel, pred) in new[] { ("can_view", f.ViewSql), ("can_edit", f.EditSql) })
        {
            var sqlIds = await TicketDb.FilterIdsAsync(ds, TicketDb.TenantA, f, pred, sample.Select(s => s.Id));
            var engineIds = new HashSet<long>();
            foreach (var chunk in sample.Chunk(50))
            {
                var d = await engine.BatchCheckAsync(a, chunk.Select(t => new CheckQuery($"user:{u}", rel, t.Object, t.ToRecordTuples(), t.Attributes)).ToList());
                for (var i = 0; i < chunk.Length; i++) if (d[i].Allowed) engineIds.Add(chunk[i].Id);
            }
            var diff = sqlIds.Except(engineIds).Count() + engineIds.Except(sqlIds).Count();
            mismatches += diff;
            compared += sample.Count;
            Console.WriteLine($"    {u,-6} {rel,-8} engine={engineIds.Count,4} sql={sqlIds.Count,4} diff={diff}");
        }
    }
    Assert($"conformance: engine Check == SQL predicate over {compared} (user, relation, row) cases", mismatches == 0, $"{mismatches} mismatches, sample {sample.Count} rows");

    foreach (var u in new[] { "carol", "alice", "erin", "frank" })
    {
        var f = await CompiledTicketFilter.CompileAsync(engine, a, u);
        var (pageMs, countMs, count) = await TicketDb.TimeListAsync(ds, TicketDb.TenantA, f);
        Metric($"list {u,-6} groups=[{string.Join(",", f.Groups)}] readAll={f.ReadAll}: first page(50) {pageMs:F1} ms, count(*) {countMs:F1} ms, visible={count:N0}");
    }
    var fb = await CompiledTicketFilter.CompileAsync(engine, b, "carol");
    var (_, _, countB) = await TicketDb.TimeListAsync(ds, TicketDb.TenantB, fb, runs: 1);
    Metric($"tenant B list for carol (no memberships in B store) visible={countB:N0} (only rows where she is requester/assignee/watcher by id)");
}

// ---------------------------------------------------------------------------------------------
Section("Fail closed");
await FailClosedDeadEndpoint();
if (dockerName is not null) await FailClosedPausedContainer(dockerName);

return Summary();

// =============================================================================================
async Task FailClosedDeadEndpoint()
{
    using var deadHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var dead = new OpenFgaClient(new ClientConfiguration { ApiUrl = "http://localhost:18099", RetryParams = new RetryParams { MaxRetry = 1, MinWaitInMs = 50 } }, deadHttp);
    var raw = Stopwatch.StartNew();
    try
    {
        await dead.Check(OpenFgaAuthorizationEngine.ToRequest(new CheckQuery("user:bob", "can_edit", t1.Object, t1.ToRecordTuples(), t1.Attributes)),
            new ClientCheckOptions { StoreId = a.StoreId, AuthorizationModelId = a.ModelId });
        Assert("raw SDK Check against dead endpoint throws", false);
    }
    catch (Exception ex)
    {
        Assert("raw SDK Check against dead endpoint throws", true, $"{ex.GetType().FullName}{(ex.InnerException is null ? "" : " <- " + ex.InnerException.GetType().Name)} after {raw.Elapsed.TotalMilliseconds:F0} ms: {ex.Message.Split('\n')[0]}");
    }
    var d = await new OpenFgaAuthorizationEngine(dead).CheckAsync(a, new CheckQuery("user:bob", "can_edit", t1.Object, t1.ToRecordTuples(), t1.Attributes));
    Assert("PDP adapter: dead endpoint => Deny(EngineUnavailable)", !d.Allowed && d.Reason == DecisionReason.EngineUnavailable, d.Detail ?? "");
    try
    {
        await CompiledTicketFilter.CompileAsync(new OpenFgaAuthorizationEngine(dead), a, "carol");
        Assert("list filter compile against dead endpoint throws (no list served)", false);
    }
    catch (Exception ex)
    {
        Assert("list filter compile against dead endpoint throws (no list served)", true, ex.GetType().Name);
    }
}

async Task FailClosedPausedContainer(string name)
{
    Docker("pause", name);
    try
    {
        var t = Stopwatch.StartNew();
        var d = await engine.CheckAsync(a, new CheckQuery("user:bob", "can_edit", t1.Object, t1.ToRecordTuples(), t1.Attributes));
        Assert($"engine hung (docker pause) => Deny(EngineUnavailable) after HttpClient timeout", !d.Allowed && d.Reason == DecisionReason.EngineUnavailable, $"{t.Elapsed.TotalMilliseconds:F0} ms: {d.Detail}");
    }
    finally { Docker("unpause", name); }
    Assert("after unpause: bob edit T1 => true again", await Can(a, "bob", "can_edit", t1));
}

async Task FailClosedAgainstStoppedEngine()
{
    Section($"Fail closed against stopped engine at {fgaUrl}");
    var raw = Stopwatch.StartNew();
    try
    {
        await client.Check(new ClientCheckRequest { User = "user:bob", Relation = "can_view", Object = "ticket:1" }, new ClientCheckOptions { StoreId = "01JZZZZZZZZZZZZZZZZZZZZZZZ" });
        Assert("raw SDK Check against stopped container throws", false);
    }
    catch (Exception ex)
    {
        Assert("raw SDK Check against stopped container throws", true, $"{ex.GetType().FullName}{(ex.InnerException is null ? "" : " <- " + ex.InnerException.GetType().Name)} after {raw.Elapsed.TotalMilliseconds:F0} ms: {ex.Message.Split('\n')[0]}");
    }
    var d = await engine.CheckAsync(new AuthzScope("acme-prod", "01JZZZZZZZZZZZZZZZZZZZZZZZ", "01JZZZZZZZZZZZZZZZZZZZZZZZ"), new CheckQuery("user:bob", "can_view", "ticket:1"));
    Assert("PDP adapter => Deny(EngineUnavailable)", !d.Allowed && d.Reason == DecisionReason.EngineUnavailable, d.Detail ?? "");
}

int Summary()
{
    var failed = results.Count(r => !r.pass);
    Console.WriteLine($"\n=== SUMMARY: {results.Count - failed}/{results.Count} PASS, {failed} FAIL ===");
    foreach (var r in results.Where(r => !r.pass)) Console.WriteLine($"FAIL {r.name} {r.detail}");
    return failed == 0 ? 0 : 1;
}

static ClientTupleKey T(string user, string rel, string obj) => new() { User = user, Relation = rel, Object = obj };

static void Docker(string verb, string name)
{
    using var p = Process.Start(new ProcessStartInfo("docker", $"{verb} {name}") { RedirectStandardOutput = true, RedirectStandardError = true })!;
    p.WaitForExit();
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
