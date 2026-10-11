using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Services;

public sealed record AuditQuery(DateTimeOffset? From, DateTimeOffset? To, Guid? Actor, string? Operation, string? TargetType, string? TargetId, string? Outcome, long? Before, int Limit = 50);

public sealed record AuditEntryView(long Id, DateTimeOffset At, string? Actor, Guid? ActorId, bool Agent, string Channel, string Operation, string TargetType, string? TargetId, string Outcome, bool WorkspaceWide, JsonNode? Details);

public sealed record AuditPage(IReadOnlyList<AuditEntryView> Items, long? Next);

public sealed record AuditChainView(long Sealed, long AnchorSeq, DateTimeOffset? SealedAt, long VerifiedThrough, DateTimeOffset? VerifiedAt, long? BrokenAt, DateTimeOffset? BrokenDetectedAt, bool Verifying);

public sealed record AuditPolicyView(int RetentionDays, int MinDays, int MaxDays, IReadOnlyList<LegalHoldView> Holds);

public sealed record LegalHoldView(Guid Id, string Reason, string CreatedBy, DateTimeOffset CreatedAt, string? ReleasedBy, DateTimeOffset? ReleasedAt);

/// <summary>
/// The audit log as people use it (ADR 0022, REQ-AUD-001): search (audit.read), streamed export (audit.export; the export
/// itself is audited before the first byte), the tamper-evidence chain (status for audit.read, verification on demand
/// for audit.manage), retention and legal holds (audit.manage). Audit entries can hold field values, so AI agents never
/// read or export them. Permission administration implies every audit right.
/// </summary>
public sealed class AuditService(Database database, ConfigStore configs, AccessService access, IClock clock)
{
    public const int DefaultRetentionDays = 2555;
    public const int MinRetentionDays = 365;
    public const int MaxRetentionDays = 3650;
    public const int MaxExportRows = 500_000;
    public static readonly TimeSpan MaxExportRange = TimeSpan.FromDays(400);

    public Task<AuditPage> SearchAsync(RequestContext ctx, AuditQuery query, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (config, perms) = await RequireWithConfigAsync(db, ctx, PlatformActions.AuditRead, fresh: false, ct);
            var limit = Math.Clamp(query.Limit, 1, 200);
            var rows = await Filter(db.AuditLog.AsNoTracking(), query).OrderByDescending(a => a.Id).Take(limit + 1).ToListAsync(ct);
            var names = await NamesAsync(db, rows.Select(r => r.Actor), ct);
            var items = rows.Take(limit).Select(r => View(r, names, config, perms)).ToList();
            return new AuditPage(items, rows.Count > limit ? items[^1].Id : null);
        }, ct);

    /// <summary>
    /// Writes the matching entries (oldest first) as CSV or JSON Lines. The export is audited in its own committed
    /// transaction first, so even an aborted download is on record. JSON Lines carry each entry's chain link and canonical
    /// text so the chain can be verified offline; CSV cells that a spreadsheet would run as formulas are escaped.
    /// </summary>
    public async Task ExportAsync(RequestContext ctx, string format, AuditQuery query, Stream output, CancellationToken ct)
    {
        var range = await PrepareExportAsync(ctx, format, query, ct);
        await WriteExportAsync(ctx, format, range, output, ct);
    }

    /// <summary>Checks the right and the range and records the export; returns the query with its date range fixed.</summary>
    public async Task<AuditQuery> PrepareExportAsync(RequestContext ctx, string format, AuditQuery query, CancellationToken ct)
    {
        if (format is not ("csv" or "jsonl"))
        {
            throw new KernelException("invalid_format", $"'{format}' is not an export format.", 400, "format", "Use csv or jsonl.");
        }

        var to = query.To ?? clock.UtcNow;
        var from = query.From ?? to.AddDays(-30);
        if (to - from > MaxExportRange || to < from)
        {
            throw new KernelException("invalid_range", "Export at most 400 days at a time.", 400, "from", "Narrow the date range.");
        }

        await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var perms = await RequireAsync(db, ctx, PlatformActions.AuditExport, fresh: true, ct);
            if (format == "jsonl" && !perms.Can(PlatformActions.PermissionsAdmin))
            {
                // The chain export carries every entry's canonical text unredacted (it must, to verify offline).
                throw KernelException.Forbidden("Exporting the audit chain needs permission administration; export CSV instead.");
            }

            AuditWriter.Add(db, ctx, "audit.export", "audit_log", null, null, new { format, from, to, query.Actor, query.Operation, query.TargetType, query.TargetId, query.Outcome });
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return query with { From = from, To = to, Before = null };
    }

    /// <summary>Streams a prepared export (oldest first), at most <see cref="MaxExportRows"/> entries.</summary>
    public async Task WriteExportAsync(RequestContext ctx, string format, AuditQuery query, Stream output, CancellationToken ct)
    {
        var from = query.From ?? throw new ArgumentException("Prepare the export first.", nameof(query));
        var to = query.To ?? throw new ArgumentException("Prepare the export first.", nameof(query));
        var (config, perms) = await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db => await RequireWithConfigAsync(db, ctx, PlatformActions.AuditExport, fresh: false, ct), ct);
        if (format == "jsonl" && !perms.Can(PlatformActions.PermissionsAdmin))
        {
            throw KernelException.Forbidden("Exporting the audit chain needs permission administration.");
        }

        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        if (format == "csv")
        {
            await writer.WriteLineAsync("id,at,actor,agent,channel,operation,target_type,target_id,outcome,workspace_wide,details");
        }
        else
        {
            var head = await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, db => db.Database.SqlQuery<HeadRow>($"""
                SELECT h.anchor_seq AS "AnchorSeq", encode(h.anchor_hash, 'hex') AS "AnchorHash", h.last_seq AS "LastSeq", encode(h.last_hash, 'hex') AS "LastHash"
                FROM kernel.audit_chain_heads h
                """).FirstOrDefaultAsync(ct), ct);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { export = "esmbler-audit", version = 1, from, to, chain = head, hash = "sha256(hex(prev_hash) || canonical)" }));
        }

        long after = 0;
        var written = 0;
        while (written < MaxExportRows)
        {
            var batch = await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
            {
                var rows = await Filter(db.AuditLog.AsNoTracking(), query with { From = from, To = to }).Where(a => a.Id > after).OrderBy(a => a.Id).Take(1000).ToListAsync(ct);
                var ids = rows.Select(r => r.Id).ToArray();
                var links = format == "jsonl" && ids.Length > 0
                    ? await db.Database.SqlQuery<LinkRow>($"""
                        SELECT a.id AS "Id", a.chain_seq AS "ChainSeq", encode(a.prev_hash, 'hex') AS "PrevHash", encode(a.hash, 'hex') AS "Hash", kernel.audit_canonical(a) AS "Canonical"
                        FROM kernel.audit_log a WHERE a.id = ANY({ids})
                        """).ToDictionaryAsync(l => l.Id, ct)
                    : [];
                var names = await NamesAsync(db, rows.Select(r => r.Actor), ct);
                return (rows, links, names);
            }, ct);
            foreach (var r in batch.rows)
            {
                var view = View(r, batch.names, config, perms);
                if (format == "csv")
                {
                    await writer.WriteLineAsync(string.Join(',', new[]
                    {
                        view.Id.ToString(CultureInfo.InvariantCulture), view.At.ToString("O", CultureInfo.InvariantCulture), view.Actor ?? string.Empty, view.Agent ? "yes" : string.Empty, view.Channel, view.Operation, view.TargetType,
                        view.TargetId ?? string.Empty, view.Outcome, view.WorkspaceWide ? "yes" : string.Empty, view.Details?.ToJsonString() ?? string.Empty,
                    }.Select(Csv)));
                }
                else
                {
                    var link = batch.links.GetValueOrDefault(r.Id);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { entry = view, chainSeq = link?.ChainSeq, prevHash = link?.PrevHash, hash = link?.Hash, canonical = link?.Canonical }, SpecJson.Options));
                }

                written++;
            }

            if (batch.rows.Count < 1000)
            {
                break;
            }

            after = batch.rows[^1].Id;
            await writer.FlushAsync(ct);
        }

        await writer.FlushAsync(ct);
    }

    public Task<AuditChainView> ChainAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireAsync(db, ctx, PlatformActions.AuditRead, fresh: false, ct);
            return await ChainViewAsync(db, ct);
        }, ct);

    /// <summary>Verifies the workspace's whole chain now (in steps), audited.</summary>
    public Task<AuditChainView> VerifyNowAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireAsync(db, ctx, PlatformActions.AuditManage, fresh: true, ct);
            // A bounded amount of work per request; the cursor carries the rest to the next call or the nightly run.
            for (var i = 0; i < 4; i++)
            {
                var step = await db.Database.SqlQuery<VerifyRow>($"""SELECT checked AS "Checked", verified_through AS "VerifiedThrough", broken_at AS "BrokenAt", complete AS "Complete" FROM kernel.verify_audit_chain(25000)""").FirstAsync(ct);
                if (step.Complete)
                {
                    break;
                }
            }

            var view = await ChainViewAsync(db, ct);
            AuditWriter.Add(db, ctx, "audit.chain_verify", "audit_log", null, null, new { view.VerifiedThrough, view.BrokenAt }, view.BrokenAt is null ? "success" : "failed", environmentScoped: false);
            await db.SaveChangesAsync(ct);
            return view;
        }, ct);

    public Task<AuditPolicyView> PolicyAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireAsync(db, ctx, PlatformActions.AuditRead, fresh: false, ct);
            return await PolicyViewAsync(db, ct);
        }, ct);

    /// <summary>Changes how long audit entries are kept (365 to 3,650 days). Shortening it also needs permission administration.</summary>
    public Task<AuditPolicyView> SetRetentionAsync(RequestContext ctx, int days, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var perms = await RequireAsync(db, ctx, PlatformActions.AuditManage, fresh: true, ct);
            if (days is < MinRetentionDays or > MaxRetentionDays)
            {
                throw new KernelException("invalid_retention", $"Keep audit entries {MinRetentionDays} to {MaxRetentionDays} days.", 400, "retentionDays");
            }

            var row = await db.AuditPolicies.AsTracking().FirstOrDefaultAsync(ct);
            var current = row?.RetentionDays ?? DefaultRetentionDays;
            if (days < current && !perms.Can(PlatformActions.PermissionsAdmin))
            {
                throw KernelException.Forbidden("Keeping audit entries for less time needs permission administration.");
            }

            if (row is null)
            {
                row = new AuditPolicyRow { TenantId = ctx.TenantId };
                db.AuditPolicies.Add(row);
            }

            row.RetentionDays = days;
            row.UpdatedBy = ctx.PrincipalId;
            row.UpdatedAt = clock.UtcNow;
            AuditWriter.Add(db, ctx, "audit.policy_change", "audit_policy", null, null, new { from = current, to = days }, environmentScoped: false);
            await db.SaveChangesAsync(ct);
            return await PolicyViewAsync(db, ct);
        }, ct);

    public Task<AuditPolicyView> PlaceHoldAsync(RequestContext ctx, string reason, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireAsync(db, ctx, PlatformActions.AuditManage, fresh: true, ct);
            var text = (reason ?? string.Empty).Trim();
            if (text.Length is 0 or > 500)
            {
                throw new KernelException("invalid_reason", "Say why the hold is placed (at most 500 characters).", 400, "reason");
            }

            var hold = new LegalHoldRow { TenantId = ctx.TenantId, Id = Guid.NewGuid(), Reason = text, CreatedBy = ctx.PrincipalId, CreatedAt = clock.UtcNow };
            db.LegalHolds.Add(hold);
            AuditWriter.Add(db, ctx, "audit.hold_place", "legal_hold", hold.Id.ToString(), null, new { reason = text }, environmentScoped: false);
            await db.SaveChangesAsync(ct);
            return await PolicyViewAsync(db, ct);
        }, ct);

    public Task<AuditPolicyView> ReleaseHoldAsync(RequestContext ctx, Guid id, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var perms = await RequireAsync(db, ctx, PlatformActions.AuditManage, fresh: true, ct);
            var hold = await db.LegalHolds.AsTracking().FirstOrDefaultAsync(h => h.Id == id, ct) ?? throw KernelException.NotFound("Legal hold");
            if (hold.CreatedBy != ctx.PrincipalId && !perms.Can(PlatformActions.PermissionsAdmin))
            {
                // Separation of duties: a hold someone else placed is released only by a permission administrator.
                throw KernelException.Forbidden("Only the person who placed this hold or a permission administrator can release it.");
            }

            if (hold.ReleasedAt is null)
            {
                hold.ReleasedAt = clock.UtcNow;
                hold.ReleasedBy = ctx.PrincipalId;
                AuditWriter.Add(db, ctx, "audit.hold_release", "legal_hold", id.ToString(), null, environmentScoped: false);
                await db.SaveChangesAsync(ct);
            }

            return await PolicyViewAsync(db, ct);
        }, ct);

    // ------------------------------------------------------------------ helpers

    private async Task<EffectivePermissions> RequireAsync(KernelDbContext db, RequestContext ctx, string action, bool fresh, CancellationToken ct) =>
        (await RequireWithConfigAsync(db, ctx, action, fresh, ct)).Perms;

    private async Task<(Kernel.Configuration.EffectiveConfig Config, EffectivePermissions Perms)> RequireWithConfigAsync(KernelDbContext db, RequestContext ctx, string action, bool fresh, CancellationToken ct)
    {
        if (ctx.AgentId is not null)
        {
            throw KernelException.Forbidden("AI agents cannot read or manage the audit log.");
        }

        var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var perms = await access.ForAsync(db, ctx, artifact.Config, ct, fresh);
        if (!perms.Can(action) && !perms.Can(PlatformActions.PermissionsAdmin))
        {
            throw KernelException.Forbidden($"This needs the {action} right.");
        }

        return (artifact.Config, perms);
    }

    private static IQueryable<AuditRow> Filter(IQueryable<AuditRow> q, AuditQuery query)
    {
        if (query.From is { } from)
        {
            q = q.Where(a => a.At >= from);
        }

        if (query.To is { } to)
        {
            q = q.Where(a => a.At <= to);
        }

        if (query.Actor is { } actor)
        {
            q = q.Where(a => a.Actor == actor);
        }

        if (!string.IsNullOrWhiteSpace(query.Operation))
        {
            var prefix = query.Operation.Trim();
            q = q.Where(a => a.Operation.StartsWith(prefix));
        }

        if (!string.IsNullOrWhiteSpace(query.TargetType))
        {
            q = q.Where(a => a.TargetType == query.TargetType);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetId))
        {
            q = q.Where(a => a.TargetId == query.TargetId);
        }

        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            q = q.Where(a => a.Outcome == query.Outcome);
        }

        if (query.Before is { } before)
        {
            q = q.Where(a => a.Id < before);
        }

        return q;
    }

    private static AuditEntryView View(AuditRow r, Dictionary<Guid, string> names, Kernel.Configuration.EffectiveConfig config, EffectivePermissions perms) =>
        new(r.Id, r.At, r.Actor is { } a ? names.GetValueOrDefault(a, "someone") : null, r.Actor, r.Agent is not null, r.Channel, r.Operation, r.TargetType, r.TargetId, r.Outcome, r.EnvironmentId is null, Redact(r, config, perms));

    /// <summary>
    /// Record entries keep old and new field values. Except for permission administrators (who see every field anyway),
    /// a value shows only when the viewer reads every record of the entity and the field is not hidden from them; the
    /// rest read "restricted", as on a record's own history.
    /// </summary>
    internal static JsonNode? Redact(AuditRow r, Kernel.Configuration.EffectiveConfig config, EffectivePermissions perms)
    {
        var details = JsonNode.Parse(r.Details);
        if (perms.Can(PlatformActions.PermissionsAdmin) || details?["changes"] is not JsonObject changes || config.FindEntity(r.TargetType) is not { } entity)
        {
            return details;
        }

        var readsAll = perms.Entity(entity.Id).Read == RecordScopes.All;
        foreach (var key in changes.Select(c => c.Key).ToList())
        {
            if (!readsAll || perms.FieldAccess(entity, key) == FieldAccessLevels.Hidden)
            {
                changes[key] = "restricted";
            }
        }

        return details;
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(KernelDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0 ? [] : await db.Principals.AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
    }

    private static async Task<AuditChainView> ChainViewAsync(KernelDbContext db, CancellationToken ct)
    {
        var h = await db.AuditChainHeads.AsNoTracking().FirstOrDefaultAsync(ct);
        return h is null
            ? new AuditChainView(0, 0, null, 0, null, null, null, false)
            : new AuditChainView(h.LastSeq, h.AnchorSeq, h.SealedAt, h.VerifiedThroughSeq, h.VerifiedAt, h.BrokenAtSeq, h.BrokenDetectedAt, h.VerifyCursor is not null);
    }

    private static async Task<AuditPolicyView> PolicyViewAsync(KernelDbContext db, CancellationToken ct)
    {
        var days = await db.AuditPolicies.AsNoTracking().Select(p => (int?)p.RetentionDays).FirstOrDefaultAsync(ct) ?? DefaultRetentionDays;
        var holds = await db.LegalHolds.AsNoTracking().OrderByDescending(h => h.CreatedAt).Take(50).ToListAsync(ct);
        var names = await NamesAsync(db, holds.SelectMany(h => new[] { (Guid?)h.CreatedBy, h.ReleasedBy }), ct);
        return new AuditPolicyView(days, MinRetentionDays, MaxRetentionDays, [.. holds.Select(h => new LegalHoldView(h.Id, h.Reason, names.GetValueOrDefault(h.CreatedBy, "someone"), h.CreatedAt, h.ReleasedBy is { } r ? names.GetValueOrDefault(r, "someone") : null, h.ReleasedAt))]);
    }

    /// <summary>Spreadsheets run cells starting with = + - @ | % (also after leading spaces, tab or return) as formulas: prefix them with a quote.</summary>
    internal static string Csv(string value)
    {
        var start = value.TrimStart(' ', '\t', '\r', '\n');
        var v = start.Length > 0 && start[0] is '=' or '+' or '-' or '@' or '|' or '%' || (value.Length > 0 && value[0] is '\t' or '\r') ? "'" + value : value;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    public sealed class HeadRow
    {
        public long AnchorSeq { get; set; }

        public string AnchorHash { get; set; } = "";

        public long LastSeq { get; set; }

        public string LastHash { get; set; } = "";
    }

    public sealed class LinkRow
    {
        public long Id { get; set; }

        public long? ChainSeq { get; set; }

        public string? PrevHash { get; set; }

        public string? Hash { get; set; }

        public string Canonical { get; set; } = "";
    }

    public sealed class VerifyRow
    {
        public int Checked { get; set; }

        public long VerifiedThrough { get; set; }

        public long? BrokenAt { get; set; }

        public bool Complete { get; set; }
    }
}
