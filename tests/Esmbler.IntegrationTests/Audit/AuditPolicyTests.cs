using System.Text;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Esmbler.IntegrationTests.Audit;

/// <summary>
/// REQ-AUD-001, REQ-NOT-005 (ADR 0022, KI-024): audit entries are sealed into one hash chain per workspace that
/// reveals tampering; retention removes only what the policy lets go (a sealed prefix of the chain, final deliveries, old
/// inbox items, resolved alerts) and nothing during a legal hold; export is audited, escapes formulas and is refused to
/// people without the right and to agents; the runtime role can never rewrite or preseal audit entries.
/// </summary>
[Collection(PlatformCollection.Name)]
public class AuditPolicyTests(PlatformFixture f)
{
    private AuditMaintenance Maintenance => f.Get<AuditMaintenance>();

    private AuditService Audit => f.Get<AuditService>();

    private string Superuser => new NpgsqlConnectionStringBuilder(f.Postgres.SuperuserConnectionString) { Database = "esmbler" }.ConnectionString;

    /// <summary>Runs SQL as the database superuser with triggers off: what someone with direct database access could do.</summary>
    private async Task AsSuperuserAsync(string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(Superuser);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SET session_replication_role = replica; " + sql, conn);
        for (var i = 0; i < args.Length; i++)
        {
            cmd.Parameters.AddWithValue($"p{i}", args[i]);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<ProvisionedTenant> TenantWithActivityAsync(string prefix, bool seal = true)
    {
        var t = await f.NewTenantAsync(prefix, "1.8.0");
        var records = f.Get<RecordService>();
        await records.CreateAsync(f.As(t, "agent"), "core.tickets.ticket", new JsonObject { ["title"] = "Audit me", ["requester"] = t.Users["dana"].ToString(), ["impact"] = "low" }, null, null, default);
        await records.CreateAsync(f.As(t, "agent", env: "sandbox"), "core.tickets.ticket", new JsonObject { ["title"] = "Sandbox too", ["requester"] = t.Users["dana"].ToString(), ["impact"] = "low" }, null, null, default);
        if (seal)
        {
            await Maintenance.SealAllAsync(default);
        }

        return t;
    }

    [Fact]
    public async Task Audit_entries_are_sealed_into_one_chain_per_workspace_that_reveals_tampering()
    {
        var t = await TenantWithActivityAsync("aud");
        var admin = f.As(t, "admin");
        var chain = await Audit.ChainAsync(admin, default);
        Assert.True(chain.Sealed > 2);
        var verified = await Audit.VerifyNowAsync(admin, default);
        Assert.Null(verified.BrokenAt);
        Assert.True(verified.VerifiedThrough >= chain.Sealed);

        // Both environments are in the one chain of the workspace.
        await using (var conn = new NpgsqlConnection(Superuser))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(DISTINCT environment_id) FROM kernel.audit_log WHERE tenant_id = @t AND chain_seq IS NOT NULL AND environment_id IS NOT NULL", conn);
            cmd.Parameters.AddWithValue("t", t.TenantId);
            Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        // Someone with direct database access edits a sealed entry: the next verification finds exactly where.
        await AsSuperuserAsync("UPDATE kernel.audit_log SET details = '{\"changes\":{}}' WHERE tenant_id = @p0 AND chain_seq = 2", t.TenantId);
        var broken = await Maintenance.VerifyAsync(t.TenantId, default);
        Assert.Equal(2, broken.BrokenAt);
        Assert.Equal(2, (await Audit.ChainAsync(admin, default)).BrokenAt);
        var alarm = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().CountAsync(a => a.Operation == "audit.chain_broken" && a.Outcome == "failed"), default);
        Assert.Equal(1, alarm);

        // Other workspaces' chains are untouched.
        var other = await TenantWithActivityAsync("aud2");
        Assert.Null((await Maintenance.VerifyAsync(other.TenantId, default)).BrokenAt);
    }

    [Fact]
    public async Task The_runtime_role_cannot_update_delete_or_preseal_audit_entries_even_with_context()
    {
        var t = await TenantWithActivityAsync("audr");
        await using var conn = new NpgsqlConnection(f.Postgres.AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{t.TenantId}', true), set_config('app.environment_id', '{t.Environments["prod"]}', true)", conn, tx))
        {
            await set.ExecuteNonQueryAsync();
        }

        foreach (var sql in new[]
        {
            "UPDATE kernel.audit_log SET outcome = 'denied'",
            "DELETE FROM kernel.audit_log",
            $"INSERT INTO kernel.audit_log (tenant_id, channel, operation, target_type, outcome, chain_seq) VALUES ('{t.TenantId}', 'api', 'x', 'x', 'success', 999999)",
            $"UPDATE kernel.audit_chain_heads SET last_seq = 0",
        })
        {
            await using var save = new NpgsqlCommand("SAVEPOINT s", conn, tx);
            await save.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
            await using var back = new NpgsqlCommand("ROLLBACK TO SAVEPOINT s", conn, tx);
            await back.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Retention_removes_only_a_verified_prefix_past_the_policy_never_tampered_entries_and_holds_pause_it()
    {
        // The workspace's first three entries were written two years ago (backdated before they were sealed).
        var t = await TenantWithActivityAsync("audp", seal: false);
        await AsSuperuserAsync("UPDATE kernel.audit_log SET at = at - interval '730 days' WHERE tenant_id = @p0 AND id IN (SELECT id FROM kernel.audit_log WHERE tenant_id = @p0 ORDER BY id LIMIT 3)", t.TenantId);
        await Maintenance.SealAllAsync(default);
        var admin = f.As(t, "admin");
        Assert.Equal(AuditService.DefaultRetentionDays, (await Audit.PolicyAsync(admin, default)).RetentionDays);
        Assert.Equal("invalid_retention", (await Assert.ThrowsAsync<KernelException>(() => Audit.SetRetentionAsync(admin, 30, default))).Code);
        await Audit.SetRetentionAsync(admin, 365, default);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Audit.SetRetentionAsync(f.As(t, "builder"), 400, default))).Status);

        // Nothing goes before the chain has been verified that far.
        await Maintenance.PurgeAsync(default);
        Assert.Equal(0, (await Audit.ChainAsync(admin, default)).AnchorSeq);
        await Audit.VerifyNowAsync(admin, default);

        // A legal hold keeps everything; a hold someone else placed is released only by a permission administrator.
        var held = await Audit.PlaceHoldAsync(admin, "Litigation 2026-17", default);
        await Maintenance.PurgeAsync(default);
        Assert.Equal(0, (await Audit.ChainAsync(admin, default)).AnchorSeq);

        // Released: the old prefix goes and the rest still verifies.
        await Audit.ReleaseHoldAsync(admin, held.Holds[0].Id, default);
        var purged = await Maintenance.PurgeAsync(default);
        Assert.Contains(purged, p => p.TenantId == t.TenantId && p.Kind == "audit" && p.Removed == 3);
        Assert.Equal(3, (await Audit.ChainAsync(admin, default)).AnchorSeq);
        await Maintenance.SealAllAsync(default);
        Assert.Null((await Audit.VerifyNowAsync(admin, default)).BrokenAt);
        var record = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().Where(a => a.Operation == "retention.purged" || a.Operation.StartsWith("audit.hold")).Select(a => a.Operation).ToListAsync(), default);
        Assert.Equal(["audit.hold_place", "audit.hold_release", "retention.purged"], record.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Entries_changed_to_look_old_are_reported_and_kept_never_purged()
    {
        var t = await TenantWithActivityAsync("audx");
        var admin = f.As(t, "admin");
        await Audit.SetRetentionAsync(admin, 365, default);
        await Audit.VerifyNowAsync(admin, default);

        // Someone with database access backdates sealed entries so retention would remove them.
        await AsSuperuserAsync("UPDATE kernel.audit_log SET at = at - interval '730 days' WHERE tenant_id = @p0 AND chain_seq <= 2", t.TenantId);
        var purged = await Maintenance.PurgeAsync(default);
        Assert.DoesNotContain(purged, p => p.TenantId == t.TenantId && p.Kind == "audit");
        var chain = await Audit.ChainAsync(admin, default);
        Assert.Equal((0L, 1L), (chain.AnchorSeq, chain.BrokenAt!.Value));
        var alarms = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().CountAsync(a => a.Operation == "audit.chain_broken"), default);
        Assert.Equal(1, alarms);

        // The daily verification agrees and does not raise the same alarm again.
        Assert.Equal(1, (await Maintenance.VerifyAsync(t.TenantId, default)).BrokenAt);
        Assert.Equal(1, await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().CountAsync(a => a.Operation == "audit.chain_broken"), default));
    }

    [Fact]
    public async Task People_without_permission_administration_never_see_values_hidden_from_them_in_the_audit_log()
    {
        var t = await TenantWithActivityAsync("audh");
        var admin = f.As(t, "admin");

        // Tickets admins (audit.read through their permission set) lose sight of the effort field.
        var drafts = f.Get<Kernel.Infrastructure.Configuration.DraftService>();
        var draft = await drafts.OpenAsync(admin, default);
        var applied = await drafts.ApplyAsync(admin, new Sdk.Overlay.SetFieldAccess { PermissionSet = "core.tickets.ps_admin", Entity = "core.tickets.ticket", Field = "effort_hours", Access = "hidden" }, draft.Version, "ui", default);
        await f.Get<Kernel.Infrastructure.Configuration.PublishService>().PublishAsync(admin, applied.Draft.ArtifactHash!, applied.Draft.PublishedRevision, default);
        await f.Get<RecordService>().CreateAsync(f.As(t, "agent"), "core.tickets.ticket", new JsonObject { ["title"] = "Effortful", ["requester"] = t.Users["dana"].ToString(), ["impact"] = "low", ["effort_hours"] = JsonValue.Create(987.25m) }, null, null, default);

        var query = new AuditQuery(null, null, null, "record.create", null, null, null, null, Limit: 200);
        var builder = f.As(t, "builder");
        var seen = (await Audit.SearchAsync(builder, query, default)).Items.Select(i => i.Details!.ToJsonString()).ToList();
        Assert.Contains(seen, d => d.Contains("\"effort_hours\":\"restricted\"", StringComparison.Ordinal));
        Assert.DoesNotContain(seen, d => d.Contains("987.25", StringComparison.Ordinal));
        Assert.Contains((await Audit.SearchAsync(admin, query, default)).Items, i => i.Details!.ToJsonString().Contains("987.25", StringComparison.Ordinal));

        // Exports render entries through the same redacting view; audit.read alone exports nothing, and the chain export
        // (unredacted by design) is for permission administrators only.
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Audit.ExportAsync(builder, "csv", query, Stream.Null, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Audit.ExportAsync(builder, "jsonl", query, Stream.Null, default))).Status);
    }

    [Fact]
    public async Task Old_deliveries_inbox_items_and_resolved_alerts_expire_but_live_ones_stay()
    {
        var t = await TenantWithActivityAsync("audn");
        var env = t.Environments["prod"];
        var now = DateTimeOffset.UtcNow;
        await f.Get<Database>().InTenantAsync(t.TenantId, env, null, async db =>
        {
            foreach (var (status, id) in new[] { ("sent", Guid.NewGuid()), ("queued", Guid.NewGuid()) })
            {
                db.NotificationDeliveries.Add(new NotificationDeliveryRow
                {
                    TenantId = t.TenantId,
                    EnvironmentId = env,
                    Id = id,
                    EventId = Guid.NewGuid(),
                    EventType = "x",
                    SubscriptionId = "s",
                    RouteId = "r",
                    TemplateId = "t",
                    ProfileId = "p",
                    ChannelType = "in_app",
                    Status = status,
                    IdempotencyKey = $"{id}",
                    Subject = status,
                    Body = "b",
                    ConfigRevision = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            db.Notifications.Add(new NotificationRow { TenantId = t.TenantId, EnvironmentId = env, Id = Guid.NewGuid(), TemplateId = "t", Recipient = t.Users["dana"], Subject = "read long ago", Body = "b", Channel = "in_app", CreatedAt = now, ReadAt = now, EventId = Guid.NewGuid() });
            db.Notifications.Add(new NotificationRow { TenantId = t.TenantId, EnvironmentId = env, Id = Guid.NewGuid(), TemplateId = "t", Recipient = t.Users["dana"], Subject = "unread", Body = "b", Channel = "in_app", CreatedAt = now, EventId = Guid.NewGuid() });
            db.Alerts.Add(new AlertRow { TenantId = t.TenantId, EnvironmentId = env, Id = Guid.NewGuid(), RuleId = "r", DedupKey = "k", Status = "resolved", Severity = "high", Title = "old", FirstEventId = Guid.NewGuid(), LastEventId = Guid.NewGuid(), OpenedAt = now, LastOccurredAt = now, ResolvedAt = now, ConfigRevision = 1, CreatedAt = now, UpdatedAt = now });
            db.Alerts.Add(new AlertRow { TenantId = t.TenantId, EnvironmentId = env, Id = Guid.NewGuid(), RuleId = "r", DedupKey = "k2", Status = "open", Severity = "high", Title = "live", FirstEventId = Guid.NewGuid(), LastEventId = Guid.NewGuid(), OpenedAt = now, LastOccurredAt = now, ConfigRevision = 1, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            return true;
        }, default);
        await AsSuperuserAsync("""
            UPDATE kernel.notification_deliveries SET updated_at = now() - interval '100 days', created_at = now() - interval '100 days' WHERE tenant_id = @p0;
            UPDATE kernel.notifications SET read_at = now() - interval '40 days', created_at = now() - interval '41 days' WHERE tenant_id = @p0 AND subject = 'read long ago';
            UPDATE kernel.alerts SET resolved_at = now() - interval '400 days' WHERE tenant_id = @p0 AND status = 'resolved';
            UPDATE kernel.alerts SET opened_at = now() - interval '400 days' WHERE tenant_id = @p0 AND status = 'open';
            """, t.TenantId);

        var purged = await Maintenance.PurgeAsync(default);
        Assert.Contains(purged, p => p.TenantId == t.TenantId && p.Kind == "deliveries" && p.Removed == 1);
        Assert.Contains(purged, p => p.TenantId == t.TenantId && p.Kind == "inbox" && p.Removed == 1);
        Assert.Contains(purged, p => p.TenantId == t.TenantId && p.Kind == "alerts" && p.Removed == 1);
        var left = await f.Get<Database>().InTenantAsync(t.TenantId, env, null, async db => (
            await db.NotificationDeliveries.Select(d => d.Status).ToListAsync(),
            await db.Notifications.Where(n => n.Subject == "unread" || n.Subject == "read long ago").Select(n => n.Subject).ToListAsync(),
            await db.Alerts.Select(a => a.Title).ToListAsync()), default);
        Assert.Equal(["queued"], left.Item1);
        Assert.Equal(["unread"], left.Item2);
        Assert.Equal(["live"], left.Item3);
    }

    [Fact]
    public async Task Export_is_audited_escapes_formulas_and_only_people_with_the_right_search_or_export()
    {
        var t = await TenantWithActivityAsync("aude");
        var records = f.Get<RecordService>();
        await records.CreateAsync(f.As(t, "agent"), "core.tickets.ticket", new JsonObject { ["title"] = "=HYPERLINK(\"http://evil\")", ["requester"] = t.Users["dana"].ToString(), ["impact"] = "low" }, null, null, default);
        await Maintenance.SealAllAsync(default);
        var admin = f.As(t, "admin");

        using var csv = new MemoryStream();
        await Audit.ExportAsync(admin, "csv", new AuditQuery(null, null, null, "record.", null, null, null, null), csv, default);
        var text = Encoding.UTF8.GetString(csv.ToArray());
        Assert.StartsWith("id,at,actor", text);
        Assert.Contains("record.create", text);
        Assert.DoesNotContain(",=", text); // a cell never starts with a formula

        using var jsonl = new MemoryStream();
        await Audit.ExportAsync(admin, "jsonl", new AuditQuery(null, null, null, null, null, null, null, null), jsonl, default);
        var lines = Encoding.UTF8.GetString(jsonl.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("esmbler-audit", lines[0]);
        var sealedLine = JsonNode.Parse(lines.First(l => l.Contains("\"chainSeq\":1", StringComparison.Ordinal)))!;
        Assert.NotNull(sealedLine["hash"]);
        Assert.NotNull(sealedLine["canonical"]);
        var exports = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().CountAsync(a => a.Operation == "audit.export"), default);
        Assert.Equal(2, exports);

        // Search pages backwards and stays in its workspace and environment.
        var page = await Audit.SearchAsync(admin, new AuditQuery(null, null, null, "record.", null, null, null, null, Limit: 1), default);
        Assert.Single(page.Items);
        Assert.NotNull(page.Next);
        Assert.DoesNotContain((await Audit.SearchAsync(f.As(f.Globex, "morgan"), new AuditQuery(null, null, null, null, null, null, null, null, Limit: 200), default)).Items, i => i.TargetId == page.Items[0].TargetId);

        // Agents never; people without the right never.
        var agentId = await f.Get<IdentityService>().EnsureAgentAsync(t.Users["admin"], t.TenantId, default);
        var ai = f.As(t, "admin", agent: agentId, scopes: new HashSet<string>(AgentScopes.All));
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Audit.SearchAsync(ai, new AuditQuery(null, null, null, null, null, null, null, null), default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Audit.ExportAsync(ai, "csv", new AuditQuery(null, null, null, null, null, null, null, null), Stream.Null, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Audit.ExportAsync(f.As(t, "agent"), "csv", new AuditQuery(null, null, null, null, null, null, null, null), Stream.Null, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Audit.PlaceHoldAsync(f.As(t, "builder"), "x", default))).Status);
        Assert.Equal("invalid_range", (await Assert.ThrowsAsync<KernelException>(() => Audit.ExportAsync(admin, "csv", new AuditQuery(DateTimeOffset.UtcNow.AddDays(-500), DateTimeOffset.UtcNow, null, null, null, null, null, null), Stream.Null, default))).Code);
        Assert.Equal("=x", AuditService.Csv("=x").TrimStart('\''));
        Assert.Equal("'=x", AuditService.Csv("=x"));
    }
}
