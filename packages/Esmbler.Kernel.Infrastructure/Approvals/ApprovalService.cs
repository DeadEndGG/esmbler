using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Esmbler.Kernel.Infrastructure.Approvals;

/// <summary>
/// Approval engine (ADR 0016, REQ-BIZ-004, REQ-AUTHZ-011). Starts approvals for requests whose catalog item names a
/// policy, records human decisions, runs stages in order, expires and escalates them, and drives the request through
/// the binding's system transitions. Every change runs in a tenant transaction that first locks the request row, so
/// decisions, expiry, cancellation and fulfillment on one request serialize.
/// </summary>
public sealed class ApprovalService(Database database, ConfigStore configs, RecordService records, AccessService access, IClock clock, ILogger<ApprovalService> logger)
{
    public const string ExpireJobKind = "approval.expire";
    public const string SweepJobKind = "approval.sweep";
    public const string DelegationJobKind = "approval.delegation";

    private const int MaxNotified = 100;
    private const int MaxCommentLength = 2000;
    private static readonly TimeSpan MaxDelegation = TimeSpan.FromDays(90);
    private const int MaxApprovers = 500;
    private const int SweepBatch = 500;

    public static class Statuses
    {
        public const string Pending = "pending";
        public const string Approved = "approved";
        public const string Rejected = "rejected";
        public const string Cancelled = "cancelled";
        public const string Expired = "expired";
        public const string Skipped = "skipped";
    }

    // ------------------------------------------------------------------ lifecycle (consumer, jobs)

    /// <summary>
    /// Starts the approval of a request in its initial state whose catalog item names a policy, unless one exists. Called
    /// by <see cref="ApprovalConsumer"/> inside the dispatcher's tenant transaction.
    /// </summary>
    internal async Task<bool> StartIfNeededAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, EntitySpec entity, Guid recordId, CancellationToken ct)
    {
        var binding = entity.Request!;
        var approval = binding.Approval!;
        var flow = artifact.Config.WorkflowFor(entity.Id);
        var row = await LockRecordAsync(db, recordId, ct);
        if (row is null || flow is null || row.EntityId != entity.Id)
        {
            return false;
        }

        var data = JsonNode.Parse(row.Data)!.AsObject();
        var state = data[flow.StateField]?.GetValue<string>() ?? flow.InitialState;
        if (state != flow.InitialState
            || artifact.Config.FindCatalogItem(data[binding.CatalogItemField]?.GetValue<string>() ?? string.Empty) is not { ApprovalPolicy: { } policyId } item
            || artifact.Config.FindApprovalPolicy(policyId) is not { } policy
            || await db.Approvals.AnyAsync(a => a.RecordId == recordId, ct))
        {
            return false;
        }

        if (!await records.SystemWriteAsync(db, ctx, artifact, entity, recordId, approval.RequestTransition, [], ct))
        {
            return false;
        }

        var now = clock.UtcNow;
        var row2 = new ApprovalRow
        {
            TenantId = ctx.TenantId,
            EnvironmentId = ctx.EnvironmentId,
            Id = Guid.NewGuid(),
            RecordId = recordId,
            EntityId = entity.Id,
            PolicyId = policy.Id,
            PolicySnapshot = SpecJson.Serialize(policy),
            Status = Statuses.Pending,
            StageIndex = 0,
            Requester = row.CreatedBy,
            Requesters = new JsonArray(Requesters(entity, row.CreatedBy, data).Select(r => (JsonNode)JsonValue.Create(r.ToString())).ToArray()).ToJsonString(),
            CreatedAt = now,
        };
        db.Approvals.Add(row2);
        AuditWriter.Add(db, ctx, "approval.start", entity.Id, recordId.ToString(), artifact.Revision, new { approval = row2.Id, policy = policy.Id, item = item.Id, source = "system" });
        await db.SaveChangesAsync(ct);
        await StartStageAsync(db, ctx, artifact, entity, row2, policy, 0, ct);
        return true;
    }

    /// <summary>Cancels the pending approval of a request that entered a cancelled-category state.</summary>
    internal async Task CancelForRecordAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, EntitySpec entity, Guid recordId, CancellationToken ct)
    {
        if (await LockRecordAsync(db, recordId, ct) is null)
        {
            return;
        }

        var approval = await db.Approvals.AsTracking().FirstOrDefaultAsync(a => a.RecordId == recordId && a.Status == Statuses.Pending, ct);
        if (approval is null)
        {
            return;
        }

        await FinishAsync(db, ctx, artifact, entity, approval, Statuses.Cancelled, "The request was cancelled.", fireTransition: false, ct);
    }

    /// <summary>Expiry timer of a stage: escalates once when the stage says so, otherwise declines the request.</summary>
    internal async Task ExpireAsync(KernelDbContext db, Guid tenantId, Guid environmentId, Guid approvalId, int stageIndex, CancellationToken ct)
    {
        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == approvalId, ct);
        if (approval is null || await LockRecordAsync(db, approval.RecordId, ct) is null)
        {
            return;
        }

        approval = await db.Approvals.AsTracking().FirstAsync(a => a.Id == approvalId, ct);
        if (approval.Status != Statuses.Pending || approval.StageIndex != stageIndex)
        {
            return;
        }

        var artifact = await configs.GetActiveAsync(db, tenantId, environmentId, ct);
        var ctx = SystemContext(tenantId, environmentId, approval.Id);
        if (artifact.Config.FindEntity(approval.EntityId) is not { Request.Approval: not null } entity)
        {
            // The entity no longer takes approvals: close the approval instead of leaving it pending forever.
            approval.Status = Statuses.Cancelled;
            approval.OutcomeReason = "Approvals were removed from this kind of request.";
            approval.DecidedAt = clock.UtcNow;
            foreach (var open in await db.ApprovalSteps.AsTracking().Where(s => s.ApprovalId == approvalId && s.Status == Statuses.Pending).ToListAsync(ct))
            {
                open.Status = Statuses.Cancelled;
                open.DecidedAt = clock.UtcNow;
            }

            AuditWriter.AddSystem(db, tenantId, environmentId, "approval.cancelled", approval.EntityId, approval.RecordId.ToString(), new { approval = approval.Id, reason = approval.OutcomeReason }, outcome: "success");
            await db.SaveChangesAsync(ct);
            return;
        }

        var policy = SpecJson.Deserialize<ApprovalPolicySpec>(approval.PolicySnapshot);
        var stage = policy.Stages[stageIndex];
        var steps = await db.ApprovalSteps.AsTracking().Where(s => s.ApprovalId == approvalId && s.StageIndex == stageIndex).ToListAsync(ct);
        var now = clock.UtcNow;
        if (stage.OnExpiry == ApprovalExpiry.Escalate && !steps.Exists(s => s.Escalation))
        {
            var data = await RecordDataAsync(db, approval.RecordId, ct);
            var escalated = await ResolveAsync(db, artifact.Config, stage.EscalateTo, data, entity, approval, ct);
            if (escalated.Count > 0)
            {
                // Escalation replaces the original approvers: their open steps expire, and the escalation decides.
                foreach (var s in steps.Where(s => s.Status == Statuses.Pending))
                {
                    s.Status = Statuses.Expired;
                    s.DecidedAt = now;
                }

                var due = now.AddHours(stage.ExpiresAfterHours ?? 72);
                foreach (var (kind, reference) in escalated)
                {
                    db.ApprovalSteps.Add(NewStep(approval, stage, stageIndex, kind, reference, due, escalation: true, now));
                }

                AuditWriter.Add(db, ctx, "approval.escalate", entity.Id, approval.RecordId.ToString(), artifact.Revision, new { approval = approval.Id, stage = stage.Id, source = "system" });
                await db.SaveChangesAsync(ct);
                await GrantReadAsync(db, ctx, artifact, entity, approval.RecordId, escalated, ct);
                await ScheduleExpiryAsync(db, approval, stageIndex, due, escalated: true, ct);
                await NotifyAsync(db, artifact.Config, approval, stage, escalated, ct);
                return;
            }
        }

        foreach (var s in steps.Where(s => s.Status == Statuses.Pending))
        {
            s.Status = Statuses.Expired;
            s.DecidedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await FinishAsync(db, ctx, artifact, entity, approval, Statuses.Rejected, $"'{stage.Label}' expired without a decision.", fireTransition: true, ct);
    }

    // ------------------------------------------------------------------ decisions

    /// <summary>
    /// Records a person's decision on an approval step. Only the step's approver or their active delegate may decide;
    /// the requester never decides on their own request; one person decides at most one step per stage; AI agents
    /// cannot decide; rejecting needs a comment.
    /// </summary>
    public async Task<RecordApprovalView> DecideAsync(RequestContext ctx, Guid stepId, string decision, string? comment, CancellationToken ct)
    {
        if (ctx.AgentId is not null)
        {
            throw KernelException.Forbidden("AI agents cannot approve or reject requests.", suggestion: "Ask the approver to decide in Approvals.");
        }

        if (decision is not (Statuses.Approved or Statuses.Rejected))
        {
            throw new KernelException("invalid_decision", "Decide with approved or rejected.", 400, "decision");
        }

        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (decision == Statuses.Rejected && comment is null)
        {
            throw new ValidationFailedException([new FieldError("comment", "required", "Say why you are rejecting, so the requester knows what to change.")]);
        }

        if (comment is { Length: > MaxCommentLength })
        {
            throw new ValidationFailedException([new FieldError("comment", "too_long", $"Keep the comment under {MaxCommentLength} characters.")]);
        }

        var result = await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var peek = await db.ApprovalSteps.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stepId, ct) ?? throw KernelException.NotFound("Approval step");
            if (await LockRecordAsync(db, peek.RecordId, ct) is null)
            {
                throw KernelException.NotFound("Request");
            }

            var step = await db.ApprovalSteps.AsTracking().FirstAsync(s => s.Id == stepId, ct);
            var approval = await db.Approvals.AsTracking().FirstAsync(a => a.Id == step.ApprovalId, ct);
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var entity = artifact.Config.FindEntity(approval.EntityId) ?? throw KernelException.NotFound("Request");
            if (step.Status != Statuses.Pending || approval.Status != Statuses.Pending || approval.StageIndex != step.StageIndex)
            {
                throw KernelException.Conflict("approval_closed", "This approval step has already been decided or is no longer open.", new { status = step.Status }, "Reload to see the current state.");
            }

            var data = await RecordDataAsync(db, approval.RecordId, ct);
            var eligibility = await EligibilityAsync(db, ctx, artifact.Config, entity, approval, step, data, ct);
            if (eligibility.Denied is { } reason)
            {
                // Denials are audited: the transaction commits with the audit row and the refusal is raised after it.
                AuditWriter.Add(db, ctx, "approval.decide", entity.Id, approval.RecordId.ToString(), artifact.Revision, new { step = step.Id, decision }, outcome: "denied");
                await db.SaveChangesAsync(ct);
                return new Decision(null, reason);
            }

            var now = clock.UtcNow;
            step.Status = decision;
            step.DecidedBy = ctx.PrincipalId;
            step.OnBehalfOf = eligibility.OnBehalfOf;
            step.Comment = comment;
            step.DecidedAt = now;
            AuditWriter.Add(db, ctx, "approval.decide", entity.Id, approval.RecordId.ToString(), artifact.Revision, new { approval = approval.Id, step = step.Id, stage = step.StageId, decision, onBehalfOf = eligibility.OnBehalfOf });
            await db.SaveChangesAsync(ct);

            var policy = SpecJson.Deserialize<ApprovalPolicySpec>(approval.PolicySnapshot);
            var stage = policy.Stages[step.StageIndex];
            var steps = await db.ApprovalSteps.AsTracking().Where(s => s.ApprovalId == approval.Id && s.StageIndex == step.StageIndex).ToListAsync(ct);
            var outcome = StageOutcome(stage, steps);
            if (outcome == Statuses.Approved)
            {
                foreach (var s in steps.Where(s => s.Status == Statuses.Pending))
                {
                    s.Status = Statuses.Skipped;
                    s.DecidedAt = now;
                }

                await db.SaveChangesAsync(ct);
                await JobScheduler.CancelAsync(db, ExpiryKey(approval.Id, step.StageIndex), ct);
                await StartStageAsync(db, ctx, artifact, entity, approval, policy, step.StageIndex + 1, ct);
            }
            else if (outcome == Statuses.Rejected)
            {
                await FinishAsync(db, ctx, artifact, entity, approval, Statuses.Rejected, $"Rejected at '{stage.Label}'.", fireTransition: true, ct);
            }

            return new Decision(await ViewAsync(db, ctx, artifact.Config, entity, approval, ct), null);
        }, ct);

        return result.View ?? throw KernelException.Forbidden(result.Denied!);
    }

    private sealed record Decision(RecordApprovalView? View, string? Denied);

    /// <summary>
    /// <c>all</c>: every step approves, any rejection rejects. <c>any</c>: the first approval passes, all rejecting
    /// rejects. An escalated stage is decided by its escalation steps alone: the first decision among them decides,
    /// approve or reject (approvals recorded before the escalation do not count; ASM-047).
    /// </summary>
    internal static string StageOutcome(ApprovalStageSpec stage, IReadOnlyList<ApprovalStepRow> steps)
    {
        var escalation = steps.Where(s => s.Escalation).ToList();
        if (escalation.Count > 0)
        {
            return escalation.Exists(s => s.Status == Statuses.Rejected) ? Statuses.Rejected
                : escalation.Exists(s => s.Status == Statuses.Approved) ? Statuses.Approved
                : escalation.Exists(s => s.Status == Statuses.Pending) ? Statuses.Pending
                : Statuses.Rejected;
        }

        var anyMode = stage.Mode == ApprovalModes.Any;
        var approved = steps.Count(s => s.Status == Statuses.Approved);
        var rejected = steps.Count(s => s.Status == Statuses.Rejected);
        var open = steps.Count(s => s.Status == Statuses.Pending);
        if (anyMode)
        {
            return approved > 0 ? Statuses.Approved : open == 0 ? Statuses.Rejected : Statuses.Pending;
        }

        return rejected > 0 ? Statuses.Rejected : open == 0 && approved > 0 ? Statuses.Approved : open == 0 ? Statuses.Rejected : Statuses.Pending;
    }

    private sealed record Eligibility(string? Denied, Guid? OnBehalfOf);

    private async Task<Eligibility> EligibilityAsync(KernelDbContext db, RequestContext ctx, EffectiveConfig config, EntitySpec entity, ApprovalRow approval, ApprovalStepRow step, JsonObject data, CancellationToken ct)
    {
        var requesters = Requesters(entity, approval, data);
        if (requesters.Contains(ctx.PrincipalId))
        {
            return new Eligibility("You cannot approve or reject a request you made or that is for you.", null);
        }

        // One person's authority counts once per stage, whether they decided themselves or through their delegate.
        var stageSteps = await db.ApprovalSteps.AsNoTracking().Where(s => s.ApprovalId == approval.Id && s.StageIndex == step.StageIndex && s.Id != step.Id && s.DecidedBy != null).ToListAsync(ct);
        if (stageSteps.Exists(s => s.DecidedBy == ctx.PrincipalId || s.OnBehalfOf == ctx.PrincipalId))
        {
            return new Eligibility("You have already decided on this stage; another approver must decide this step.", null);
        }

        var mine = await access.ForAsync(db, ctx, config, ct, fresh: true);
        if (Matches(step, mine.Facts))
        {
            return new Eligibility(null, null);
        }

        foreach (var delegator in await ActiveDelegatorsAsync(db, ctx.PrincipalId, ct))
        {
            if (requesters.Contains(delegator))
            {
                continue;
            }

            var theirs = await access.ForAsync(db, ctx with { PrincipalId = delegator }, config, ct, fresh: true);
            if (Matches(step, theirs.Facts) && !stageSteps.Exists(s => s.DecidedBy == delegator || s.OnBehalfOf == delegator))
            {
                return new Eligibility(null, delegator);
            }
        }

        return new Eligibility("You are not an approver of this step.", null);
    }

    private static bool Matches(ApprovalStepRow step, AuthorizationFacts facts) => step.ApproverKind switch
    {
        ApproverKinds.User => Guid.TryParse(step.ApproverRef, out var u) && u == facts.PrincipalId,
        ApproverKinds.Group => Guid.TryParse(step.ApproverRef, out var g) && facts.Groups.Contains(g),
        ApproverKinds.Role => facts.Roles.ContainsKey(step.ApproverRef),
        _ => false,
    };

    /// <summary>
    /// Who never decides (separation of duties): the snapshot taken when the approval started plus, as the record is
    /// now, its creator and the binding's requester fields (and any access field with relation "requester").
    /// </summary>
    private static HashSet<Guid> Requesters(EntitySpec entity, ApprovalRow approval, JsonObject data)
    {
        var result = Requesters(entity, approval.Requester, data);
        foreach (var v in (JsonNode.Parse(approval.Requesters) as JsonArray ?? []).OfType<JsonValue>())
        {
            if (v.TryGetValue<string>(out var raw) && Guid.TryParse(raw, out var id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static HashSet<Guid> Requesters(EntitySpec entity, Guid? creator, JsonObject data)
    {
        var result = new HashSet<Guid>();
        if (creator is { } c)
        {
            result.Add(c);
        }

        var fields = (entity.Request?.Approval?.RequesterFields ?? []).Concat(entity.AccessFields.Where(a => a.Relation == "requester").Select(a => a.Field)).Distinct();
        foreach (var field in fields)
        {
            if (data[field] is JsonValue v && v.TryGetValue<string>(out var raw) && Guid.TryParse(raw, out var id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private async Task<List<Guid>> ActiveDelegatorsAsync(KernelDbContext db, Guid @delegate, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await db.ApprovalDelegations.AsNoTracking()
            .Where(d => d.Delegate == @delegate && d.RevokedAt == null && d.StartsAt <= now && d.EndsAt > now)
            .Select(d => d.Delegator).Distinct().ToListAsync(ct);
    }

    // ------------------------------------------------------------------ stages

    private async Task StartStageAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, EntitySpec entity, ApprovalRow approval, ApprovalPolicySpec policy, int from, CancellationToken ct)
    {
        var data = await RecordDataAsync(db, approval.RecordId, ct);
        var item = artifact.Config.FindCatalogItem(data[entity.Request!.CatalogItemField]?.GetValue<string>() ?? string.Empty);
        for (var i = from; i < policy.Stages.Count; i++)
        {
            var stage = policy.Stages[i];
            if (!Applies(stage, entity, item, data))
            {
                continue;
            }

            var approvers = await ResolveAsync(db, artifact.Config, stage.Approvers, data, entity, approval, ct);
            var escalation = false;
            if (approvers.Count == 0)
            {
                approvers = await ResolveAsync(db, artifact.Config, stage.EscalateTo, data, entity, approval, ct);
                escalation = true;
            }

            if (approvers.Count == 0)
            {
                await FinishAsync(db, ctx, artifact, entity, approval, Statuses.Rejected, $"Nobody in this workspace can approve '{stage.Label}'.", fireTransition: true, ct);
                return;
            }

            var now = clock.UtcNow;
            DateTimeOffset? due = stage.ExpiresAfterHours is { } hours ? now.AddHours(hours) : null;
            approval.StageIndex = i;
            foreach (var (kind, reference) in approvers)
            {
                db.ApprovalSteps.Add(NewStep(approval, stage, i, kind, reference, due, escalation, now));
            }

            await db.SaveChangesAsync(ct);
            await GrantReadAsync(db, ctx, artifact, entity, approval.RecordId, approvers, ct);
            if (due is { } d)
            {
                await ScheduleExpiryAsync(db, approval, i, d, escalated: false, ct);
            }

            await NotifyAsync(db, artifact.Config, approval, stage, approvers, ct);
            EventWriter.AddPlatform(db, ctx, EventTypes.ApprovalRequested, "platform.approvals", entity.Id, approval.RecordId, await RevisionAsync(db, approval.RecordId, ct), artifact.Revision, new JsonObject { ["policy"] = approval.PolicyId, ["stage"] = stage.Id }, EventSeverities.Low);
            await db.SaveChangesAsync(ct);
            return;
        }

        approval.StageIndex = policy.Stages.Count;
        await FinishAsync(db, ctx, artifact, entity, approval, Statuses.Approved, null, fireTransition: true, ct);
    }

    private bool Applies(ApprovalStageSpec stage, EntitySpec entity, CatalogItemSpec? item, JsonObject data)
    {
        if (string.IsNullOrWhiteSpace(stage.When))
        {
            return true;
        }

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["record"] = Values.MapFromJson(data),
            ["now"] = Kernel.Records.FieldNormalizer.FormatInstant(clock.UtcNow),
            ["variables"] = Values.MapFromJson(data[entity.Request!.VariablesField] as JsonObject),
        };
        try
        {
            return Expression.Compile(stage.When, EntitySchema.ForRequest(entity, item ?? new CatalogItemSpec { Id = "none", Label = "none", RequestEntity = entity.Id })).EvaluateBool(vars);
        }
        catch (ExpressionException ex)
        {
            // Fail closed: a stage whose condition cannot be evaluated still runs, so no approval is skipped by mistake.
            logger.LogWarning("Condition of approval stage {Stage} failed ({Message}); the stage runs", stage.Id, ex.Message);
            return true;
        }
    }

    /// <summary>Resolves approver specs to step targets (user, group or role), dropping the requester and duplicates.</summary>
    private static async Task<List<(string Kind, string Ref)>> ResolveAsync(KernelDbContext db, EffectiveConfig config, IEnumerable<ApproverSpec> specs, JsonObject data, EntitySpec entity, ApprovalRow approval, CancellationToken ct)
    {
        var requesters = Requesters(entity, approval, data);
        var result = new List<(string Kind, string Ref)>();
        foreach (var spec in specs)
        {
            (string, string)? target = null;
            switch (spec.Kind)
            {
                case ApproverKinds.Group:
                    var groupId = await db.Groups.AsNoTracking().Where(g => g.Key == spec.Ref).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(ct);
                    target = groupId is { } gid ? (ApproverKinds.Group, gid.ToString()) : null;
                    break;
                case ApproverKinds.Role:
                    target = config.Roles.Exists(r => r.Id == spec.Ref) ? (ApproverKinds.Role, spec.Ref) : null;
                    break;
                case ApproverKinds.Field:
                    var parts = spec.Ref.Split('.', 2);
                    var (source, key, type) = parts switch
                    {
                        ["record", var k] => (data, k, entity.FindField(k)?.Type),
                        ["variables", var k] => (data[entity.Request!.VariablesField] as JsonObject ?? [], k, (string?)null),
                        _ => ((JsonObject?)null, string.Empty, (string?)null),
                    };
                    if (source?[key] is JsonValue v && v.TryGetValue<string>(out var raw) && Guid.TryParse(raw, out var id))
                    {
                        var isGroup = type == FieldTypes.Group || (type is null && await db.Groups.AnyAsync(g => g.Id == id, ct));
                        target = isGroup
                            ? (await db.Groups.AnyAsync(g => g.Id == id, ct) ? (ApproverKinds.Group, id.ToString()) : null)
                            : requesters.Contains(id) || !await db.Memberships.AnyAsync(m => m.PrincipalId == id, ct) ? null : (ApproverKinds.User, id.ToString());
                    }

                    break;
            }

            if (target is { } t && !result.Contains(t))
            {
                result.Add(t);
            }
        }

        return result;
    }

    private static ApprovalStepRow NewStep(ApprovalRow approval, ApprovalStageSpec stage, int index, string kind, string reference, DateTimeOffset? due, bool escalation, DateTimeOffset now) => new()
    {
        TenantId = approval.TenantId,
        EnvironmentId = approval.EnvironmentId,
        Id = Guid.NewGuid(),
        ApprovalId = approval.Id,
        RecordId = approval.RecordId,
        StageIndex = index,
        StageId = stage.Id,
        ApproverKind = kind,
        ApproverRef = reference,
        Status = Statuses.Pending,
        DueAt = due,
        Escalation = escalation,
        CreatedAt = now,
    };

    private async Task FinishAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, EntitySpec entity, ApprovalRow approval, string status, string? reason, bool fireTransition, CancellationToken ct)
    {
        var now = clock.UtcNow;
        approval.Status = status;
        approval.OutcomeReason = reason;
        approval.DecidedAt = now;
        var open = await db.ApprovalSteps.AsTracking().Where(s => s.ApprovalId == approval.Id && s.Status == Statuses.Pending).ToListAsync(ct);
        foreach (var s in open)
        {
            s.Status = status == Statuses.Approved ? Statuses.Skipped : Statuses.Cancelled;
            s.DecidedAt = now;
        }

        await JobScheduler.CancelAsync(db, ExpiryKey(approval.Id, approval.StageIndex), ct);
        await JobScheduler.CancelAsync(db, ExpiryKey(approval.Id, approval.StageIndex) + ":escalated", ct);
        AuditWriter.Add(db, ctx, $"approval.{status}", entity.Id, approval.RecordId.ToString(), artifact.Revision, new { approval = approval.Id, policy = approval.PolicyId, reason, source = "system" });
        var (eventType, severity) = status switch
        {
            Statuses.Approved => (EventTypes.ApprovalApproved, EventSeverities.Info),
            Statuses.Rejected => (EventTypes.ApprovalRejected, EventSeverities.Medium),
            _ => (EventTypes.ApprovalCancelled, EventSeverities.Info),
        };
        var payload = new JsonObject { ["policy"] = approval.PolicyId };
        if (status == Statuses.Rejected && reason is not null)
        {
            payload["reason"] = reason;
        }

        EventWriter.AddPlatform(db, ctx, eventType, "platform.approvals", entity.Id, approval.RecordId, await RevisionAsync(db, approval.RecordId, ct), artifact.Revision, payload, severity);
        await db.SaveChangesAsync(ct);
        if (fireTransition)
        {
            var binding = entity.Request!.Approval!;
            var transition = status == Statuses.Approved ? binding.ApprovedTransition : binding.DeclinedTransition;
            if (!await records.SystemWriteAsync(db, ctx, artifact, entity, approval.RecordId, transition, [], ct))
            {
                logger.LogWarning("Approval {Approval} finished {Status} but transition {Transition} is not available on the request", approval.Id, status, transition);
            }
        }
    }

    private static string ExpiryKey(Guid approvalId, int stageIndex) => $"{ExpireJobKind}:{approvalId}:{stageIndex}";

    private static Task ScheduleExpiryAsync(KernelDbContext db, ApprovalRow approval, int stageIndex, DateTimeOffset due, bool escalated, CancellationToken ct) =>
        JobScheduler.ScheduleAsync(db, approval.TenantId, approval.EnvironmentId, new JobRequest(ExpireJobKind, new JsonObject { ["approval"] = approval.Id.ToString(), ["stage"] = stageIndex })
        {
            RunAt = due,
            DedupKey = ExpiryKey(approval.Id, stageIndex) + (escalated ? ":escalated" : string.Empty),
            ReplaceExisting = true,
        }, ct);

    // ------------------------------------------------------------------ approver read access and notifications

    /// <summary>Adds the approvers (and the active delegates of the people among them) to the request's approvers field.</summary>
    private async Task GrantReadAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, EntitySpec entity, Guid recordId, IReadOnlyList<(string Kind, string Ref)> approvers, CancellationToken ct)
    {
        var principals = new List<Guid>();
        var people = await PeopleAsync(db, approvers, ct);
        foreach (var (kind, reference) in approvers)
        {
            if (kind is ApproverKinds.User or ApproverKinds.Group && Guid.TryParse(reference, out var id))
            {
                principals.Add(id);
            }
            else if (kind == ApproverKinds.Role)
            {
                principals.AddRange(await db.RoleAssignments.AsNoTracking().Where(r => r.RoleId == reference).Select(r => r.PrincipalId ?? r.GroupId!.Value).ToListAsync(ct));
            }
        }

        var now = clock.UtcNow;
        principals.AddRange(await db.ApprovalDelegations.AsNoTracking().Where(d => people.Contains(d.Delegator) && d.RevokedAt == null && d.StartsAt <= now && d.EndsAt > now).Select(d => d.Delegate).ToListAsync(ct));
        await AddApproversAsync(db, ctx, artifact, entity, recordId, principals, ct);
    }

    private async Task AddApproversAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, EntitySpec entity, Guid recordId, IEnumerable<Guid> principals, CancellationToken ct)
    {
        var field = entity.Request!.Approval!.ApproversField;
        var data = await RecordDataAsync(db, recordId, ct);
        var current = (data[field] as JsonArray ?? []).OfType<JsonValue>().Select(v => v.GetValue<string>()).ToList();
        var all = current.Concat(principals.Select(p => p.ToString())).Distinct().ToList();
        if (all.Count > MaxApprovers)
        {
            logger.LogWarning("Request {Record} has {Count} approvers; only the first {Max} can open it", recordId, all.Count, MaxApprovers);
        }

        var merged = all.Take(MaxApprovers).ToList();
        if (merged.Count == current.Count)
        {
            return;
        }

        await records.SystemWriteAsync(db, ctx, artifact, entity, recordId, null, new JsonObject { [field] = new JsonArray(merged.Select(m => (JsonNode)JsonValue.Create(m)).ToArray()) }, ct);
    }

    /// <summary>People behind approver targets: users, members of groups, holders of roles (directly or through groups).</summary>
    private static async Task<HashSet<Guid>> PeopleAsync(KernelDbContext db, IEnumerable<(string Kind, string Ref)> approvers, CancellationToken ct)
    {
        var people = new HashSet<Guid>();
        var groups = new HashSet<Guid>();
        foreach (var (kind, reference) in approvers)
        {
            if (kind == ApproverKinds.User && Guid.TryParse(reference, out var u))
            {
                people.Add(u);
            }
            else if (kind == ApproverKinds.Group && Guid.TryParse(reference, out var g))
            {
                groups.Add(g);
            }
            else if (kind == ApproverKinds.Role)
            {
                foreach (var a in await db.RoleAssignments.AsNoTracking().Where(r => r.RoleId == reference).ToListAsync(ct))
                {
                    if (a.PrincipalId is { } p)
                    {
                        people.Add(p);
                    }
                    else if (a.GroupId is { } gid)
                    {
                        groups.Add(gid);
                    }
                }
            }
        }

        if (groups.Count > 0)
        {
            people.UnionWith(await db.GroupMembers.AsNoTracking().Where(m => groups.Contains(m.GroupId) && m.PrincipalId != null).Select(m => m.PrincipalId!.Value).ToListAsync(ct));
        }

        return people;
    }

    private async Task NotifyAsync(KernelDbContext db, EffectiveConfig config, ApprovalRow approval, ApprovalStageSpec stage, IReadOnlyList<(string Kind, string Ref)> approvers, CancellationToken ct)
    {
        var data = await RecordDataAsync(db, approval.RecordId, ct);
        var entity = config.FindEntity(approval.EntityId);
        // The subject names the catalog item, not the request title: not everyone notified may read the request.
        var title = entity?.Request is { } binding && config.FindCatalogItem(data[binding.CatalogItemField]?.GetValue<string>() ?? string.Empty) is { } catalogItem ? catalogItem.Label : "a request";
        var requesters = entity is null ? [] : Requesters(entity, approval, data);
        var people = await PeopleAsync(db, approvers, ct);
        var eventId = Guid.NewGuid();
        foreach (var person in people.Where(p => !requesters.Contains(p)).Take(MaxNotified))
        {
            db.Notifications.Add(new NotificationRow
            {
                TenantId = approval.TenantId,
                EnvironmentId = approval.EnvironmentId,
                Id = Guid.NewGuid(),
                TemplateId = "approval.requested",
                Recipient = person,
                Subject = $"Approval needed: {Truncate(title, 120)}",
                Body = $"{stage.Label}: a request is waiting for your decision. Open Approvals to approve or reject it.",
                EventId = eventId,
                CreatedAt = clock.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ------------------------------------------------------------------ queries

    /// <summary>Approvals of one record, newest first, for the approvals panel. Requires read access to the record.</summary>
    public Task<IReadOnlyList<RecordApprovalView>> ForRecordAsync(RequestContext ctx, string entityId, Guid recordId, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var entity = artifact.Config.FindEntity(entityId) ?? throw KernelException.NotFound($"Entity '{entityId}'");
            var row = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId && r.EntityId == entityId && r.DeletedAt == null, ct) ?? throw KernelException.NotFound("Record");
            var perms = await access.ForAsync(db, ctx, artifact.Config, ct);
            if (!perms.CanRead(entity, JsonNode.Parse(row.Data)!.AsObject()))
            {
                throw KernelException.NotFound("Record");
            }

            var approvals = await db.Approvals.AsNoTracking().Where(a => a.RecordId == recordId).OrderByDescending(a => a.CreatedAt).Take(10).ToListAsync(ct);
            var result = new List<RecordApprovalView>();
            foreach (var a in approvals)
            {
                result.Add(await ViewAsync(db, ctx, artifact.Config, entity, a, ct));
            }

            return (IReadOnlyList<RecordApprovalView>)result;
        }, ct);

    /// <summary>Steps waiting for the caller (directly or as a delegate) plus their recent decisions.</summary>
    public async Task<ApprovalInbox> InboxAsync(RequestContext ctx, CancellationToken ct)
    {
        var (pending, decided) = await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var waiting = await WaitingForAsync(db, ctx, artifact.Config, ct);
            var mine = await db.ApprovalSteps.AsNoTracking().Where(s => s.DecidedBy == ctx.PrincipalId).OrderByDescending(s => s.DecidedAt).Take(20).ToListAsync(ct);
            return (waiting, mine);
        }, ct);

        var items = new List<ApprovalInboxItem>();
        foreach (var (step, onBehalfOf) in pending.Concat(decided.Select(s => (s, s.OnBehalfOf))))
        {
            items.Add(await InboxItemAsync(ctx, step, onBehalfOf, ct));
        }

        return new ApprovalInbox(items.Where(i => i.Status == Statuses.Pending).ToList(), items.Where(i => i.Status != Statuses.Pending).ToList());
    }

    /// <summary>Number of steps waiting for the caller (navigation badge).</summary>
    public Task<int> PendingCountAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            return (await WaitingForAsync(db, ctx, artifact.Config, ct)).Count;
        }, ct);

    private async Task<List<(ApprovalStepRow Step, Guid? OnBehalfOf)>> WaitingForAsync(KernelDbContext db, RequestContext ctx, EffectiveConfig config, CancellationToken ct)
    {
        var result = new List<(ApprovalStepRow, Guid?)>();
        var seen = new HashSet<Guid>();
        var who = new List<(Guid Principal, Guid? OnBehalfOf)> { (ctx.PrincipalId, null) };
        who.AddRange((await ActiveDelegatorsAsync(db, ctx.PrincipalId, ct)).Select(d => (d, (Guid?)d)));
        foreach (var (principal, onBehalfOf) in who)
        {
            var facts = (await access.ForAsync(db, ctx with { PrincipalId = principal }, config, ct)).Facts;
            var groups = facts.Groups.Select(g => g.ToString()).ToList();
            var roles = facts.Roles.Keys.ToList();
            var me = principal.ToString();
            var steps = await db.ApprovalSteps.AsNoTracking()
                .Where(s => s.Status == Statuses.Pending && ((s.ApproverKind == ApproverKinds.User && s.ApproverRef == me) || (s.ApproverKind == ApproverKinds.Group && groups.Contains(s.ApproverRef)) || (s.ApproverKind == ApproverKinds.Role && roles.Contains(s.ApproverRef))))
                .OrderBy(s => s.CreatedAt).Take(100).ToListAsync(ct);
            foreach (var step in steps)
            {
                var approval = await db.Approvals.AsNoTracking().FirstAsync(a => a.Id == step.ApprovalId, ct);
                var entity = config.FindEntity(approval.EntityId);
                var data = await RecordDataAsync(db, approval.RecordId, ct);
                var requesters = entity is null ? [] : Requesters(entity, approval, data);
                if (approval.Status != Statuses.Pending || approval.StageIndex != step.StageIndex || requesters.Contains(ctx.PrincipalId) || requesters.Contains(principal) || !seen.Add(step.Id))
                {
                    continue;
                }

                var decidedHere = await db.ApprovalSteps.AnyAsync(s => s.ApprovalId == approval.Id && s.StageIndex == step.StageIndex && (s.DecidedBy == ctx.PrincipalId || s.OnBehalfOf == ctx.PrincipalId || (onBehalfOf != null && (s.OnBehalfOf == onBehalfOf || s.DecidedBy == onBehalfOf))), ct);
                if (!decidedHere)
                {
                    result.Add((step, onBehalfOf));
                }
            }
        }

        return result;
    }

    private async Task<ApprovalInboxItem> InboxItemAsync(RequestContext ctx, ApprovalStepRow step, Guid? onBehalfOf, CancellationToken ct)
    {
        var (approval, entityId, stageLabel, policyLabel, names) = await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var a = await db.Approvals.AsNoTracking().FirstAsync(x => x.Id == step.ApprovalId, ct);
            var policy = SpecJson.Deserialize<ApprovalPolicySpec>(a.PolicySnapshot);
            var ids = new[] { a.Requester, onBehalfOf }.OfType<Guid>().ToList();
            var n = await db.Principals.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
            return (a, a.EntityId, policy.FindStage(step.StageId)?.Label ?? step.StageId, policy.Label, n);
        }, ct);

        RecordView? record = null;
        try
        {
            record = await records.GetAsync(ctx, entityId, approval.RecordId, ct);
        }
        catch (KernelException)
        {
            // The approver cannot read this request (for example a role holder added after the stage started); the
            // inbox still lists the step without its details.
        }

        return new ApprovalInboxItem(
            step.Id,
            approval.Id,
            entityId,
            approval.RecordId,
            policyLabel,
            stageLabel,
            step.Status,
            step.DueAt,
            step.Escalation,
            record is not null && approval.Requester is { } r ? names.GetValueOrDefault(r) : null,
            onBehalfOf is { } o ? names.GetValueOrDefault(o) : null,
            step.Comment,
            step.DecidedAt,
            record);
    }

    private async Task<RecordApprovalView> ViewAsync(KernelDbContext db, RequestContext ctx, EffectiveConfig config, EntitySpec entity, ApprovalRow approval, CancellationToken ct)
    {
        var policy = SpecJson.Deserialize<ApprovalPolicySpec>(approval.PolicySnapshot);
        var steps = await db.ApprovalSteps.AsNoTracking().Where(s => s.ApprovalId == approval.Id).OrderBy(s => s.StageIndex).ThenBy(s => s.Escalation).ThenBy(s => s.CreatedAt).ToListAsync(ct);
        var people = steps.SelectMany(s => new[] { s.DecidedBy, s.OnBehalfOf, s.ApproverKind == ApproverKinds.User && Guid.TryParse(s.ApproverRef, out var u) ? u : (Guid?)null }).OfType<Guid>().Distinct().ToList();
        var names = await db.Principals.AsNoTracking().Where(p => people.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        var groupIds = steps.Where(s => s.ApproverKind == ApproverKinds.Group).Select(s => Guid.TryParse(s.ApproverRef, out var g) ? g : Guid.Empty).Distinct().ToList();
        var groupNames = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        var data = await RecordDataAsync(db, approval.RecordId, ct);

        var canDecide = new HashSet<Guid>();
        if (approval.Status == Statuses.Pending && ctx.AgentId is null)
        {
            foreach (var step in steps.Where(s => s.Status == Statuses.Pending && s.StageIndex == approval.StageIndex))
            {
                if ((await EligibilityAsync(db, ctx, config, entity, approval, step, data, ct)).Denied is null)
                {
                    canDecide.Add(step.Id);
                }
            }
        }

        string Label(ApprovalStepRow s) => s.ApproverKind switch
        {
            ApproverKinds.User => Guid.TryParse(s.ApproverRef, out var u) ? names.GetValueOrDefault(u, "Someone") : "Someone",
            ApproverKinds.Group => Guid.TryParse(s.ApproverRef, out var g) ? groupNames.GetValueOrDefault(g, "A group") : "A group",
            _ => config.Roles.Find(r => r.Id == s.ApproverRef)?.Label is { } role ? $"Any {role.ToLowerInvariant()}" : s.ApproverRef,
        };

        var stages = new List<ApprovalStageView>();
        for (var i = 0; i < policy.Stages.Count; i++)
        {
            var stage = policy.Stages[i];
            var stageSteps = steps.Where(s => s.StageIndex == i).ToList();
            var status = stageSteps.Count == 0
                ? (i < approval.StageIndex || approval.Status == Statuses.Approved ? Statuses.Skipped : approval.Status == Statuses.Pending ? "waiting" : Statuses.Cancelled)
                : i == approval.StageIndex && approval.Status == Statuses.Pending ? Statuses.Pending : StageOutcome(stage, stageSteps) is var o && o != Statuses.Pending ? o : approval.Status;
            stages.Add(new ApprovalStageView(stage.Id, stage.Label, stage.Mode, status, stageSteps.Select(s => new ApprovalStepView(
                s.Id,
                s.ApproverKind,
                Label(s),
                s.Status,
                s.DecidedBy is { } d ? names.GetValueOrDefault(d) : null,
                s.OnBehalfOf is { } b ? names.GetValueOrDefault(b) : null,
                s.Comment,
                s.DecidedAt,
                s.DueAt,
                s.Escalation,
                canDecide.Contains(s.Id))).ToList()));
        }

        return new RecordApprovalView(approval.Id, approval.PolicyId, policy.Label, approval.Status, approval.StageIndex, approval.OutcomeReason, approval.CreatedAt, approval.DecidedAt, stages);
    }

    // ------------------------------------------------------------------ delegations

    /// <summary>The caller's delegations (given and received); with <paramref name="all"/>, every current delegation (permission administrators).</summary>
    public Task<IReadOnlyList<DelegationView>> DelegationsAsync(RequestContext ctx, CancellationToken ct, bool all = false) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            if (all)
            {
                var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
                if (!(await access.ForAsync(db, ctx, artifact.Config, ct, fresh: true)).Can(PlatformActions.PermissionsAdmin))
                {
                    throw KernelException.Forbidden("Only permission administrators can review everyone's delegations.");
                }
            }

            var now = clock.UtcNow;
            var rows = await db.ApprovalDelegations.AsNoTracking()
                .Where(d => (all || d.Delegator == ctx.PrincipalId || d.Delegate == ctx.PrincipalId) && d.RevokedAt == null && d.EndsAt > now)
                .OrderBy(d => d.StartsAt).Take(500).ToListAsync(ct);
            var ids = rows.SelectMany(r => new[] { r.Delegator, r.Delegate }).Distinct().ToList();
            var names = await db.Principals.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
            return (IReadOnlyList<DelegationView>)rows.Select(r => new DelegationView(r.Id, r.Delegator, names.GetValueOrDefault(r.Delegator, ""), r.Delegate, names.GetValueOrDefault(r.Delegate, ""), r.StartsAt, r.EndsAt, r.StartsAt <= now)).ToList();
        }, ct);

    /// <summary>
    /// Delegates the caller's approvals to another member of the workspace for a period of up to 90 days that starts
    /// within 90 days (out of office). The delegate can read the requests waiting for the caller only while the
    /// delegation is active: access is granted when it starts and withdrawn when it ends or is revoked, unless the
    /// delegate decided on the request or approves it in their own right.
    /// </summary>
    public async Task<DelegationView> DelegateAsync(RequestContext ctx, Guid @delegate, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct)
    {
        if (ctx.AgentId is not null)
        {
            throw KernelException.Forbidden("AI agents cannot delegate approvals.");
        }

        var now = clock.UtcNow;
        if (@delegate == ctx.PrincipalId)
        {
            throw new ValidationFailedException([new FieldError("delegate", "invalid", "Choose someone other than yourself.")]);
        }

        if (endsAt <= startsAt || endsAt <= now || startsAt < now.AddMinutes(-5) || startsAt > now.Add(MaxDelegation) || endsAt - startsAt > MaxDelegation)
        {
            throw new ValidationFailedException([new FieldError("endsAt", "invalid", "The delegation must end after it starts, start within 90 days, and last at most 90 days.")]);
        }

        var id = await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            if (!await db.Memberships.AnyAsync(m => m.PrincipalId == @delegate, ct) || !await db.Principals.AnyAsync(p => p.Id == @delegate && p.Kind == "user" && !p.Disabled, ct))
            {
                throw new ValidationFailedException([new FieldError("delegate", "unknown_user", "That person is not a member of this workspace.")]);
            }

            if (await db.ApprovalDelegations.AnyAsync(d => d.Delegator == ctx.PrincipalId && d.RevokedAt == null && d.StartsAt < endsAt && d.EndsAt > startsAt, ct))
            {
                throw KernelException.Conflict("delegation_overlaps", "You already delegate your approvals during part of this period.", null, "End the current delegation first.");
            }

            var row = new ApprovalDelegationRow
            {
                TenantId = ctx.TenantId,
                EnvironmentId = ctx.EnvironmentId,
                Id = Guid.NewGuid(),
                Delegator = ctx.PrincipalId,
                Delegate = @delegate,
                StartsAt = startsAt,
                EndsAt = endsAt,
                CreatedBy = ctx.PrincipalId,
                CreatedAt = now,
            };
            db.ApprovalDelegations.Add(row);
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            AuditWriter.Add(db, ctx, "approval.delegate", "approval_delegation", row.Id.ToString(), artifact.Revision, new { @delegate, startsAt, endsAt });
            await db.SaveChangesAsync(ct);
            if (startsAt <= now)
            {
                await GrantDelegateAsync(db, ctx, artifact, row, ct);
            }
            else
            {
                await ScheduleDelegationAsync(db, row, "start", startsAt, ct);
            }

            await ScheduleDelegationAsync(db, row, "end", endsAt, ct);
            return row.Id;
        }, ct);

        return (await DelegationsAsync(ctx, ct)).First(d => d.Id == id);
    }

    public Task RevokeDelegationAsync(RequestContext ctx, Guid id, CancellationToken ct)
    {
        if (ctx.AgentId is not null)
        {
            throw KernelException.Forbidden("AI agents cannot change delegations.");
        }

        return database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var row = await db.ApprovalDelegations.AsTracking().FirstOrDefaultAsync(d => d.Id == id && d.Delegator == ctx.PrincipalId && d.RevokedAt == null, ct) ?? throw KernelException.NotFound("Delegation");
            row.RevokedAt = clock.UtcNow;
            AuditWriter.Add(db, ctx, "approval.delegate.revoke", "approval_delegation", id.ToString(), null);
            await db.SaveChangesAsync(ct);
            await JobScheduler.CancelAsync(db, $"{DelegationJobKind}:{id}:start", ct);
            await JobScheduler.CancelAsync(db, $"{DelegationJobKind}:{id}:end", ct);
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            await WithdrawDelegateAsync(db, SystemContext(ctx.TenantId, ctx.EnvironmentId, id) with { PrincipalId = ctx.PrincipalId }, artifact, row, ct);
            return 0;
        }, ct);
    }

    /// <summary>Delegation timer: grants read access when a delegation starts and withdraws it when it ends.</summary>
    internal async Task DelegationTimerAsync(KernelDbContext db, Guid tenantId, Guid environmentId, Guid delegationId, string phase, CancellationToken ct)
    {
        var row = await db.ApprovalDelegations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == delegationId, ct);
        if (row is null || row.RevokedAt is not null)
        {
            return;
        }

        var artifact = await configs.GetActiveAsync(db, tenantId, environmentId, ct);
        var ctx = SystemContext(tenantId, environmentId, delegationId) with { PrincipalId = row.Delegator };
        if (phase == "start")
        {
            await GrantDelegateAsync(db, ctx, artifact, row, ct);
        }
        else
        {
            await WithdrawDelegateAsync(db, ctx, artifact, row, ct);
        }
    }

    private static Task ScheduleDelegationAsync(KernelDbContext db, ApprovalDelegationRow row, string phase, DateTimeOffset at, CancellationToken ct) =>
        JobScheduler.ScheduleAsync(db, row.TenantId, row.EnvironmentId, new JobRequest(DelegationJobKind, new JsonObject { ["delegation"] = row.Id.ToString(), ["phase"] = phase })
        {
            RunAt = at,
            DedupKey = $"{DelegationJobKind}:{row.Id}:{phase}",
            ReplaceExisting = true,
        }, ct);

    /// <summary>Adds the delegate to the approvers of the requests waiting for the delegator.</summary>
    private async Task GrantDelegateAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, ApprovalDelegationRow row, CancellationToken ct)
    {
        var waiting = await WaitingForAsync(db, ctx with { PrincipalId = row.Delegator }, artifact.Config, ct);
        foreach (var recordId in waiting.Where(w => w.OnBehalfOf is null).Select(w => w.Step.RecordId).Distinct())
        {
            var approval = await db.Approvals.AsNoTracking().FirstAsync(a => a.RecordId == recordId && a.Status == Statuses.Pending, ct);
            if (artifact.Config.FindEntity(approval.EntityId) is { Request.Approval: not null } entity && await LockRecordAsync(db, recordId, ct) is not null)
            {
                await AddApproversAsync(db, ctx, artifact, entity, recordId, [row.Delegate], ct);
            }
        }
    }

    /// <summary>
    /// Removes the delegate from the approvers of pending requests they neither decided on nor may decide in their own
    /// right (ADR 0016: delegated read access lasts only as long as the delegation).
    /// </summary>
    private async Task WithdrawDelegateAsync(KernelDbContext db, RequestContext ctx, ActiveArtifact artifact, ApprovalDelegationRow row, CancellationToken ct)
    {
        var id = row.Delegate.ToString();
        var facts = (await access.ForAsync(db, ctx with { PrincipalId = row.Delegate }, artifact.Config, ct, fresh: true)).Facts;
        var pending = await db.Approvals.AsNoTracking().Where(a => a.Status == Statuses.Pending).ToListAsync(ct);
        foreach (var approval in pending)
        {
            if (artifact.Config.FindEntity(approval.EntityId) is not { Request.Approval: { } binding } entity)
            {
                continue;
            }

            var data = await RecordDataAsync(db, approval.RecordId, ct);
            if (data[binding.ApproversField] is not JsonArray list || !list.OfType<JsonValue>().Any(v => v.GetValue<string>() == id))
            {
                continue;
            }

            var steps = await db.ApprovalSteps.AsNoTracking().Where(s => s.ApprovalId == approval.Id).ToListAsync(ct);
            if (steps.Exists(s => s.DecidedBy == row.Delegate) || steps.Exists(s => Matches(s, facts)) || await LockRecordAsync(db, approval.RecordId, ct) is null)
            {
                continue;
            }

            var kept = list.OfType<JsonValue>().Select(v => v.GetValue<string>()).Where(v => v != id).Select(v => (JsonNode)JsonValue.Create(v)).ToArray();
            await records.SystemWriteAsync(db, ctx, artifact, entity, approval.RecordId, null, new JsonObject { [binding.ApproversField] = new JsonArray(kept) }, ct);
        }
    }

    // ------------------------------------------------------------------ intake sweep

    /// <summary>
    /// Starts approvals for requests still in intake whose item now names a policy (scheduled after each activation).
    /// Records pinned to a workflow version without the approval state predate approvals and are left alone (ASM-046).
    /// </summary>
    internal async Task<int> SweepAsync(KernelDbContext db, Guid tenantId, Guid environmentId, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, tenantId, environmentId, ct);
        var started = 0;
        foreach (var entity in artifact.Config.Entities.Where(e => e.Request?.Approval is not null))
        {
            var binding = entity.Request!;
            var flow = artifact.Config.WorkflowFor(entity.Id);
            var items = artifact.Config.CatalogItems.Where(i => i.RequestEntity == entity.Id && i.ApprovalPolicy is not null).Select(i => i.Id).ToList();
            if (flow is null || items.Count == 0)
            {
                continue;
            }

            var candidates = await db.Records.FromSqlInterpolated($"SELECT * FROM kernel.records r WHERE r.entity_id = {entity.Id} AND r.deleted_at IS NULL AND coalesce(r.data->>{flow.StateField}, {flow.InitialState}) = {flow.InitialState} AND r.data->>{binding.CatalogItemField} = ANY({items.ToArray()}) AND NOT EXISTS (SELECT 1 FROM kernel.approvals a WHERE a.record_id = r.id) ORDER BY r.created_at LIMIT {SweepBatch}")
                .AsNoTracking().Select(r => r.Id).ToListAsync(ct);
            foreach (var recordId in candidates)
            {
                if (await StartIfNeededAsync(db, SystemContext(tenantId, environmentId, recordId), artifact, entity, recordId, ct))
                {
                    started++;
                }
            }
        }

        return started;
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<RecordRow?> LockRecordAsync(KernelDbContext db, Guid recordId, CancellationToken ct) =>
        (await db.Records.FromSqlInterpolated($"SELECT * FROM kernel.records WHERE id = {recordId} AND deleted_at IS NULL FOR UPDATE").AsNoTracking().ToListAsync(ct)).FirstOrDefault();

    private static async Task<JsonObject> RecordDataAsync(KernelDbContext db, Guid recordId, CancellationToken ct) =>
        JsonNode.Parse(await db.Records.AsNoTracking().Where(r => r.Id == recordId).Select(r => r.Data).FirstAsync(ct))!.AsObject();

    private static RequestContext SystemContext(Guid tenantId, Guid environmentId, Guid correlation) => new()
    {
        TenantId = tenantId,
        EnvironmentId = environmentId,
        PrincipalId = Guid.Empty,
        Channel = "worker",
        CorrelationId = correlation,
    };

    /// <summary>The request's current revision, for platform events about it (the envelope names the revision it saw).</summary>
    private static Task<int> RevisionAsync(KernelDbContext db, Guid recordId, CancellationToken ct) =>
        db.Records.Where(r => r.Id == recordId).Select(r => r.Revision).FirstAsync(ct);

}

public sealed record RecordApprovalView(Guid Id, string PolicyId, string PolicyLabel, string Status, int StageIndex, string? OutcomeReason, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, IReadOnlyList<ApprovalStageView> Stages);

public sealed record ApprovalStageView(string Id, string Label, string Mode, string Status, IReadOnlyList<ApprovalStepView> Steps);

public sealed record ApprovalStepView(Guid Id, string ApproverKind, string Approver, string Status, string? DecidedBy, string? OnBehalfOf, string? Comment, DateTimeOffset? DecidedAt, DateTimeOffset? DueAt, bool Escalation, bool CanDecide);

public sealed record ApprovalInbox(IReadOnlyList<ApprovalInboxItem> Pending, IReadOnlyList<ApprovalInboxItem> Decided);

public sealed record ApprovalInboxItem(Guid StepId, Guid ApprovalId, string Entity, Guid RecordId, string Policy, string Stage, string Status, DateTimeOffset? DueAt, bool Escalation, string? Requester, string? OnBehalfOf, string? Comment, DateTimeOffset? DecidedAt, RecordView? Record);

public sealed record DelegationView(Guid Id, Guid DelegatorId, string Delegator, Guid DelegateId, string Delegate, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Active);
