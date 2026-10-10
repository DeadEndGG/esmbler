using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;

namespace Esmbler.IntegrationTests.Configuration;

/// <summary>
/// Gate 1 configuration lifecycle: REQ-ACC-01 (tenant isolation of customization), REQ-ACC-02 (competing edits),
/// REQ-CFG-007 (approval bound to hash), REQ-CFG-008 (pinned revisions), REQ-AUTHZ-008 (permission ops).
/// </summary>
[Collection(PlatformCollection.Name)]
public class ConfigLifecycleTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";
    private const string RecordView = "core.tickets.record";

    public static OverlayOp[] Customization() =>
    [
        new AddField { Entity = Ticket, Field = new FieldSpec { Key = "tenant__vendor", Type = "short_text", Label = "Vendor", MaxLength = 80 } },
        new AddField { Entity = Ticket, Field = new FieldSpec { Key = "tenant__justification", Type = "long_text", Label = "Justification" } },
        new AddRule
        {
            Rule = new RuleSpec
            {
                Id = "tenant__access_needs_justification",
                Kind = RuleKinds.Conditional,
                Entity = Ticket,
                Description = "Access requests need a justification",
                When = "record.category == 'access'",
                Actions = [new RuleActionSpec { Type = RuleActionTypes.Require, Field = "tenant__justification" }],
            },
        },
        new InsertNode
        {
            View = RecordView,
            Parent = "core.tickets.record.c_side",
            Anchor = new Anchor { After = "core.tickets.record.card_properties" },
            Node = new NodeSpec
            {
                Id = "tenant__n_vendor_card",
                Type = "layout.card",
                Props = new JsonObject { ["title"] = "Vendor" },
                Children = [new NodeSpec { Id = "tenant__n_vendor_field", Type = "field", Props = new JsonObject { ["field"] = "tenant__vendor" } }],
            },
        },
    ];

    private DraftService Drafts => f.Get<DraftService>();

    private PublishService Publisher => f.Get<PublishService>();

    private async Task<(RequestContext Admin, int Revision)> CustomizeAndPublishAsync(ProvisionedTenant t)
    {
        var admin = f.As(t, "admin");
        var draft = await Drafts.OpenAsync(admin, default);
        var version = draft.Version;
        foreach (var op in Customization())
        {
            version = (await Drafts.ApplyAsync(admin, op, version, "ui", default)).Version;
        }

        var state = await Drafts.CurrentAsync(admin, default);
        var result = await Publisher.PublishAsync(admin, state!.ArtifactHash!, state.PublishedRevision, default);
        return (admin, result.Revision);
    }

    [Fact]
    public async Task Tenant_A_customization_leaves_tenant_B_unchanged()
    {
        var a = await f.NewTenantAsync("ta");
        var b = await f.NewTenantAsync("tb");
        var experience = f.Get<ExperienceService>();
        var bBefore = SpecJson.Serialize((await experience.ViewAsync(f.As(b, "admin"), RecordView, default)).View);

        await CustomizeAndPublishAsync(a);

        var aView = await experience.ViewAsync(f.As(a, "admin"), RecordView, default);
        Assert.NotNull(aView.View.Layout.Find("tenant__n_vendor_card"));
        Assert.Contains(aView.Entity.Entity.Fields, x => x.Key == "tenant__vendor");
        var bAfter = SpecJson.Serialize((await experience.ViewAsync(f.As(b, "admin"), RecordView, default)).View);
        Assert.Equal(bBefore, bAfter);

        var records = f.Get<RecordService>();
        var accessTicket = new JsonObject { ["title"] = "Access to payroll", ["category"] = "access", ["requester"] = a.Users["dana"].ToString() };
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => records.CreateAsync(f.As(a, "agent"), Ticket, accessTicket, null, null, default));
        Assert.Contains(ex.Errors, e => e.Field == "tenant__justification" && e.Code == "required");

        var bTicket = new JsonObject { ["title"] = "Access to payroll", ["category"] = "access", ["requester"] = b.Users["dana"].ToString() };
        var created = await records.CreateAsync(f.As(b, "agent"), Ticket, bTicket, null, null, default);
        Assert.Equal("access", created.Data["category"]!.GetValue<string>());
        var bad = await Assert.ThrowsAsync<ValidationFailedException>(() => records.CreateAsync(f.As(b, "agent"), Ticket, new JsonObject { ["title"] = "Vendor field", ["tenant__vendor"] = "Dell" }, null, null, default));
        Assert.Contains(bad.Errors, e => e.Code == "unknown_field");
    }

    [Fact]
    public async Task Competing_edits_and_stale_publishes_are_rejected_with_details()
    {
        var t = await f.NewTenantAsync("tc");
        var admin = f.As(t, "admin");
        var builder = f.As(t, "builder");
        var draft = await Drafts.OpenAsync(admin, default);
        var first = await Drafts.ApplyAsync(admin, Customization()[0], draft.Version, "ui", default);

        var conflict = await Assert.ThrowsAsync<KernelException>(() => Drafts.ApplyAsync(builder, Customization()[1], draft.Version, "mcp", default));
        Assert.Equal("draft_conflict", conflict.Code);
        Assert.Contains("Vendor", System.Text.Json.JsonSerializer.Serialize(conflict.Details));

        var second = await Drafts.ApplyAsync(builder, Customization()[1], first.Version, "mcp", default);
        var state = await Drafts.CurrentAsync(admin, default);

        var stale = await Assert.ThrowsAsync<KernelException>(() => Publisher.PublishAsync(admin, "sha256:not-what-is-in-the-draft", state!.PublishedRevision, default));
        Assert.Equal("approval_stale", stale.Code);
        var wrongRevision = await Assert.ThrowsAsync<KernelException>(() => Publisher.PublishAsync(admin, state!.ArtifactHash!, state.PublishedRevision - 1, default));
        Assert.Equal("publish_conflict", wrongRevision.Code);

        var published = await Publisher.PublishAsync(admin, state!.ArtifactHash!, state.PublishedRevision, default);
        Assert.Equal(state.PublishedRevision + 1, published.Revision);
        Assert.Equal(2, published.Steps);
        _ = second;
    }

    [Fact]
    public async Task Invalid_operations_are_refused_with_actionable_errors_and_undo_respects_dependencies()
    {
        var t = await f.NewTenantAsync("td");
        var admin = f.As(t, "admin");
        var draft = await Drafts.OpenAsync(admin, default);
        var bad = await Assert.ThrowsAsync<KernelException>(() => Drafts.ApplyAsync(admin, new InsertNode
        {
            View = RecordView,
            Parent = "core.tickets.record.c_side",
            Node = new NodeSpec { Id = "tenant__n_x", Type = "field", Props = new JsonObject { ["field"] = "tenant__missing" } },
        }, draft.Version, "mcp", default));
        Assert.Equal("layout.unknown_field", bad.Code);
        Assert.Contains("tenant__missing", bad.Message);

        var v = draft.Version;
        foreach (var op in Customization())
        {
            v = (await Drafts.ApplyAsync(admin, op, v, "ui", default)).Version;
        }

        var undo = await Assert.ThrowsAsync<KernelException>(() => Drafts.SetUndoneAsync(admin, 1, true, v, default));
        Assert.Equal("draft.undo_has_dependents", undo.Code);
        Assert.Contains("step 4", undo.Message);

        var afterUndo = await Drafts.SetUndoneAsync(admin, 4, true, v, default);
        Assert.True(afterUndo.Steps.Single(s => s.Seq == 4).Undone);
        var afterUndo1 = await Drafts.SetUndoneAsync(admin, 1, true, afterUndo.Version, default);
        Assert.DoesNotContain(afterUndo1.Changes, c => c.Id == $"{Ticket}/tenant__vendor");
    }

    [Fact]
    public async Task A_batch_is_one_change_applied_whole_and_undone_together()
    {
        // REQ-WS-005: one request (a field plus its card placement and rule) is one reviewable change.
        var t = await f.NewTenantAsync("tbatch");
        var admin = f.As(t, "admin");
        var draft = await Drafts.OpenAsync(admin, default);
        var ops = Customization();

        // A batch with an invalid step records nothing.
        var broken = ops.Take(2).Append(new InsertNode { View = RecordView, Parent = "core.tickets.record.c_side", Node = new NodeSpec { Id = "tenant__n_bad", Type = "field", Props = new JsonObject { ["field"] = "tenant__missing" } } }).ToList();
        var refused = await Assert.ThrowsAsync<KernelException>(() => Drafts.ApplyBatchAsync(admin, broken, draft.Version, "mcp", default));
        Assert.Equal("layout.unknown_field", refused.Code);
        var unchanged = await Drafts.CurrentAsync(admin, default);
        Assert.Equal((draft.Version, 0), (unchanged!.Version, unchanged.Steps.Count));

        var applied = await Drafts.ApplyBatchAsync(admin, ops, draft.Version, "mcp", default);
        Assert.Equal(draft.Version + 1, applied.Version);
        Assert.Equal(ops.Length, applied.Steps.Count);
        Assert.All(applied.Draft.Steps, s => Assert.Equal(applied.Batch, s.Batch));

        // Any single step can still be undone alone when nothing depends on it (REQ-LIVE-003): the card placement.
        var last = applied.Steps[^1].Seq;
        var single = await Drafts.SetUndoneAsync(admin, last, true, applied.Version, default);
        Assert.Equal([last], single.Steps.Where(s => s.Undone).Select(s => s.Seq));
        single = await Drafts.SetUndoneAsync(admin, last, false, single.Version, default);

        // Undoing the change undoes every step of it; redoing it brings all of it back.
        var undone = await Drafts.SetUndoneAsync(admin, 2, true, single.Version, default, wholeChange: true);
        Assert.All(undone.Steps, s => Assert.True(s.Undone));
        Assert.DoesNotContain(undone.Changes, c => c.Id == $"{Ticket}/tenant__vendor");
        var redone = await Drafts.SetUndoneAsync(admin, 1, false, undone.Version, default, wholeChange: true);
        Assert.All(redone.Steps, s => Assert.False(s.Undone));
    }

    [Fact]
    public async Task Agents_request_publish_but_only_humans_publish_and_permissions_need_admin()
    {
        var t = await f.NewTenantAsync("te");
        var identity = f.Get<IdentityService>();
        var agentId = await identity.EnsureAgentAsync(t.Users["builder"], t.TenantId, default);
        var agent = f.As(t, "builder", agent: agentId, scopes: new HashSet<string>(AgentScopes.Builder));
        var draft = await Drafts.OpenAsync(agent, default);
        var applied = await Drafts.ApplyAsync(agent, Customization()[0], draft.Version, "mcp", default);

        var perm = await Assert.ThrowsAsync<KernelException>(() => Drafts.ApplyAsync(agent, new SetFieldAccess { PermissionSet = "core.tickets.ps_requester", Entity = Ticket, Field = "tenant__vendor", Access = "hidden" }, applied.Version, "mcp", default));
        Assert.Equal(403, perm.Status);

        var request = await Publisher.RequestAsync(agent, default);
        Assert.True(request.RequestedByAgent);
        var agentPublish = await Assert.ThrowsAsync<KernelException>(() => Publisher.PublishAsync(agent, request.ArtifactHash, 1, default));
        Assert.Equal(403, agentPublish.Status);

        var builder = f.As(t, "builder");
        var pending = await Publisher.PendingAsync(builder, default);
        Assert.NotNull(pending);
        var result = await Publisher.PublishAsync(builder, pending!.ArtifactHash, 1, default);
        Assert.Equal(2, result.Revision);

        // The tickets admin (config.publish without permissions.admin) cannot publish a permission change.
        var d2 = await Drafts.OpenAsync(builder, default);
        var perm2 = await Assert.ThrowsAsync<KernelException>(() => Drafts.ApplyAsync(builder, new SetFieldAccess { PermissionSet = "core.tickets.ps_requester", Entity = Ticket, Field = "tenant__vendor", Access = "hidden" }, d2.Version, "ui", default));
        Assert.Equal(403, perm2.Status);
        var admin = f.As(t, "admin");
        var a2 = await Drafts.ApplyAsync(admin, new SetFieldAccess { PermissionSet = "core.tickets.ps_requester", Entity = Ticket, Field = "tenant__vendor", Access = "hidden" }, d2.Version, "ui", default);
        Assert.True(a2.Draft.PermissionImpact);
    }

    [Fact]
    public async Task Form_submitted_under_an_incompatible_older_revision_gets_a_conflict()
    {
        var t = await f.NewTenantAsync("tf");
        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        var ticket = await records.CreateAsync(agent, Ticket, new JsonObject { ["title"] = "Pinned revision", ["requester"] = t.Users["dana"].ToString() }, 1, null, default);
        await CustomizeAndPublishAsync(t);
        var ex = await Assert.ThrowsAsync<KernelException>(() => records.UpdateAsync(agent, Ticket, ticket.Id, ticket.Revision, new JsonObject { ["priority"] = "p2" }, 1, null, default));
        Assert.Equal("config_conflict", ex.Code);
        var ok = await records.UpdateAsync(agent, Ticket, ticket.Id, ticket.Revision, new JsonObject { ["priority"] = "p2" }, 2, null, default);
        Assert.Equal(2, ok.ConfigRevision);
    }

    [Fact]
    public async Task Rollback_restores_an_earlier_configuration_as_a_new_revision()
    {
        var t = await f.NewTenantAsync("tg");
        var (admin, revision) = await CustomizeAndPublishAsync(t);
        var result = await Publisher.RollbackAsync(admin, 1, revision, default);
        Assert.Equal(revision + 1, result.Revision);
        var view = await f.Get<ExperienceService>().ViewAsync(admin, RecordView, default);
        Assert.Null(view.View.Layout.Find("tenant__n_vendor_card"));
        var history = await Publisher.RevisionsAsync(admin, default);
        Assert.Equal("rollback", history[0].Source);
    }
}
