using System.Text.Json.Nodes;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Identity;

public sealed record AccessExplanation(Guid Principal, string PrincipalName, string Entity, Guid? Record, bool CanRead, bool CanUpdate, string? FieldAccess, IReadOnlyList<AccessReason> Reasons);

/// <summary>
/// "Why can Maria see this ticket?" (REQ-AUTHZ-009). Explaining someone else's access requires permission
/// administration or audit rights and never returns record values, only the decision and its reasons.
/// </summary>
public sealed class AccessExplainer(Database database, ConfigStore configs, AccessService access)
{
    public Task<AccessExplanation> ExplainAsync(RequestContext ctx, string entityId, Guid? recordId, Guid? principalId, string? field, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var callerPerms = await access.ForAsync(db, ctx, artifact.Config, ct);
            if (!ctx.HasScope(AgentScopes.AccessExplain))
            {
                throw KernelException.Forbidden("This agent may not explain access.");
            }

            var entity = artifact.Config.FindEntity(entityId) ?? throw KernelException.NotFound($"Entity '{entityId}'");
            var subjectId = principalId ?? ctx.PrincipalId;
            if (subjectId != ctx.PrincipalId && !callerPerms.Can(PlatformActions.PermissionsAdmin) && !callerPerms.Can(PlatformActions.AuditRead))
            {
                throw KernelException.Forbidden("Explaining another person's access requires permission administration or audit rights.");
            }

            JsonObject? data = null;
            if (recordId is { } rid)
            {
                var row = await db.Records.FirstOrDefaultAsync(r => r.Id == rid && r.EntityId == entity.Id && r.DeletedAt == null, ct) ?? throw KernelException.NotFound(entity.Label);
                data = JsonNode.Parse(row.Data)!.AsObject();
                if (!callerPerms.CanRead(entity, data))
                {
                    throw KernelException.NotFound(entity.Label);
                }
            }

            var subjectCtx = new RequestContext { TenantId = ctx.TenantId, EnvironmentId = ctx.EnvironmentId, PrincipalId = subjectId, Channel = ctx.Channel };
            EffectivePermissions subject;
            try
            {
                subject = await access.ForAsync(db, subjectCtx, artifact.Config, ct);
            }
            catch (KernelException ex) when (ex.Status == 403)
            {
                return new AccessExplanation(subjectId, "?", entity.Id, recordId, false, false, null, [new AccessReason("membership", "Is not a member of this workspace.")]);
            }

            var name = await db.Principals.Where(p => p.Id == subjectId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct) ?? "?";
            var reasons = subject.Explain(entity, data, field);
            return new AccessExplanation(
                subjectId,
                name,
                entity.Id,
                recordId,
                data is null ? subject.Entity(entity.Id).Read != RecordScopes.None : subject.CanRead(entity, data),
                data is null ? subject.Entity(entity.Id).Update != RecordScopes.None : subject.CanUpdate(entity, data),
                field is null ? null : subject.FieldAccess(entity, field),
                reasons);
        }, ct);
}
