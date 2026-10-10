using System.Text.Json;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Configuration;

public sealed record ReadinessItem(string Severity, string Code, string Message, string? Target);

public sealed record PublishRequestView(Guid Id, int DraftVersion, string ArtifactHash, string Summary, string Status, bool RequestedByAgent, string? RequestedBy, DateTimeOffset CreatedAt, IReadOnlyList<ReadinessItem> Readiness, IReadOnlyList<ConfigChange> Changes, bool PermissionImpact);

public sealed record PublishResult(int Revision, string ContentHash, int Steps);

public sealed record RevisionView(int Revision, string ContentHash, string Status, string Source, string? CreatedBy, string? ApprovedBy, DateTimeOffset CreatedAt, IReadOnlyList<LockedModule> Modules, int OverlayOperations, bool CanRollback, string? RollbackBlockedReason);

/// <summary>
/// Compile/preview/publish (REQ-CFG-006/007/010). The AI may request a publish; only a human with config.publish
/// approves it, bound to the exact artifact hash they reviewed, the tenant environment, and the expected published
/// revision. Any later change to the draft invalidates the approval.
/// </summary>
public sealed class PublishService(Database database, ConfigStore configs, ConfigActivator activator, AccessService access, DraftService drafts)
{
    public Task<PublishRequestView> RequestAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var published = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, published.Config, ct);
            if (!perms.Can(PlatformActions.ConfigEdit) || !ctx.HasScope(AgentScopes.PublishRequest))
            {
                throw KernelException.Forbidden("You can't request a publish here.");
            }

            var draft = await db.Drafts.AsTracking().FirstOrDefaultAsync(d => d.Status == "open", ct) ?? throw new KernelException("no_draft", "There is no open draft to publish.", 409);
            var compiled = await drafts.CompileAsync(db, ctx, draft, ct);
            if (!compiled.Result.Succeeded)
            {
                throw new KernelException("config_invalid", "The draft has errors that must be fixed before publishing.", 422, details: compiled.Result.Errors.ToList(), suggestion: "Run validate_draft to see each error with a fix.");
            }

            if (compiled.Commands.All(c => c.Undone))
            {
                throw new KernelException("nothing_to_publish", "The draft has no changes.", 409);
            }

            await db.PublishRequests.Where(p => p.DraftId == draft.Id && p.Status == "pending").ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "stale"), ct);
            var summary = string.Join("; ", compiled.Commands.Where(c => !c.Undone).Select(c => c.Summary));
            var row = new PublishRequestRow
            {
                TenantId = ctx.TenantId,
                EnvironmentId = ctx.EnvironmentId,
                Id = Guid.NewGuid(),
                DraftId = draft.Id,
                DraftVersion = draft.Version,
                ArtifactHash = compiled.Result.Manifest!.ContentHash,
                Summary = summary.Length > 2000 ? summary[..2000] + "…" : summary,
                RequestedBy = ctx.PrincipalId,
                RequestedAgent = ctx.AgentId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.PublishRequests.Add(row);
            AuditWriter.Add(db, ctx, "publish.request", "draft", draft.Id.ToString(), published.Revision, new { row.ArtifactHash, draft.Version });
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_notify('esmbler_draft', {JsonSerializer.Serialize(new { t = ctx.TenantId, e = ctx.EnvironmentId, d = draft.Id, v = draft.Version, s = 0, k = "publish_requested" })})", ct);
            return View(row, compiled, null);
        }, ct);

    public Task<PublishRequestView?> PendingAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var row = await db.PublishRequests.Where(p => p.Status == "pending").OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync(ct);
            if (row is null)
            {
                return null;
            }

            var draft = await db.Drafts.FirstAsync(d => d.Id == row.DraftId, ct);
            var compiled = await drafts.CompileAsync(db, ctx, draft, ct);
            var name = await db.Principals.Where(p => p.Id == row.RequestedBy).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);
            return View(row, compiled, name);
        }, ct);

    /// <summary>One-click publish by a human. <paramref name="artifactHash"/> is what they reviewed.</summary>
    public Task<PublishResult> PublishAsync(RequestContext ctx, string artifactHash, int expectedRevision, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            if (ctx.AgentId is not null)
            {
                throw KernelException.Forbidden("AI agents can't publish. A person reviews and publishes the draft.", suggestion: "Use request_publish; a builder approves it in the app.");
            }

            var published = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, published.Config, ct, fresh: true);
            if (!perms.Can(PlatformActions.ConfigPublish))
            {
                throw KernelException.Forbidden("Publishing requires the config.publish right.", suggestion: "Request a publish; an administrator approves it.");
            }

            var drafts1 = await db.Drafts.FromSqlRaw("SELECT * FROM kernel.drafts WHERE status = 'open' FOR UPDATE").AsTracking().ToListAsync(ct);
            var draft = drafts1.FirstOrDefault() ?? throw new KernelException("no_draft", "There is no open draft to publish.", 409);
            var compiled = await drafts.CompileAsync(db, ctx, draft, ct);
            if (!compiled.Result.Succeeded)
            {
                throw new KernelException("config_invalid", "The draft has errors that must be fixed before publishing.", 422, details: compiled.Result.Errors.ToList());
            }

            if (compiled.Result.Manifest!.ContentHash != artifactHash)
            {
                throw KernelException.Conflict("approval_stale", "The draft changed after you reviewed it. Review the latest version and publish again.", new { reviewed = artifactHash, current = compiled.Result.Manifest.ContentHash });
            }

            if (compiled.Result.Manifest.PermissionImpact.Changed && !perms.Can(PlatformActions.PermissionsAdmin))
            {
                throw KernelException.Forbidden("This draft changes permissions; publishing it requires the permission administration right.", suggestion: string.Join("; ", compiled.Result.Manifest.PermissionImpact.Changes));
            }

            var revision = await activator.ActivateAsync(db, ctx.TenantId, ctx.EnvironmentId, compiled.Result, compiled.Overlay, "publish", expectedRevision, draft.CreatedBy, ctx.PrincipalId, ct);
            draft.Status = "published";
            draft.Version++;
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            await db.PublishRequests.Where(p => p.DraftId == draft.Id && p.Status == "pending").ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, p => p.ArtifactHash == artifactHash ? "approved" : "stale").SetProperty(p => p.DecidedBy, ctx.PrincipalId).SetProperty(p => p.DecidedAt, DateTimeOffset.UtcNow), ct);
            AuditWriter.Add(db, ctx, "config.publish", "config", revision.ToString(System.Globalization.CultureInfo.InvariantCulture), revision, new { artifactHash, steps = compiled.Commands.Count(c => !c.Undone), permissionImpact = compiled.Result.Manifest.PermissionImpact });
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_notify('esmbler_draft', {JsonSerializer.Serialize(new { t = ctx.TenantId, e = ctx.EnvironmentId, d = draft.Id, v = draft.Version, s = 0, k = "published", r = revision })})", ct);
            return new PublishResult(revision, artifactHash, compiled.Commands.Count(c => !c.Undone));
        }, ct);

    public Task<IReadOnlyList<RevisionView>> RevisionsAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<RevisionView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var published = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, published.Config, ct);
            if (!perms.Can(PlatformActions.ConfigEdit) && !perms.Can(PlatformActions.AuditRead))
            {
                throw KernelException.Forbidden("Viewing configuration history requires builder or audit rights.");
            }

            var rows = await db.ConfigArtifacts.OrderByDescending(a => a.Revision).Take(50).Select(a => new { a.Revision, a.ContentHash, a.Status, a.Source, a.CreatedBy, a.ApprovedBy, a.CreatedAt, a.Manifest }).ToListAsync(ct);
            var people = rows.SelectMany(r => new[] { r.CreatedBy, r.ApprovedBy }).OfType<Guid>().Distinct().ToList();
            var names = await db.Principals.Where(p => people.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
            return rows.Select(r =>
            {
                var manifest = JsonSerializer.Deserialize<ArtifactManifest>(r.Manifest, SpecJson.Options)!;
                var blocked = RollbackBlocker(published.Lockfile, manifest.Lockfile);
                return new RevisionView(r.Revision, r.ContentHash, r.Status, r.Source, r.CreatedBy is { } c ? names.GetValueOrDefault(c) : null, r.ApprovedBy is { } a ? names.GetValueOrDefault(a) : null, r.CreatedAt, manifest.Lockfile, manifest.OverlayOperations, r.Revision != published.Revision && blocked is null, r.Revision == published.Revision ? "This is the active revision." : blocked);
            }).ToList();
        }, ct);

    /// <summary>
    /// Restores an earlier configuration as a new revision. Blocked when module versions differ (data migrations ran
    /// in between): restoring configuration would not undo data changes, so a forward repair is offered instead.
    /// </summary>
    public Task<PublishResult> RollbackAsync(RequestContext ctx, int toRevision, int expectedRevision, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            if (ctx.AgentId is not null)
            {
                throw KernelException.Forbidden("AI agents can't roll back configuration.");
            }

            var published = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var perms = await access.ForAsync(db, ctx, published.Config, ct, fresh: true);
            if (!perms.Can(PlatformActions.ConfigPublish))
            {
                throw KernelException.Forbidden("Rolling back requires the config.publish right.");
            }

            if (await db.Drafts.AnyAsync(d => d.Status == "open", ct))
            {
                throw KernelException.Conflict("draft_open", "Publish or discard the open draft before rolling back.");
            }

            var target = await configs.GetAsync(db, ctx.TenantId, ctx.EnvironmentId, toRevision, ct);
            if (RollbackBlocker(published.Lockfile, target.Lockfile) is { } reason)
            {
                throw KernelException.Conflict("rollback_unsafe", reason, suggestion: "Fix forward: open a draft and change the configuration on top of the current version.");
            }

            var result = activator.Compile(target.Lockfile.Select(m => (m.Id, m.Version)).ToList(), target.Overlay, published.Config);
            if (result.Manifest?.PermissionImpact.Changed == true && !perms.Can(PlatformActions.PermissionsAdmin))
            {
                throw KernelException.Forbidden("This rollback changes permissions and requires the permission administration right.");
            }

            var revision = await activator.ActivateAsync(db, ctx.TenantId, ctx.EnvironmentId, result, target.Overlay, "rollback", expectedRevision, ctx.PrincipalId, ctx.PrincipalId, ct);
            AuditWriter.Add(db, ctx, "config.rollback", "config", revision.ToString(System.Globalization.CultureInfo.InvariantCulture), revision, new { from = published.Revision, to = toRevision });
            await db.SaveChangesAsync(ct);
            return new PublishResult(revision, result.Manifest!.ContentHash, target.Overlay.Count);
        }, ct);

    private static string? RollbackBlocker(IReadOnlyList<LockedModule> current, IReadOnlyList<LockedModule> target)
    {
        var differs = current.Where(c => target.FirstOrDefault(t => t.Id == c.Id) is not { } t || t.Version != c.Version).Select(c => c.Id).ToList();
        return differs.Count == 0 ? null : $"Module versions changed since that revision ({string.Join(", ", differs)}); its data migrations can't be undone by restoring configuration.";
    }

    private static PublishRequestView View(PublishRequestRow row, CompiledDraft compiled, string? requestedBy)
    {
        var readiness = new List<ReadinessItem>();
        if (row.DraftVersion != compiled.Draft.Version || row.ArtifactHash != compiled.Result.Manifest?.ContentHash)
        {
            readiness.Add(new ReadinessItem("error", "approval_stale", "The draft changed after this request; request a publish again.", null));
        }

        readiness.AddRange(compiled.Result.Diagnostics.Select(d => new ReadinessItem(d.Severity, d.Code, d.Message, d.Target)));
        if (compiled.Result.Manifest?.PermissionImpact.Changed == true)
        {
            readiness.Add(new ReadinessItem("warning", "permission_impact", $"Changes permissions: {string.Join("; ", compiled.Result.Manifest.PermissionImpact.Changes)}", null));
        }

        return new PublishRequestView(row.Id, row.DraftVersion, row.ArtifactHash, row.Summary, row.Status, row.RequestedAgent is not null, requestedBy, row.CreatedAt, readiness, ConfigDiff.Compare(compiled.Published.Config, compiled.Result.Config), compiled.Result.Manifest?.PermissionImpact.Changed ?? false);
    }
}
