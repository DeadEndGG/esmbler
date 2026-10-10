using System.Text.Json.Nodes;
using Esmbler.Kernel;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Records;
using Esmbler.Kernel.Tenancy;
using Esmbler.Platform.Assignment;
using Esmbler.Platform.Rules;
using Esmbler.Platform.Workflow;
using Esmbler.UnitTests.Configuration;

namespace Esmbler.UnitTests.Records;

/// <summary>REQ-RULE-001/002/004, REQ-AUTHZ-002, REQ-BIZ-001: data-level enforcement on the write path.</summary>
public class WritePipelineTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Env = Guid.NewGuid();
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid AgentUser = Guid.NewGuid();
    private static readonly Guid ServiceDesk = Guid.NewGuid();
    private static readonly EffectiveConfig Config = CompilerFixture.Compile("1.0.0", OverlayTests.CanonicalCustomization()).Config;
    private static readonly Esmbler.Sdk.Specs.EntitySpec Ticket = Config.FindEntity("core.tickets.ticket")!;

    private static readonly RecordWriteEngine Engine = new(
        new FieldNormalizer(new PassThroughSanitizer()),
        [new WorkflowHook(), new AssignmentHook(new Groups()), new RulesHook()]);

    private static EffectivePermissions Perms(Guid user, string role, IReadOnlySet<string>? scopes = null, Guid? agent = null, params Guid[] groups) =>
        new(Config, new AuthorizationFacts
        {
            PrincipalId = user,
            Roles = new Dictionary<string, IReadOnlyList<string>> { [role] = ["direct"] },
            Groups = groups.ToHashSet(),
        }, new RequestContext { TenantId = Tenant, EnvironmentId = Env, PrincipalId = user, AgentId = agent, Scopes = scopes });

    private static EffectivePermissions RequesterPerms => Perms(Requester, "core.tickets.requester");

    private static EffectivePermissions AgentPerms => Perms(AgentUser, "core.tickets.agent", null, null, ServiceDesk);

    private static Task<RecordWriteContext> Create(EffectivePermissions perms, JsonObject changes) =>
        Engine.PrepareAsync(Config, Ticket, perms, WriteOperations.Create, null, changes, "2026-10-10T09:00:00.000Z", null, default);

    [Fact]
    public async Task Create_applies_defaults_initial_state_and_assignment()
    {
        var ctx = await Create(RequesterPerms, new JsonObject { ["title"] = "  Laptop will not boot  ", ["category"] = "hardware" });
        Assert.Equal("Laptop will not boot", ctx.Data["title"]!.GetValue<string>());
        Assert.Equal("new", ctx.Data["status"]!.GetValue<string>());
        Assert.Equal("p3", ctx.Data["priority"]!.GetValue<string>());
        Assert.Equal(Requester.ToString(), ctx.Data["requester"]!.GetValue<string>());
        Assert.Equal(ServiceDesk.ToString(), ctx.Data["assignment_group"]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_title_and_short_title_are_reported_together()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Create(RequesterPerms, new JsonObject { ["category"] = "hardware" }));
        Assert.Contains(ex.Errors, e => e.Field == "title" && e.Code == "required");
        var ex2 = await Assert.ThrowsAsync<ValidationFailedException>(() => Create(RequesterPerms, new JsonObject { ["title"] = "Hi" }));
        Assert.Contains(ex2.Errors, e => e.Code == "rule:core.tickets.rules.title_length");
        Assert.DoesNotContain(ex.Errors, e => e.Code.StartsWith("rule:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Requester_cannot_write_hidden_or_read_only_fields()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Create(RequesterPerms, new JsonObject { ["title"] = "Printer broken", ["labels"] = new JsonArray("vip"), ["status"] = "resolved" }));
        Assert.Contains(ex.Errors, e => e.Field == "labels" && e.Code == "forbidden_field");
        Assert.Contains(ex.Errors, e => e.Field == "status" && e.Code == "read_only" && e.Suggestion!.Contains("transition"));
        Assert.Equal("hidden", FieldAccessLevels(RequesterPerms, "estimated_effort"));
    }

    [Fact]
    public async Task Tenant_conditional_rule_requires_justification_for_access_requests()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Create(RequesterPerms, new JsonObject { ["title"] = "Payroll access", ["category"] = "access" }));
        Assert.Contains(ex.Errors, e => e.Field == "tenant__justification" && e.Code == "required");
        var ok = await Create(RequesterPerms, new JsonObject { ["title"] = "Payroll access", ["category"] = "access", ["tenant__justification"] = "Moved to Finance" });
        Assert.Equal("access", ok.Data["category"]!.GetValue<string>());
    }

    [Fact]
    public async Task Resolve_requires_notes_and_sets_resolved_at()
    {
        var created = await Create(AgentPerms, new JsonObject { ["title"] = "VPN keeps dropping", ["requester"] = Requester.ToString() });
        var prior = created.Data;
        prior["status"] = "in_progress";
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Engine.PrepareAsync(Config, Ticket, AgentPerms, WriteOperations.Transition, prior, [], "2026-10-10T10:00:00.000Z", "resolve", default));
        Assert.Contains(ex.Errors, e => e.Field == "resolution_notes" && e.Code == "required");

        var resolved = await Engine.PrepareAsync(Config, Ticket, AgentPerms, WriteOperations.Transition, prior, new JsonObject { ["resolution_notes"] = "Replaced router" }, "2026-10-10T10:00:00.000Z", "resolve", default);
        Assert.Equal("resolved", resolved.Data["status"]!.GetValue<string>());
        Assert.Equal("2026-10-10T10:00:00.000Z", resolved.Data["resolved_at"]!.GetValue<string>());
        Assert.Equal("in_progress", resolved.FromState);
    }

    [Fact]
    public async Task Invalid_transition_and_role_restrictions_are_enforced()
    {
        var created = await Create(RequesterPerms, new JsonObject { ["title"] = "Need a monitor" });
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Engine.PrepareAsync(Config, Ticket, AgentPerms, WriteOperations.Transition, created.Data, [], "2026-10-10T10:00:00.000Z", "confirm", default));
        Assert.Equal("invalid_transition", ex.Errors[0].Code);
        await Assert.ThrowsAsync<KernelException>(() =>
            Engine.PrepareAsync(Config, Ticket, RequesterPerms, WriteOperations.Transition, created.Data, [], "2026-10-10T10:00:00.000Z", "start", default));
    }

    [Fact]
    public async Task Closed_tickets_are_read_only_by_rule()
    {
        var closed = new JsonObject { ["title"] = "Old ticket", ["status"] = "closed", ["priority"] = "p3", ["requester"] = Requester.ToString(), ["resolution_notes"] = "done" };
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Engine.PrepareAsync(Config, Ticket, AgentPerms, WriteOperations.Update, closed, new JsonObject { ["title"] = "Changed title" }, "2026-10-10T10:00:00.000Z", null, default));
        Assert.Equal("read_only", Assert.Single(ex.Errors).Code);
    }

    [Fact]
    public void Record_level_access_follows_relationships()
    {
        var mine = new JsonObject { ["requester"] = Requester.ToString() };
        var other = new JsonObject { ["requester"] = Guid.NewGuid().ToString(), ["assignment_group"] = ServiceDesk.ToString() };
        Assert.True(RequesterPerms.CanRead(Ticket, mine));
        Assert.False(RequesterPerms.CanRead(Ticket, other));
        Assert.True(AgentPerms.CanRead(Ticket, other));
        var groupMemberRequester = Perms(Guid.NewGuid(), "core.tickets.requester", null, null, ServiceDesk);
        Assert.True(groupMemberRequester.CanRead(Ticket, other));
        var explain = RequesterPerms.Explain(Ticket, other);
        Assert.Contains(explain, r => r.Text.Contains("Cannot see this record"));
    }

    [Fact]
    public void Agent_gets_intersection_of_user_permissions_and_scopes()
    {
        var admin = Perms(AgentUser, "core.platform.tenant_admin", new HashSet<string> { AgentScopes.ConfigRead, AgentScopes.ConfigEdit, AgentScopes.RecordsRead }, Guid.NewGuid());
        Assert.True(admin.Can("config.edit"));
        Assert.False(admin.Can("config.publish"));
        Assert.False(admin.Can("permissions.admin"));
        Assert.False(admin.CanCreate(Ticket));
        Assert.True(admin.CanRead(Ticket, new JsonObject()));
        Assert.Equal("read", admin.FieldAccess(Ticket, "title"));

        var limitedUser = Perms(Requester, "core.tickets.requester", new HashSet<string>(Esmbler.Kernel.Tenancy.AgentScopes.All), Guid.NewGuid());
        Assert.False(limitedUser.Can("config.edit"));
        Assert.False(limitedUser.CanRead(Ticket, new JsonObject { ["requester"] = Guid.NewGuid().ToString() }));
    }

    [Fact]
    public async Task Normalization_rejects_bad_types_with_suggestions()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Create(AgentPerms, new JsonObject
        {
            ["title"] = "Valid title",
            ["priority"] = "p9",
            ["contact_email"] = "not-an-email",
            ["reopen_count"] = 2,
            ["categry"] = "network",
        }));
        Assert.Contains(ex.Errors, e => e.Field == "priority" && e.Code == "invalid_option");
        Assert.Contains(ex.Errors, e => e.Field == "contact_email" && e.Code == "invalid_email");
        Assert.Contains(ex.Errors, e => e.Field == "reopen_count" && e.Code == "read_only");
        Assert.Contains(ex.Errors, e => e.Field == "categry" && e.Suggestion == "category");
    }

    private static string FieldAccessLevels(EffectivePermissions p, string field) => p.FieldAccess(Ticket, field);

    private sealed class PassThroughSanitizer : IRichTextSanitizer
    {
        public string Sanitize(string html) => html;
    }

    private sealed class Groups : IGroupDirectory
    {
        public Task<Guid?> FindByKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(key == "service_desk" ? ServiceDesk : key == "network" ? Guid.Parse("00000000-0000-0000-0000-00000000000a") : null);
    }
}
