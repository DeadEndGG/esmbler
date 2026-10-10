using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Configuration;

public sealed record DraftStep(int Seq, string Op, string Summary, IReadOnlyList<string> Targets, string Source, bool ByAgent, string? Actor, bool Undone, DateTimeOffset At, Guid? Batch = null);

public sealed record DraftState(
    Guid Id,
    int Version,
    int BaseRevision,
    int PublishedRevision,
    IReadOnlyList<DraftStep> Steps,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ConfigChange> Changes,
    string? ArtifactHash,
    bool PermissionImpact,
    IReadOnlyList<WorkflowImpact>? WorkflowImpact = null,
    IReadOnlyList<ApprovalImpact>? ApprovalImpact = null,
    IReadOnlyList<SlaImpact>? SlaImpact = null);

/// <summary>Running SLA clocks of a policy or calendar the draft changes; they keep the goal and calendar they started with (ADR 0018).</summary>
public sealed record SlaImpact(string Policy, string PolicyLabel, int Running);

/// <summary>Approvals in flight under a policy the draft changes or moves items away from; they keep the policy they started with (ADR 0016).</summary>
public sealed record ApprovalImpact(string Policy, string PolicyLabel, int Pending);

/// <summary>
/// What publishing a changed workflow does to its running instances (REQ-MIG-003): how many run now, how many the
/// policy moves, and how many stay pinned to their version (with the states that have no target).
/// </summary>
public sealed record WorkflowImpact(string Workflow, string Entity, string EntityLabel, string Policy, int Running, int Moving, int StayingPinned, IReadOnlyList<string> UnmappedStates);

public sealed record ApplyResult(int Version, int Seq, string Summary, IReadOnlyList<string> Targets, IReadOnlyList<Diagnostic> Warnings, DraftState Draft);

/// <summary>One step of an applied batch.</summary>
public sealed record AppliedStep(int Seq, string Summary, IReadOnlyList<string> Targets, IReadOnlyList<Diagnostic> Warnings);

/// <summary>A batch applied as one change (REQ-WS-005): all steps or none, one draft version, undone together.</summary>
public sealed record BatchResult(int Version, Guid? Batch, IReadOnlyList<AppliedStep> Steps, DraftState Draft);

/// <summary>A compiled draft: published overlay + active draft commands composed on the installed base.</summary>
public sealed record CompiledDraft(DraftRow Draft, List<DraftCommandRow> Commands, CompileResult Result, ActiveArtifact Published, IReadOnlyList<OverlayOp> Overlay);

/// <summary>
/// Draft workspace (REQ-AI-005, REQ-LAY-004, REQ-LIVE-004). Every human or AI edit is a Command appended to the one
/// open draft of the environment; each carries the expected draft version so concurrent edits never overwrite
/// each other silently. Operations that would make the configuration invalid are refused with actionable errors.
/// </summary>
public sealed class DraftService(Database database, ConfigStore configs, ConfigActivator activator, AccessService access)
{
    public const int MaxCommandsPerDraft = 300;

    public Task<DraftState> OpenAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (published, perms) = await RequireEditAsync(db, ctx, ct);
            var draft = await db.Drafts.FirstOrDefaultAsync(d => d.Status == "open", ct);
            if (draft is null)
            {
                draft = new DraftRow { TenantId = ctx.TenantId, EnvironmentId = ctx.EnvironmentId, Id = Guid.NewGuid(), BaseRevision = published.Revision, Version = 0, Status = "open", CreatedBy = ctx.PrincipalId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                db.Drafts.Add(draft);
                AuditWriter.Add(db, ctx, "draft.open", "draft", draft.Id.ToString(), published.Revision);
                await db.SaveChangesAsync(ct);
            }

            return await StateAsync(db, ctx, draft, ct);
        }, ct);

    public Task<DraftState?> CurrentAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireEditAsync(db, ctx, ct);
            var draft = await db.Drafts.FirstOrDefaultAsync(d => d.Status == "open", ct);
            return draft is null ? null : await StateAsync(db, ctx, draft, ct);
        }, ct);

    public async Task<ApplyResult> ApplyAsync(RequestContext ctx, OverlayOp op, int expectedVersion, string source, CancellationToken ct)
    {
        var result = await ApplyBatchAsync(ctx, [op], expectedVersion, source, ct);
        var step = result.Steps.Single();
        return new ApplyResult(result.Version, step.Seq, step.Summary, step.Targets, step.Warnings, result.Draft);
    }

    /// <summary>
    /// Applies several operations as one change (REQ-WS-005): they are validated together, either all are recorded or
    /// none, the draft version moves once, and undoing any of them undoes the whole batch.
    /// </summary>
    public Task<BatchResult> ApplyBatchAsync(RequestContext ctx, IReadOnlyList<OverlayOp> ops, int expectedVersion, string source, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            if (ops.Count == 0)
            {
                throw new KernelException("empty_batch", "There is nothing to apply.", 400);
            }

            var (_, perms) = await RequireEditAsync(db, ctx, ct);
            if (ops.Any(op => op.AffectsPermissions) && !perms.Can(PlatformActions.PermissionsAdmin))
            {
                throw KernelException.Forbidden(
                    ctx.AgentId is null ? "Changing who can see or edit fields requires the permission administration right." : "AI agents cannot change permissions. Ask an administrator to make this change.",
                    suggestion: "Permission changes are a separate administrative action (REQ-AUTHZ-008).");
            }

            var draft = await LockOpenDraftAsync(db, ct) ?? throw new KernelException("no_draft", "There is no open draft. Open one first.", 409, suggestion: "Call open_draft.");
            await CheckVersionAsync(db, draft, expectedVersion, ct);
            var before = await CompileAsync(db, ctx, draft, ct);
            if (before.Commands.Count(c => !c.Undone) + ops.Count > MaxCommandsPerDraft)
            {
                throw new KernelException("draft_too_large", $"A draft can hold at most {MaxCommandsPerDraft} changes. Publish or discard it first.", 409);
            }

            var after = Compile(before.Published, [.. before.Overlay, .. ops]);
            var outcomes = after.Composition.Outcomes.Skip(after.Composition.Outcomes.Count - ops.Count).ToList();
            if (outcomes.FirstOrDefault(o => o.Status is OpStatus.Failed or OpStatus.Conflict) is { } failed)
            {
                throw Refuse(failed.Diagnostics);
            }

            // Refuse changes that introduce new validation errors; existing ones (e.g. after an upgrade) don't block unrelated edits.
            var existing = before.Result.Errors.Select(Key).ToHashSet(StringComparer.Ordinal);
            var introduced = after.Errors.Where(e => !existing.Contains(Key(e))).ToList();
            if (introduced.Count > 0)
            {
                throw Refuse(introduced);
            }

            var first = (before.Commands.Count == 0 ? 0 : before.Commands.Max(c => c.Seq)) + 1;
            Guid? batch = ops.Count > 1 ? Guid.NewGuid() : null;
            var steps = new List<AppliedStep>();
            for (var i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                var seq = first + i;
                var summary = op.Describe();
                var targets = op.Targets().ToArray();
                db.DraftCommands.Add(new DraftCommandRow
                {
                    TenantId = ctx.TenantId,
                    EnvironmentId = ctx.EnvironmentId,
                    DraftId = draft.Id,
                    Seq = seq,
                    Op = SpecJson.Serialize<OverlayOp>(op),
                    Summary = summary,
                    Targets = targets,
                    Actor = ctx.PrincipalId,
                    Agent = ctx.AgentId,
                    Source = source,
                    CreatedAt = DateTimeOffset.UtcNow,
                    BatchId = batch,
                });
                AuditWriter.Add(db, ctx, "draft.apply", "draft", draft.Id.ToString(), before.Published.Revision, new { seq, op = Composer.OpName(op), summary, targets, batch });
                steps.Add(new AppliedStep(seq, summary, targets, outcomes[i].Diagnostics.Where(d => !d.IsError).ToList()));
            }

            draft.Version++;
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await NotifyAsync(db, ctx, draft, steps[^1].Seq, "apply", [.. steps.SelectMany(s => s.Targets).Distinct(StringComparer.Ordinal)], ct);
            var state = await StateAsync(db, ctx, draft, ct);
            return new BatchResult(draft.Version, batch, steps, state);
        }, ct);

    /// <summary>
    /// Undoes (or redoes) one step, or with <paramref name="wholeChange"/> every step of the change (batch) it belongs to
    /// (REQ-LIVE-003, REQ-WS-005). Refused when later active steps depend on it (draft.undo_has_dependents).
    /// </summary>
    public Task<DraftState> SetUndoneAsync(RequestContext ctx, int seq, bool undone, int expectedVersion, CancellationToken ct, bool wholeChange = false) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (_, perms) = await RequireEditAsync(db, ctx, ct);
            var draft = await LockOpenDraftAsync(db, ct) ?? throw new KernelException("no_draft", "There is no open draft.", 409);
            await CheckVersionAsync(db, draft, expectedVersion, ct);
            var commands = await db.DraftCommands.AsTracking().Where(c => c.DraftId == draft.Id).OrderBy(c => c.Seq).ToListAsync(ct);
            var command = commands.FirstOrDefault(c => c.Seq == seq) ?? throw KernelException.NotFound($"Step {seq}");
            if (command.Undone == undone)
            {
                return await StateAsync(db, ctx, draft, ct);
            }

            // A change (batch) can be undone or redone as a whole (REQ-WS-005); any single step can still be undone alone (REQ-LIVE-003).
            var group = wholeChange && command.BatchId is { } batch ? commands.Where(c => c.BatchId == batch && c.Undone != undone).ToList() : [command];
            var inGroup = group.Select(c => c.Seq).ToHashSet();

            // Undoing or redoing a permission change is itself a permission change (REQ-AUTHZ-008). A change that includes
            // one is never split by someone without that right: undoing a companion step could remove its safeguard.
            var change = command.BatchId is { } changeBatch ? commands.Where(c => c.BatchId == changeBatch) : group;
            if (!perms.Can(PlatformActions.PermissionsAdmin) && change.Any(c => SpecJson.Deserialize<OverlayOp>(c.Op).AffectsPermissions))
            {
                throw KernelException.Forbidden(
                    ctx.AgentId is null ? "Undoing or redoing a change to who can see or edit things requires the permission administration right." : "AI agents cannot undo or redo permission changes. Ask an administrator.",
                    suggestion: "Permission changes are a separate administrative action (REQ-AUTHZ-008).");
            }

            var current = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var activeAfter = commands.Where(c => inGroup.Contains(c.Seq) ? !undone : !c.Undone).ToList();
            var ops = current.Overlay.Concat(activeAfter.Select(c => SpecJson.Deserialize<OverlayOp>(c.Op))).ToList();
            var result = Compile(current, ops);
            var failing = result.Composition.Outcomes.Skip(current.Overlay.Count).Where(o => o.Status is OpStatus.Failed).ToList();
            if (failing.Count > 0 || result.Errors.Any())
            {
                var dependents = new List<string>();
                if (undone)
                {
                    // Later active steps whose elements depend on what this step created or changed.
                    var full = Compile(current, current.Overlay.Concat(commands.Where(c => !c.Undone).Select(c => SpecJson.Deserialize<OverlayOp>(c.Op))).ToList());
                    var graph = DependencyGraph.Build(full.Config);
                    var impact = group.SelectMany(c => c.Targets).SelectMany(graph.ImpactOf).ToHashSet(StringComparer.Ordinal);
                    dependents = commands.Where(c => c.Seq > seq && !inGroup.Contains(c.Seq) && !c.Undone && c.Targets.Any(impact.Contains)).Select(c => $"step {c.Seq} ({c.Summary})").ToList();
                }

                dependents.AddRange(failing.Select(o => activeAfter[o.Index - current.Overlay.Count]).Where(c => !inGroup.Contains(c.Seq)).Select(c => $"step {c.Seq} ({c.Summary})").Where(x => !dependents.Contains(x)));
                throw new KernelException(
                    undone ? "draft.undo_has_dependents" : "draft.redo_invalid",
                    undone && dependents.Count > 0 ? $"Step {seq} can't be undone on its own: {string.Join("; ", dependents)} depend on it." : $"Step {seq} can't be {(undone ? "undone" : "redone")}: {string.Join(" ", result.Errors.Select(e => e.Message).Take(3))}",
                    409,
                    suggestion: undone && dependents.Count > 0 ? "Undo the dependent steps first (latest first)." : null);
            }

            group.ForEach(c => c.Undone = undone);
            draft.Version++;
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            AuditWriter.Add(db, ctx, undone ? "draft.undo" : "draft.redo", "draft", draft.Id.ToString(), current.Revision, new { seq, steps = group.Count > 1 ? inGroup.Order().ToArray() : null });
            await db.SaveChangesAsync(ct);
            await NotifyAsync(db, ctx, draft, seq, undone ? "undo" : "redo", [.. group.SelectMany(c => c.Targets).Distinct(StringComparer.Ordinal)], ct);
            return await StateAsync(db, ctx, draft, ct);
        }, ct);

    public Task DiscardAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireEditAsync(db, ctx, ct);
            var draft = await LockOpenDraftAsync(db, ct) ?? throw new KernelException("no_draft", "There is no open draft.", 409);
            draft.Status = "discarded";
            draft.Version++;
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            await db.PublishRequests.Where(p => p.DraftId == draft.Id && p.Status == "pending").ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "stale"), ct);
            AuditWriter.Add(db, ctx, "draft.discard", "draft", draft.Id.ToString(), draft.BaseRevision);
            await db.SaveChangesAsync(ct);
            await NotifyAsync(db, ctx, draft, 0, "discard", [], ct);
        }, ct);

    /// <summary>Compiles the open draft (for previews, publish, and the AI's validate/diff tools).</summary>
    public async Task<CompiledDraft> CompileAsync(KernelDbContext db, RequestContext ctx, DraftRow draft, CancellationToken ct)
    {
        var published = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var commands = await db.DraftCommands.Where(c => c.DraftId == draft.Id).OrderBy(c => c.Seq).ToListAsync(ct);
        var overlay = published.Overlay.Concat(commands.Where(c => !c.Undone).Select(c => SpecJson.Deserialize<OverlayOp>(c.Op))).ToList();
        return new CompiledDraft(draft, commands, Compile(published, overlay), published, overlay);
    }

    public Task<(CompiledDraft Draft, EffectivePermissions Permissions)?> OpenCompiledAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<(CompiledDraft, EffectivePermissions)?>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (_, perms) = await RequireEditAsync(db, ctx, ct);
            var draft = await db.Drafts.FirstOrDefaultAsync(d => d.Status == "open", ct);
            if (draft is null)
            {
                return null;
            }

            var compiled = await CompileAsync(db, ctx, draft, ct);
            var draftPerms = await access.ForAsync(db, ctx, compiled.Result.Config, ct);
            return (compiled, draftPerms);
        }, ct);

    private CompileResult Compile(ActiveArtifact published, IReadOnlyList<OverlayOp> overlay) =>
        activator.Compile(published.Lockfile.Select(m => (m.Id, m.Version)).ToList(), overlay, published.Config);

    internal async Task<DraftState> StateAsync(KernelDbContext db, RequestContext ctx, DraftRow draft, CancellationToken ct)
    {
        var compiled = await CompileAsync(db, ctx, draft, ct);
        var actors = compiled.Commands.Select(c => c.Actor).Distinct().ToList();
        var names = await db.Principals.Where(p => actors.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        var steps = compiled.Commands.Select(c => new DraftStep(c.Seq, Composer.OpName(SpecJson.Deserialize<OverlayOp>(c.Op)), c.Summary, c.Targets, c.Source, c.Agent is not null, names.GetValueOrDefault(c.Actor), c.Undone, c.CreatedAt, c.BatchId)).ToList();
        return new DraftState(
            draft.Id,
            draft.Version,
            draft.BaseRevision,
            compiled.Published.Revision,
            steps,
            compiled.Result.Diagnostics.ToList(),
            ConfigDiff.Compare(compiled.Published.Config, compiled.Result.Config),
            compiled.Result.Manifest?.ContentHash,
            compiled.Result.Manifest?.PermissionImpact.Changed ?? false,
            await WorkflowImpactAsync(db, compiled.Published.Config, compiled.Result.Config, ct),
            await ApprovalImpactAsync(db, compiled.Published.Config, compiled.Result.Config, ct),
            await SlaImpactAsync(db, compiled.Published.Config, compiled.Result.Config, ct));
    }

    private static async Task<IReadOnlyList<SlaImpact>> SlaImpactAsync(KernelDbContext db, EffectiveConfig published, EffectiveConfig draft, CancellationToken ct)
    {
        var changedCalendars = published.BusinessCalendars.Where(c => draft.FindBusinessCalendar(c.Id) is not { } now || !JsonNode.DeepEquals(SpecJson.ToNode(now), SpecJson.ToNode(c))).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var affected = published.SlaPolicies.Where(p =>
            draft.FindSlaPolicy(p.Id) is not { } now || !JsonNode.DeepEquals(SpecJson.ToNode(now), SpecJson.ToNode(p))
            || p.Targets.Any(t => (t.Calendar is { } c && changedCalendars.Contains(c)) || t.Goals.Any(g => g.Calendar is { } gc && changedCalendars.Contains(gc)))).ToList();
        var result = new List<SlaImpact>();
        foreach (var policy in affected.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var running = await db.SlaTimers.CountAsync(t => t.PolicyId == policy.Id && (t.Status == "running" || t.Status == "paused"), ct);
            if (running > 0)
            {
                result.Add(new SlaImpact(policy.Id, policy.Label, running));
            }
        }

        return result;
    }

    private static async Task<IReadOnlyList<ApprovalImpact>> ApprovalImpactAsync(KernelDbContext db, EffectiveConfig published, EffectiveConfig draft, CancellationToken ct)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in published.CatalogItems.Where(i => i.ApprovalPolicy is not null && draft.FindCatalogItem(i.Id)?.ApprovalPolicy != i.ApprovalPolicy))
        {
            affected.Add(item.ApprovalPolicy!);
        }

        foreach (var policy in published.ApprovalPolicies)
        {
            if (draft.FindApprovalPolicy(policy.Id) is not { } now || !JsonNode.DeepEquals(SpecJson.ToNode(now.Stages), SpecJson.ToNode(policy.Stages)))
            {
                affected.Add(policy.Id);
            }
        }

        var result = new List<ApprovalImpact>();
        foreach (var id in affected.Order(StringComparer.Ordinal))
        {
            var pending = await db.Approvals.CountAsync(a => a.PolicyId == id && a.Status == "pending", ct);
            if (pending > 0)
            {
                result.Add(new ApprovalImpact(id, published.FindApprovalPolicy(id)?.Label ?? id, pending));
            }
        }

        return result;
    }

    private sealed record StateCount(string? State, int Count);

    private static async Task<IReadOnlyList<WorkflowImpact>> WorkflowImpactAsync(KernelDbContext db, EffectiveConfig published, EffectiveConfig draft, CancellationToken ct)
    {
        var result = new List<WorkflowImpact>();
        foreach (var wf in draft.Workflows)
        {
            var old = published.Workflows.Find(w => w.Id == wf.Id);
            if (old is null || WorkflowVersions.Hash(old) == WorkflowVersions.Hash(wf))
            {
                continue;
            }

            var counts = await db.Database.SqlQuery<StateCount>(
                $"SELECT data->>{wf.StateField} AS \"State\", count(*)::int AS \"Count\" FROM kernel.records WHERE entity_id = {wf.Entity} AND deleted_at IS NULL GROUP BY 1").ToListAsync(ct);
            var running = counts.Where(c => old.FindState(c.State ?? old.InitialState)?.Category is not (StateCategories.Resolved or StateCategories.Closed or StateCategories.Cancelled)).ToList();
            var total = running.Sum(c => c.Count);
            // A policy applies only to the publish that sets it (ConfigActivator); otherwise instances stay pinned.
            var policy = SpecJson.Serialize(wf.ActiveInstances) == SpecJson.Serialize(old.ActiveInstances) ? new ActiveInstancePolicy() : wf.ActiveInstances;
            var moving = 0;
            var unmapped = new List<string>();
            foreach (var c in running)
            {
                var state = c.State ?? old.InitialState;
                var target = policy.Policy switch
                {
                    ActiveInstancePolicies.Cancel => policy.CancelState,
                    ActiveInstancePolicies.Migrate => policy.StateMap.TryGetValue(state, out var mapped) ? mapped : wf.FindState(state) is not null ? state : null,
                    _ => null,
                };
                if (policy.Policy == ActiveInstancePolicies.Pin)
                {
                    continue;
                }

                if (target is not null && wf.FindState(target) is not null)
                {
                    moving += c.Count;
                }
                else
                {
                    unmapped.Add(state);
                }
            }

            result.Add(new WorkflowImpact(wf.Id, wf.Entity, draft.FindEntity(wf.Entity)?.PluralLabel ?? wf.Entity, policy.Policy, total, moving, total - moving, unmapped));
        }

        return result;
    }

    private async Task<(ActiveArtifact Published, EffectivePermissions Perms)> RequireEditAsync(KernelDbContext db, RequestContext ctx, CancellationToken ct)
    {
        var published = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var perms = await access.ForAsync(db, ctx, published.Config, ct);
        if (!perms.Can(PlatformActions.ConfigEdit))
        {
            throw KernelException.Forbidden(ctx.AgentId is null ? "You can't customize this workspace." : "This AI agent may not edit configuration.", suggestion: "Ask an administrator for builder access (config.edit).");
        }

        return (published, perms);
    }

    private static async Task<DraftRow?> LockOpenDraftAsync(KernelDbContext db, CancellationToken ct)
    {
        var rows = await db.Drafts.FromSqlRaw("SELECT * FROM kernel.drafts WHERE status = 'open' FOR UPDATE").AsTracking().ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    private static async Task CheckVersionAsync(KernelDbContext db, DraftRow draft, int expectedVersion, CancellationToken ct)
    {
        if (draft.Version == expectedVersion)
        {
            return;
        }

        // REQ-LIVE-004 / REQ-ACC-02: tell the caller exactly what changed since the version it saw.
        var since = await db.DraftCommands.Where(c => c.DraftId == draft.Id).OrderBy(c => c.Seq).Select(c => new { c.Seq, c.Summary, c.Undone, c.Agent }).ToListAsync(ct);
        throw KernelException.Conflict(
            "draft_conflict",
            $"The draft changed while you were editing (version {expectedVersion} → {draft.Version}). Review the latest steps and try again.",
            new { expectedVersion, currentVersion = draft.Version, steps = since },
            "Reload the draft and re-apply your change.");
    }

    private static KernelException Refuse(IEnumerable<Diagnostic> diagnostics)
    {
        var list = diagnostics.ToList();
        var first = list[0];
        return new KernelException(first.Code, list.Count == 1 ? first.Message : $"{first.Message} (and {list.Count - 1} more)", 422, first.Target, first.Suggestion, list);
    }

    private static string Key(Diagnostic d) => $"{d.Code}|{d.Target}|{d.Message}";

    private static Task NotifyAsync(KernelDbContext db, RequestContext ctx, DraftRow draft, int seq, string kind, IReadOnlyList<string> targets, CancellationToken ct)
    {
        // Delivered on commit. IDs only; listeners reload under their own authorization.
        var payload = JsonSerializer.Serialize(new { t = ctx.TenantId, e = ctx.EnvironmentId, d = draft.Id, v = draft.Version, s = seq, k = kind, targets, agent = ctx.AgentId is not null, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_notify('esmbler_draft', {payload})", ct);
    }
}
