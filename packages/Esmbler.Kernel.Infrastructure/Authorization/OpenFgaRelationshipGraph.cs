using System.Collections.Concurrent;
using System.Text.Json;
using Esmbler.Kernel.Authorization;
using Microsoft.Extensions.Logging;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;

namespace Esmbler.Kernel.Infrastructure.Authorization;

public sealed class OpenFgaOptions
{
    public required string ApiUrl { get; set; }

    /// <summary>Per-call deadline; on expiry the decision fails closed.</summary>
    public int TimeoutMs { get; set; } = 2000;
}

/// <summary>
/// OpenFGA adapter (ADR 0004). One store per tenant with the fixed platform model. Reads use HIGHER_CONSISTENCY so
/// revocations apply to the next request (bounded staleness 0 in Gate 1). Every failure becomes
/// <see cref="AuthorizationUnavailableException"/>: callers deny (fail closed).
/// </summary>
public sealed class OpenFgaRelationshipGraph : IRelationshipGraph, IDisposable
{
    private static readonly string ModelJson = ReadModel();
    private readonly OpenFgaClient _client;
    private readonly IAuthzStoreRegistry _registry;
    private readonly ILogger<OpenFgaRelationshipGraph> _logger;
    private readonly int _timeoutMs;
    private readonly ConcurrentDictionary<Guid, (string StoreId, string ModelId)> _stores = new();
    private readonly SemaphoreSlim _provisionLock = new(1, 1);

    public OpenFgaRelationshipGraph(OpenFgaOptions options, HttpClient http, IAuthzStoreRegistry registry, ILogger<OpenFgaRelationshipGraph> logger)
    {
        http.Timeout = TimeSpan.FromMilliseconds(options.TimeoutMs);
        _client = new OpenFgaClient(new ClientConfiguration { ApiUrl = options.ApiUrl, RetryParams = new RetryParams { MaxRetry = 1, MinWaitInMs = 50 } }, http);
        _registry = registry;
        _logger = logger;
        _timeoutMs = options.TimeoutMs;
    }

    public async Task EnsureTenantAsync(Guid tenantId, CancellationToken cancellationToken) => await StoreAsync(tenantId, create: true, cancellationToken);

    public async Task<(IReadOnlySet<string> Roles, IReadOnlySet<Guid> Groups)> GetFactsAsync(Guid tenantId, Guid principalId, CancellationToken cancellationToken)
    {
        var (store, model) = await StoreAsync(tenantId, create: false, cancellationToken);
        var user = $"user:{principalId}";
        var groupsTask = Guard(ct => _client.ListObjects(new ClientListObjectsRequest { User = user, Relation = "member", Type = "group" }, Options(store, model), ct), cancellationToken);
        var rolesTask = Guard(ct => _client.ListObjects(new ClientListObjectsRequest { User = user, Relation = "assignee", Type = "role" }, Options(store, model), ct), cancellationToken);
        var groups = (await groupsTask).Objects.Select(o => Guid.Parse(o["group:".Length..])).ToHashSet();
        var roles = (await rolesTask).Objects.Select(o => o["role:".Length..]).ToHashSet(StringComparer.Ordinal);
        return (roles, groups);
    }

    public async Task<bool> IsRelatedAsync(Guid tenantId, Guid principalId, Guid recordId, IReadOnlyList<RelationshipTuple> recordTuples, CancellationToken cancellationToken)
    {
        var (store, model) = await StoreAsync(tenantId, create: false, cancellationToken);
        var response = await Guard(ct => _client.Check(
            new ClientCheckRequest
            {
                User = $"user:{principalId}",
                Relation = "related",
                Object = $"record:{recordId}",
                ContextualTuples = recordTuples.Select(t => new ClientTupleKey { User = t.User, Relation = t.Relation, Object = t.Object }).ToList(),
            },
            new ClientCheckOptions { StoreId = store, AuthorizationModelId = model, Consistency = ConsistencyPreference.HIGHERCONSISTENCY },
            ct), cancellationToken);
        return response.Allowed == true;
    }

    public async Task WriteAsync(Guid tenantId, IReadOnlyList<RelationshipTuple> writes, IReadOnlyList<RelationshipTuple> deletes, CancellationToken cancellationToken)
    {
        if (writes.Count == 0 && deletes.Count == 0)
        {
            return;
        }

        var (store, model) = await StoreAsync(tenantId, create: true, cancellationToken);
        var request = new ClientWriteRequest(
            writes.Select(t => new ClientTupleKey { User = t.User, Relation = t.Relation, Object = t.Object }).ToList(),
            deletes.Select(t => new ClientTupleKeyWithoutCondition { User = t.User, Relation = t.Relation, Object = t.Object }).ToList());
        await Guard(ct => _client.Write(request, new ClientWriteOptions
        {
            StoreId = store,
            AuthorizationModelId = model,
            Conflict = new ConflictOptions { OnDuplicateWrites = OnDuplicateWrites.Ignore, OnMissingDeletes = OnMissingDeletes.Ignore },
            Transaction = new TransactionOptions { Disable = true, MaxPerChunk = 100 },
        }, ct), cancellationToken);
    }

    private async Task<(string StoreId, string ModelId)> StoreAsync(Guid tenantId, bool create, CancellationToken cancellationToken)
    {
        if (_stores.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        if (await _registry.GetAsync(tenantId, cancellationToken) is { } saved)
        {
            _stores[tenantId] = saved;
            return saved;
        }

        if (!create)
        {
            throw new AuthorizationUnavailableException("This tenant has no authorization store yet.");
        }

        await _provisionLock.WaitAsync(cancellationToken);
        try
        {
            if (await _registry.GetAsync(tenantId, cancellationToken) is { } again)
            {
                return _stores[tenantId] = again;
            }

            var store = await Guard(ct => _client.CreateStore(new ClientCreateStoreRequest { Name = $"tenant-{tenantId:N}" }, null, ct), cancellationToken);
            var modelRequest = JsonSerializer.Deserialize<ClientWriteAuthorizationModelRequest>(ModelJson)!;
            var model = await Guard(ct => _client.WriteAuthorizationModel(modelRequest, new ClientWriteOptions { StoreId = store.Id }, ct), cancellationToken);
            await _registry.SaveAsync(tenantId, store.Id, model.AuthorizationModelId, cancellationToken);
            _logger.LogInformation("Created authorization store {Store} for tenant {Tenant}", store.Id, tenantId);
            return _stores[tenantId] = (store.Id, model.AuthorizationModelId);
        }
        finally
        {
            _provisionLock.Release();
        }
    }

    private static ClientListObjectsOptions Options(string store, string model) =>
        new() { StoreId = store, AuthorizationModelId = model, Consistency = ConsistencyPreference.HIGHERCONSISTENCY };

    private async Task<T> Guard<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeoutMs);
        try
        {
            return await call(deadline.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Authorization service call failed; denying");
            throw new AuthorizationUnavailableException("Access could not be verified right now, so it was denied. Try again shortly.", ex);
        }
    }

    private static string ReadModel()
    {
        var assembly = typeof(OpenFgaRelationshipGraph).Assembly;
        using var stream = assembly.GetManifestResourceStream("authz/platform-model.fga.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose() => _provisionLock.Dispose();
}
