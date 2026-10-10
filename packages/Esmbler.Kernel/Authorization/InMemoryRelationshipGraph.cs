using System.Collections.Concurrent;

namespace Esmbler.Kernel.Authorization;

/// <summary>Deterministic in-process graph with the same semantics as the platform OpenFGA model. Tests and tooling only.</summary>
public sealed class InMemoryRelationshipGraph : IRelationshipGraph
{
    private readonly ConcurrentDictionary<Guid, HashSet<RelationshipTuple>> _tuples = new();

    public bool Unavailable { get; set; }

    public Task EnsureTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        _tuples.TryAdd(tenantId, []);
        return Task.CompletedTask;
    }

    public Task WriteAsync(Guid tenantId, IReadOnlyList<RelationshipTuple> writes, IReadOnlyList<RelationshipTuple> deletes, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var set = _tuples.GetOrAdd(tenantId, _ => []);
        lock (set)
        {
            foreach (var d in deletes)
            {
                set.Remove(d);
            }

            foreach (var w in writes)
            {
                set.Add(w);
            }
        }

        return Task.CompletedTask;
    }

    public Task<(IReadOnlySet<string> Roles, IReadOnlySet<Guid> Groups)> GetFactsAsync(Guid tenantId, Guid principalId, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var set = Snapshot(tenantId);
        var groups = Groups(set, principalId);
        var subjects = groups.Select(g => $"group:{g}#member").Append($"user:{principalId}").ToHashSet(StringComparer.Ordinal);
        var roles = set.Where(t => t.Relation == "assignee" && t.Object.StartsWith("role:", StringComparison.Ordinal) && subjects.Contains(t.User))
            .Select(t => t.Object["role:".Length..]).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<(IReadOnlySet<string>, IReadOnlySet<Guid>)>((roles, groups));
    }

    public Task<bool> IsRelatedAsync(Guid tenantId, Guid principalId, Guid recordId, IReadOnlyList<RelationshipTuple> recordTuples, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var groups = Groups(Snapshot(tenantId), principalId);
        var record = $"record:{recordId}";
        var related = recordTuples.Any(t => t.Object == record && (
            (t.Relation is "requester" or "assignee" && t.User == $"user:{principalId}") ||
            (t.Relation == "owner_group" && Guid.TryParse(t.User["group:".Length..], out var g) && groups.Contains(g))));
        return Task.FromResult(related);
    }

    private HashSet<RelationshipTuple> Snapshot(Guid tenantId)
    {
        var set = _tuples.GetOrAdd(tenantId, _ => []);
        lock (set)
        {
            return [.. set];
        }
    }

    private static HashSet<Guid> Groups(HashSet<RelationshipTuple> set, Guid principalId)
    {
        var result = new HashSet<Guid>();
        var frontier = new Queue<string>([$"user:{principalId}"]);
        while (frontier.Count > 0)
        {
            var subject = frontier.Dequeue();
            foreach (var t in set.Where(t => t.Relation == "member" && t.User == subject && t.Object.StartsWith("group:", StringComparison.Ordinal)))
            {
                var id = Guid.Parse(t.Object["group:".Length..]);
                if (result.Add(id))
                {
                    frontier.Enqueue($"group:{id}#member");
                }
            }
        }

        return result;
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new AuthorizationUnavailableException("The authorization service is unavailable.");
        }
    }
}
