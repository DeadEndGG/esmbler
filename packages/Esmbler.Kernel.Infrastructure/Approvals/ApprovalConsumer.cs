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

/// <summary>Starts approvals for requests already in intake when their item gains a policy (scheduled at activation).</summary>
public sealed class ApprovalSweepJobHandler(ApprovalService approvals) : IJobHandler
{
    public string Kind => ApprovalService.SweepJobKind;

    /// <summary>The publish that set the policy carried the approval; the sweep only starts what the policy requires.</summary>
    public string? RequiredAction => null;

    public Task RunAsync(JobContext job, CancellationToken ct) =>
        job.Database.InTenantAsync(job.TenantId, job.EnvironmentId, null, db => approvals.SweepAsync(db, job.TenantId, job.EnvironmentId, ct), ct);
}

/// <summary>Delegation timer (ADR 0016): grants a delegate read access when the delegation starts, withdraws it when it ends.</summary>
public sealed class ApprovalDelegationJobHandler(ApprovalService approvals) : IJobHandler
{
    public string Kind => ApprovalService.DelegationJobKind;

    /// <summary>Set by the delegator's own action; the timer acts only on that delegation.</summary>
    public string? RequiredAction => null;

    public Task RunAsync(JobContext job, CancellationToken ct) =>
        job.Database.InTenantAsync(job.TenantId, job.EnvironmentId, null, async db =>
        {
            await approvals.DelegationTimerAsync(db, job.TenantId, job.EnvironmentId, Guid.Parse(job.Payload["delegation"]!.GetValue<string>()), job.Payload["phase"]!.GetValue<string>(), ct);
            return 0;
        }, ct);
}

