using System.Text.Json.Nodes;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Approvals;

/// <summary>
/// Starts approvals and cancels them (ADR 0016). On any event of a request in its initial state whose catalog item
/// names a policy, it starts the approval (once per request); when a request enters a cancelled-category state it
/// cancels the pending approval. It reads current state, never the event payload, so duplicates are harmless.
/// </summary>
public sealed class ApprovalConsumer(ConfigStore configs, ApprovalService approvals) : IEventConsumer
{
    public string Name => "approvals";

    public bool Handles(EventEnvelope envelope) =>
        envelope.RecordId is not null && envelope.Type is EventTypes.RecordCreated or EventTypes.RecordTransitioned or EventTypes.RecordUpdated or EventTypes.RecordRestored;

    public async Task HandleAsync(EventEnvelope envelope, KernelDbContext db, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, envelope.TenantId, envelope.EnvironmentId, ct);
        if (artifact.Config.FindEntity(envelope.Entity ?? string.Empty) is not { Request.Approval: not null } entity || artifact.Config.WorkflowFor(entity.Id) is not { } flow)
        {
            return;
        }

        var row = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == envelope.RecordId && r.DeletedAt == null, ct);
        if (row is null)
        {
            return;
        }

        var ctx = new RequestContext
        {
            TenantId = envelope.TenantId,
            EnvironmentId = envelope.EnvironmentId,
            PrincipalId = envelope.Actor.StartsWith("user:", StringComparison.Ordinal) && Guid.TryParse(envelope.Actor[5..], out var actor) ? actor : Guid.Empty,
            Channel = "worker",
            CorrelationId = envelope.CorrelationId,
        };
        var state = JsonNode.Parse(row.Data)?[flow.StateField]?.GetValue<string>() ?? flow.InitialState;
        if (state == flow.InitialState)
        {
            await approvals.StartIfNeededAsync(db, ctx, artifact, entity, row.Id, ct);
        }
        else if (flow.FindState(state)?.Category == StateCategories.Cancelled)
        {
            await approvals.CancelForRecordAsync(db, ctx, artifact, entity, row.Id, ct);
        }
    }
}

/// <summary>Stage expiry timer (ADR 0016): escalates or declines a stage nobody decided in time.</summary>
public sealed class ApprovalExpiryJobHandler(ApprovalService approvals) : IJobHandler
{
    public string Kind => ApprovalService.ExpireJobKind;

    /// <summary>A platform timer set by a published policy; it acts for nobody, so there is nothing to re-authorize.</summary>
    public string? RequiredAction => null;

    public Task RunAsync(JobContext job, CancellationToken ct) =>
        job.Database.InTenantAsync(job.TenantId, job.EnvironmentId, null, async db =>
        {
            await approvals.ExpireAsync(db, job.TenantId, job.EnvironmentId, Guid.Parse(job.Payload["approval"]!.GetValue<string>()), job.Payload["stage"]!.GetValue<int>(), ct);
            return 0;
        }, ct);
}
