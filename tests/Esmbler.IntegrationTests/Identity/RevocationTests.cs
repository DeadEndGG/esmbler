using System.Text.Json.Nodes;
using Esmbler.Assistant;
using Esmbler.IntegrationTests.Api;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.IntegrationTests.Mcp;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Identity;

/// <summary>
/// REQ-ACC-07, REQ-AUTHZ-006 (G1-M17): a revocation applies to requests, cached permission facts, queued jobs and AI
/// agent sessions. Sensitive actions read the graph directly; everything else keys its cache on authz_revision.
/// </summary>
[Collection(PlatformCollection.Name)]
[Trait("Requirement", "REQ-ACC-07")]
public class RevocationTests(PlatformFixture f) : IClassFixture<ApiTestsFactoryHolder>, IClassFixture<McpServerTests.Holder>
{
    private const string Ticket = "core.tickets.ticket";

    [Fact]
    public async Task Removing_group_membership_applies_to_the_next_request_despite_cached_facts()
    {
        var t = await f.NewTenantAsync("rv");
        var admin = f.As(t, "admin");
        var agent = f.As(t, "agent");
        var records = f.Get<RecordService>();

        // Warm the facts cache: the agent sees every ticket through the agent role of its groups.
        var before = await records.QueryAsync(agent, new RecordQuery { Entity = Ticket }, default);
        Assert.True(before.Total > 0);

        var identity = f.Get<IdentityService>();
        await identity.RemoveGroupMemberAsync(admin, t.Groups["service_desk"], t.Users["agent"], default);
        await identity.RemoveGroupMemberAsync(admin, t.Groups["network"], t.Users["agent"], default);

        // No role left: the very next list neither reuses the cached facts nor shows any ticket.
        var after = await Record(() => records.QueryAsync(agent, new RecordQuery { Entity = Ticket }, default));
        Assert.True(after is KernelException { Status: 403 } || after is RecordPage { Total: 0 }, $"agent still sees tickets: {after}");
    }

    [Fact]
    public async Task Revoked_role_blocks_publish_immediately()
    {
        var t = await f.NewTenantAsync("rp");
        var admin = f.As(t, "admin");
        var builder = f.As(t, "builder");
        var drafts = f.Get<DraftService>();
        var draft = await drafts.OpenAsync(builder, default);
        await drafts.ApplyAsync(builder, ConfigLifecycleTestsOps.Vendor(), draft.Version, "ui", default);
        var state = await drafts.CurrentAsync(builder, default);

        await f.Get<IdentityService>().RevokeRoleAsync(admin, "core.tickets.admin", t.Users["builder"], null, default);

        var ex = await Assert.ThrowsAsync<KernelException>(() => f.Get<PublishService>().PublishAsync(builder, state!.ArtifactHash!, state.PublishedRevision, default));
        Assert.Equal(403, ex.Status);
    }

    [Fact]
    public async Task Queued_job_is_reauthorized_and_fails_closed_when_its_owner_lost_the_right()
    {
        var t = await f.NewTenantAsync("rj");
        var admin = f.As(t, "admin");
        var env = t.Environments["prod"];
        var upgrades = f.Get<UpgradeService>();
        var started = await upgrades.StartAsync(admin, "core.tickets", "1.1.0", default);

        // A second administrator takes the tenant-admin role away from the one who queued the upgrade.
        await f.Get<IdentityService>().AssignRoleAsync(admin, Kernel.Configuration.PlatformModule.TenantAdminRole, t.Users["alex"], null, default);
        await f.Get<IdentityService>().RevokeRoleAsync(f.As(t, "alex"), Kernel.Configuration.PlatformModule.TenantAdminRole, t.Users["admin"], null, default);

        var runner = f.Get<JobRunner>();
        JobRow? job = null;
        for (var i = 0; i < 30 && job?.Status is null or "pending" or "running"; i++)
        {
            await runner.RunOnceAsync(default);
            job = await f.Get<Database>().InTenantAsync(t.TenantId, env, null, db => db.Jobs.FirstAsync(j => j.Kind == UpgradeService.JobKind, default), default);
        }

        Assert.Equal("failed", job!.Status);
        Assert.Contains("modules.manage", job.LastError, StringComparison.Ordinal);
        var upgrade = await upgrades.GetAsync(f.As(t, "alex"), started.Id, default);
        Assert.Equal("failed", upgrade.Status);
        var active = await f.Get<Database>().InTenantAsync(t.TenantId, env, null, db => f.Get<ConfigStore>().GetActiveAsync(db, t.TenantId, env, default), default);
        Assert.Contains(active.Lockfile, m => m.Id == "core.tickets" && m.Version == "1.0.0");
    }

    [Fact]
    public async Task Agent_session_is_cut_off_at_its_next_tool_call()
    {
        var t = await f.NewTenantAsync("ra");
        var slug = PlatformFixture.Slugs[t.TenantId];
        var (_, token) = await McpFactory.AgentAsync(ApiTestsFactoryHolder.Get(f), $"builder@{slug}.example", slug);
        await using var tools = new McpToolGateway(await McpServerTests.Holder.Get(f).ConnectAsync(token), new AssistantBudget());
        var first = await tools.CallAsync("describe_entity", new JsonObject { ["entity"] = Ticket }, default);
        Assert.False(first.IsError, first.Text);

        await f.Get<IdentityService>().RevokeRoleAsync(f.As(t, "admin"), "core.tickets.admin", t.Users["builder"], null, default);

        var next = await tools.CallAsync("open_draft", [], default);
        Assert.True(next.IsError, next.Text);
        Assert.Equal("forbidden", next.Json?["error"]?["code"]?.GetValue<string>());
    }

    private static async Task<object> Record<T>(Func<Task<T>> action)
    {
        try
        {
            return (await action())!;
        }
        catch (KernelException ex)
        {
            return ex;
        }
    }
}

internal static class ConfigLifecycleTestsOps
{
    public static Sdk.Overlay.OverlayOp Vendor() => new Sdk.Overlay.AddField
    {
        Entity = "core.tickets.ticket",
        Field = new Sdk.Specs.FieldSpec { Key = "tenant__vendor", Type = "short_text", Label = "Vendor" },
    };
}
