using System.Diagnostics;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Exceptions;
using OpenFga.Sdk.Model;

namespace AuthzSpike;

/// <summary>Tenant-environment scope. Each scope maps to exactly one OpenFGA store + pinned model id.</summary>
public sealed record AuthzScope(string TenantEnvironment, string StoreId, string ModelId);

/// <summary>A relationship derived from the record row (never stored in the engine).</summary>
public sealed record RecordTuple(string User, string Relation, string Object);

public sealed record CheckQuery(
    string User,
    string Relation,
    string Object,
    IReadOnlyList<RecordTuple>? RecordTuples = null,
    IReadOnlyDictionary<string, object>? Attributes = null,
    bool HigherConsistency = false);

public enum DecisionReason { Granted, NotGranted, EngineUnavailable, EngineError }

public sealed record Decision(bool Allowed, DecisionReason Reason, string? Detail = null)
{
    public static Decision Deny(DecisionReason r, string? d = null) => new(false, r, d);
}

/// <summary>
/// Port used by the kernel's policy decision point. The production kernel would expose this
/// (plus IRelationshipGraph for tuple writes); an in-memory fake implements it for unit tests.
/// </summary>
public interface IAuthorizationEngine
{
    Task<Decision> CheckAsync(AuthzScope scope, CheckQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<Decision>> BatchCheckAsync(AuthzScope scope, IReadOnlyList<CheckQuery> queries, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListObjectsAsync(AuthzScope scope, string user, string relation, string type, bool higherConsistency = false, CancellationToken ct = default);
}

/// <summary>OpenFGA adapter. Fails closed: any transport/engine failure becomes a Deny decision.</summary>
public sealed class OpenFgaAuthorizationEngine(OpenFgaClient client) : IAuthorizationEngine
{
    public async Task<Decision> CheckAsync(AuthzScope scope, CheckQuery q, CancellationToken ct = default)
    {
        try
        {
            var res = await client.Check(ToRequest(q), new ClientCheckOptions
            {
                StoreId = scope.StoreId,
                AuthorizationModelId = scope.ModelId,
                Consistency = q.HigherConsistency ? ConsistencyPreference.HIGHERCONSISTENCY : ConsistencyPreference.MINIMIZELATENCY,
            }, ct);
            return res.Allowed == true ? new Decision(true, DecisionReason.Granted) : Decision.Deny(DecisionReason.NotGranted);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return Decision.Deny(DecisionReason.EngineUnavailable, $"{ex.GetType().Name}: {Trim(ex.Message)}");
        }
        catch (FgaApiError ex)
        {
            return Decision.Deny(DecisionReason.EngineError, $"{ex.GetType().Name} {(int)ex.StatusCode} {ex.ApiError?.ErrorCode}: {Trim(ex.ApiError?.Message ?? ex.Message)}");
        }
    }

    public async Task<IReadOnlyList<Decision>> BatchCheckAsync(AuthzScope scope, IReadOnlyList<CheckQuery> queries, CancellationToken ct = default)
    {
        try
        {
            var items = queries.Select((q, i) => new ClientBatchCheckItem
            {
                User = q.User,
                Relation = q.Relation,
                Object = q.Object,
                CorrelationId = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ContextualTuples = q.RecordTuples is null ? null : new ContextualTupleKeys(q.RecordTuples.Select(t => new TupleKey(t.User, t.Relation, t.Object, null!)).ToList()),
                Context = q.Attributes,
            }).ToList();
            var res = await client.BatchCheck(new ClientBatchCheckRequest { Checks = items }, new ClientBatchCheckOptions
            {
                StoreId = scope.StoreId,
                AuthorizationModelId = scope.ModelId,
                Consistency = queries.Any(q => q.HigherConsistency) ? ConsistencyPreference.HIGHERCONSISTENCY : ConsistencyPreference.MINIMIZELATENCY,
            }, ct);
            var byId = res.Result.ToDictionary(r => r.CorrelationId);
            return queries.Select((_, i) =>
            {
                var r = byId[i.ToString(System.Globalization.CultureInfo.InvariantCulture)];
                if (r.Error is not null) return Decision.Deny(DecisionReason.EngineError, r.Error.Message);
                return r.Allowed ? new Decision(true, DecisionReason.Granted) : Decision.Deny(DecisionReason.NotGranted);
            }).ToList();
        }
        catch (Exception ex) when (IsUnavailable(ex) || ex is FgaApiError)
        {
            var d = Decision.Deny(IsUnavailable(ex) ? DecisionReason.EngineUnavailable : DecisionReason.EngineError, Trim(ex.Message));
            return queries.Select(_ => d).ToList();
        }
    }

    public async Task<IReadOnlyList<string>> ListObjectsAsync(AuthzScope scope, string user, string relation, string type, bool higherConsistency = false, CancellationToken ct = default)
    {
        // No fail-open fallback: list callers must treat an exception as "no access" (empty predicate => FALSE).
        var res = await client.ListObjects(new ClientListObjectsRequest { User = user, Relation = relation, Type = type }, new ClientListObjectsOptions
        {
            StoreId = scope.StoreId,
            AuthorizationModelId = scope.ModelId,
            Consistency = higherConsistency ? ConsistencyPreference.HIGHERCONSISTENCY : ConsistencyPreference.MINIMIZELATENCY,
        }, ct);
        return res.Objects;
    }

    public static ClientCheckRequest ToRequest(CheckQuery q) => new()
    {
        User = q.User,
        Relation = q.Relation,
        Object = q.Object,
        ContextualTuples = q.RecordTuples?.Select(t => new ClientTupleKey { User = t.User, Relation = t.Relation, Object = t.Object }).ToList(),
        Context = q.Attributes,
    };

    internal static bool IsUnavailable(Exception ex) => ex switch
    {
        HttpRequestException => true,
        TaskCanceledException => true,   // HttpClient.Timeout
        OperationCanceledException => true,
        FgaApiError f when (int)f.StatusCode >= 500 || f.StatusCode == 0 => true,
        FgaError { InnerException: not null } f => IsUnavailable(f.InnerException!),
        _ => false,
    };

    private static string Trim(string s) => s.Length > 160 ? s[..160] + "..." : s;
}

/// <summary>Ticket row as stored in Postgres (the source of truth for record attributes).</summary>
public sealed record TicketRow(long Id, int Priority, string Requester, string? Assignee, string? OwnerGroup, IReadOnlyList<string> Watchers)
{
    public string Object => $"ticket:{Id}";

    /// <summary>Record attribute relations derived from the row, sent as contextual tuples on every Check.</summary>
    public IReadOnlyList<RecordTuple> ToRecordTuples()
    {
        var list = new List<RecordTuple>
        {
            new("entity_type:ticket", "entity", Object),
            new($"user:{Requester}", "requester", Object),
        };
        if (Assignee is not null) list.Add(new($"user:{Assignee}", "assignee", Object));
        if (OwnerGroup is not null) list.Add(new($"group:{OwnerGroup}", "owner_group", Object));
        list.AddRange(Watchers.Select(w => new RecordTuple($"user:{w}", "watcher", Object)));
        return list;
    }

    public IReadOnlyDictionary<string, object> Attributes => new Dictionary<string, object> { ["priority"] = Priority };
}

public static class Timing
{
    public static async Task<(double mean, double p50, double p95, double p99, double total)> MeasureAsync(int n, int concurrency, Func<int, Task> op)
    {
        var lat = new double[n];
        var sw = Stopwatch.StartNew();
        var next = -1;
        var workers = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < n)
            {
                var t = Stopwatch.GetTimestamp();
                await op(i);
                lat[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            }
        });
        await Task.WhenAll(workers);
        var total = sw.Elapsed.TotalMilliseconds;
        Array.Sort(lat);
        return (lat.Average(), lat[n / 2], lat[(int)(n * 0.95)], lat[Math.Min(n - 1, (int)(n * 0.99))], total);
    }
}
