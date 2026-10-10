using System.Text.Json;
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

public sealed record DraftStep(int Seq, string Op, string Summary, IReadOnlyList<string> Targets, string Source, bool ByAgent, string? Actor, bool Undone, DateTimeOffset At);

public sealed record DraftState(
    Guid Id,
    int Version,
    int BaseRevision,
    int PublishedRevision,
    IReadOnlyList<DraftStep> Steps,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ConfigChange> Changes,
    string? ArtifactHash,
    bool PermissionImpact);

public sealed record ApplyResult(int Version, int Seq, string Summary, IReadOnlyList<string> Targets, IReadOnlyList<Diagnostic> Warnings, DraftState Draft);

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

    public Task<ApplyResult> ApplyAsync(RequestContext ctx, OverlayOp op, int expectedVersion, string source, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (_, perms) = await RequireEditAsync(db, ctx, ct);
            if (op.AffectsPermissions && !perms.Can(PlatformActions.PermissionsAdmin))
            {
                throw KernelException.Forbidden(
                    ctx.AgentId is null ? "Changing who can see or edit fields requires the permission administration right." : "AI agents cannot change permissions. Ask an administrator to make this change.",
                    suggestion: "Permission changes are a separate administrative action (REQ-AUTHZ-008).");
            }

            var draft = await LockOpenDraftAsync(db, ct) ?? throw new KernelException("no_draft", "There is no open draft. Open one first.", 409, suggestion: "Call open_draft.");
            await CheckVersionAsync(db, draft, expectedVersion, ct);
            var before = await CompileAsync(db, ctx, draft, ct);
            if (before.Commands.Count(c => !c.Undone) >= MaxCommandsPerDraft)
            {
                throw new KernelException("draft_too_large", $"A draft can hold at most {MaxCommandsPerDraft} changes. Publish or discard it first.", 409);
            }

            var candidateOps = before.Overlay.Append(op).ToList();
            var after = Compile(before.Published, candidateOps);
            var outcome = after.Composition.Outcomes[^1];
            if (outcome.Status is OpStatus.Failed or OpStatus.Conflict)
            {
                throw Refuse(outcome.Diagnostics);
            }

            // Refuse changes that introduce new validation errors; existing ones (e.g. after an upgrade) don't block unrelated edits.
            var existing = before.Result.Errors.Select(Key).ToHashSet(StringComparer.Ordinal);
            var introduced = after.Errors.Where(e => !existing.Contains(Key(e))).ToList();
            if (introduced.Count > 0)
            {
                throw Refuse(introduced);
            }

            var seq = (before.Commands.Count == 0 ? 0 : before.Commands.Max(c => c.Seq)) + 1;
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
            });
            draft.Version++;
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            AuditWriter.Add(db, ctx, "draft.apply", "draft", draft.Id.ToString(), before.Published.Revision, new { seq, op = Composer.OpName(op), summary, targets });
            await db.SaveChangesAsync(ct);
            await NotifyAsync(db, ctx, draft, seq, "apply", targets, ct);
            var state = await StateAsync(db, ctx, draft, ct);
            return new ApplyResult(draft.Version, seq, summary, targets, outcome.Diagnostics.Where(d => !d.IsError).ToList(), state);
        }, ct);

    /// <summary>Undoes one step. Refused when later active steps depend on it (draft.undo_has_dependents).</summary>
    public Task<DraftState> SetUndoneAsync(RequestContext ctx, int seq, bool undone, int expectedVersion, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireEditAsync(db, ctx, ct);
            var draft = await LockOpenDraftAsync(db, ct) ?? throw new KernelException("no_draft", "There is no open draft.", 409);
            await CheckVersionAsync(db, draft, expectedVersion, ct);
            var commands = await db.DraftCommands.AsTracking().Where(c => c.DraftId == draft.Id).OrderBy(c => c.Seq).ToListAsync(ct);
            var command = commands.FirstOrDefault(c => c.Seq == seq) ?? throw KernelException.NotFound($"Step {seq}");
            if (command.Undone == undone)
            {
                return await StateAsync(db, ctx, draft, ct);
            }

            var current = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var ops = current.Overlay.Concat(commands.Where(c => c.Seq == seq ? !undone : !c.Undone).Select(c => SpecJson.Deserialize<OverlayOp>(c.Op))).ToList();
            var result = Compile(current, ops);
            var failing = result.Composition.Outcomes.Skip(current.Overlay.Count).Where(o => o.Status is OpStatus.Failed).ToList();
            var activeAfter = commands.Where(c => c.Seq == seq ? !undone : !c.Undone).ToList();
            if (failing.Count > 0 || result.Errors.Any())
            {
                var dependents = new List<string>();
                if (undone)
                {
                    // Later active steps whose elements depend on what this step created or changed.
                    var full = Compile(current, current.Overlay.Concat(commands.Where(c => !c.Undone).Select(c => SpecJson.Deserialize<OverlayOp>(c.Op))).ToList());
                    var graph = DependencyGraph.Build(full.Config);
                    var impact = command.Targets.SelectMany(graph.ImpactOf).ToHashSet(StringComparer.Ordinal);
                    dependents = commands.Where(c => c.Seq > seq && !c.Undone && c.Targets.Any(impact.Contains)).Select(c => $"step {c.Seq} ({c.Summary})").ToList();
                }

                dependents.AddRange(failing.Select(o => activeAfter[o.Index - current.Overlay.Count]).Where(c => c.Seq != seq).Select(c => $"step {c.Seq} ({c.Summary})").Where(x => !dependents.Contains(x)));
                throw new KernelException(
                    undone ? "draft.undo_has_dependents" : "draft.redo_invalid",
                    undone && dependents.Count > 0 ? $"Step {seq} can't be undone on its own: {string.Join("; ", dependents)} depend on it." : $"Step {seq} can't be {(undone ? "undone" : "redone")}: {string.Join(" ", result.Errors.Select(e => e.Message).Take(3))}",
                    409,
                    suggestion: undone && dependents.Count > 0 ? "Undo the dependent steps first (latest first)." : null);
            }

            command.Undone = undone;
            draft.Version++;
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            AuditWriter.Add(db, ctx, undone ? "draft.undo" : "draft.redo", "draft", draft.Id.ToString(), current.Revision, new { seq });
            await db.SaveChangesAsync(ct);
            await NotifyAsync(db, ctx, draft, seq, undone ? "undo" : "redo", command.Targets, ct);
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
        var steps = compiled.Commands.Select(c => new DraftStep(c.Seq, Composer.OpName(SpecJson.Deserialize<OverlayOp>(c.Op)), c.Summary, c.Targets, c.Source, c.Agent is not null, names.GetValueOrDefault(c.Actor), c.Undone, c.CreatedAt)).ToList();
        return new DraftState(
            draft.Id,
            draft.Version,
            draft.BaseRevision,
            compiled.Published.Revision,
            steps,
            compiled.Result.Diagnostics.ToList(),
            ConfigDiff.Compare(compiled.Published.Config, compiled.Result.Config),
            compiled.Result.Manifest?.ContentHash,
            compiled.Result.Manifest?.PermissionImpact.Changed ?? false);
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
