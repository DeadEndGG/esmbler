using System.Text.Json;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Infrastructure.Services;

/// <summary>Appends audit entries in the caller's transaction (REQ-REC-007, REQ-AI-006). Never logs secrets or record bodies.</summary>
public static class AuditWriter
{
    public static void Add(KernelDbContext db, RequestContext ctx, string operation, string targetType, string? targetId, int? configRevision, object? details = null, string outcome = "success", bool environmentScoped = true) =>
        db.AuditLog.Add(new AuditRow
        {
            TenantId = ctx.TenantId,
            EnvironmentId = environmentScoped ? ctx.EnvironmentId : null,
            At = DateTimeOffset.UtcNow,
            Actor = ctx.PrincipalId,
            Agent = ctx.AgentId,
            Channel = ctx.Channel,
            Operation = operation,
            TargetType = targetType,
            TargetId = targetId,
            ConfigRevision = configRevision,
            Outcome = outcome,
            CorrelationId = ctx.CorrelationId,
            Details = details is null ? "{}" : JsonSerializer.Serialize(details, SpecJson.Options),
        });

    /// <summary>An entry written by the platform itself (governors, workers), with no acting person.</summary>
    public static void AddSystem(KernelDbContext db, Guid tenantId, Guid environmentId, string operation, string targetType, string? targetId, object? details = null, string outcome = "denied") =>
        db.AuditLog.Add(new AuditRow
        {
            TenantId = tenantId,
            EnvironmentId = environmentId,
            At = DateTimeOffset.UtcNow,
            Channel = "system",
            Operation = operation,
            TargetType = targetType,
            TargetId = targetId,
            Outcome = outcome,
            Details = details is null ? "{}" : JsonSerializer.Serialize(details, SpecJson.Options),
        });
}
