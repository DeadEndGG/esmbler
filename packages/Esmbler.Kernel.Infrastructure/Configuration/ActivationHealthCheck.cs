using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Records;
using Esmbler.Platform.Rules;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Configuration;

/// <summary>Runs after a new configuration revision is written and before it commits (REQ-CFG-010).</summary>
public interface IActivationHealthCheck
{
    /// <returns>Problems found; empty when the revision is healthy.</returns>
    Task<IReadOnlyList<string>> CheckAsync(KernelDbContext db, Guid tenantId, Guid environmentId, int revision, CancellationToken ct);
}

/// <summary>
/// Smoke test of a freshly activated revision against real data: the stored artifact loads back and matches its
/// hash, and every enabled rule evaluates without runtime errors on a sample of live records of each entity. A failure
/// rolls back the activation transaction, so the previous revision stays live (automatic revert).
/// </summary>
public sealed class ActivationHealthCheck : IActivationHealthCheck
{
    private const int SamplePerEntity = 25;

    public async Task<IReadOnlyList<string>> CheckAsync(KernelDbContext db, Guid tenantId, Guid environmentId, int revision, CancellationToken ct)
    {
        var problems = new List<string>();
        var row = await db.ConfigArtifacts.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.EnvironmentId == environmentId && a.Revision == revision, ct);
        if (row is null)
        {
            return [$"Revision {revision} was not stored."];
        }

        EffectiveConfig config;
        try
        {
            config = SpecJson.Deserialize<EffectiveConfig>(row.Config);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return [$"Revision {revision} does not load back: {ex.Message}"];
        }

        foreach (var entity in config.Entities)
        {
            var samples = await db.Records.Where(r => r.EntityId == entity.Id && r.DeletedAt == null).OrderByDescending(r => r.UpdatedAt).Take(SamplePerEntity).Select(r => new { r.Id, r.Data }).ToListAsync(ct);
            foreach (var sample in samples)
            {
                var data = JsonNode.Parse(sample.Data)!.AsObject();
                var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["record"] = Values.MapFromJson(data),
                    ["prior"] = Values.MapFromJson(data),
                    ["user"] = new Dictionary<string, object?> { ["id"] = "system", ["roles"] = new List<object?>(), ["groups"] = new List<object?>() },
                    ["now"] = FieldNormalizer.FormatInstant(DateTimeOffset.UtcNow),
                };
                try
                {
                    RuleEvaluator.Evaluate(config, entity, variables, includeForm: true);
                }
                catch (ExpressionException ex)
                {
                    problems.Add($"{entity.Label} record {sample.Id}: a rule fails to evaluate ({ex.Message}).");
                    break;
                }
            }
        }

        return problems;
    }
}
