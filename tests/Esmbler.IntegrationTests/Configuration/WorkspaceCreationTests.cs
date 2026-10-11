using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Metrics;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Configuration;

/// <summary>
/// REQ-WS-001, REQ-WS-002, REQ-WS-006, REQ-ACC-15 (G2-M9b): a signed-in person creates a complete workspace from a
/// starter by answering only the questions without defaults; it starts empty with labeled preview figures; creation is
/// limited per person and can be turned off.
/// </summary>
[Collection(PlatformCollection.Name)]
public class WorkspaceCreationTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";

    // Self-service creation is off by default (ADR 0020); these tests turn it on explicitly.
    private WorkspaceService Workspaces => Service(enabled: true, limit: 5);

    private WorkspaceService Service(bool enabled, int limit) => new(f.Get<ModuleCatalog>(), f.Get<ProvisioningService>(), f.Get<Database>(), new WorkspaceCreationPolicy(enabled, limit), f.Get<Kernel.Configuration.ConfigCompiler>());

    private async Task<RequestContext> EnterAsync(Guid principal, Guid tenantId)
    {
        var choice = (await f.Get<IdentityService>().TenantsForAsync(principal, default)).Single(t => t.TenantId == tenantId);
        return new RequestContext { TenantId = tenantId, EnvironmentId = choice.Environments.Single(e => e.Key == "prod").EnvironmentId, PrincipalId = principal };
    }

    [Fact]
    public void Starters_ask_only_questions_without_defaults_and_list_the_connected_surfaces()
    {
        var ticketing = Assert.Single(Workspaces.Starters(), s => s.Id == "ticketing");
        var question = Assert.Single(ticketing.Questions);
        Assert.Equal("service_desk", question.Id);
        Assert.Equal(["forms", "explorer", "dashboard", "navigation", "permissions", "catalog"], ticketing.Surfaces.Select(s => s.Kind));
        Assert.Contains(ticketing.Surfaces.Single(s => s.Kind == "dashboard").Items, i => i.StartsWith("Overview: Open, Unassigned, SLA breached", StringComparison.Ordinal));
    }

    [Fact]
    public void Persona_preview_shows_what_each_role_would_see_before_anything_is_created()
    {
        // REQ-WS-001 step 5: "preview as an agent or a requester", from the same configuration and permission model.
        var personas = Workspaces.Personas("ticketing");
        var agent = personas.Single(p => p.Role == "core.tickets.agent");
        var requester = personas.Single(p => p.Role == "core.tickets.requester");
        IReadOnlyList<string> Items(PersonaPreview p, string kind) => p.Surfaces.FirstOrDefault(s => s.Kind == kind)?.Items ?? [];

        Assert.Contains(Items(agent, "records"), r => r.StartsWith("Tickets: sees all tickets, creates them, edits all", StringComparison.Ordinal));
        Assert.Contains(Items(requester, "records"), r => r.StartsWith("Tickets: sees their own tickets, creates them", StringComparison.Ordinal));

        // Team figures are for agents; fields hidden from requesters are listed; requesters get no extra rights.
        Assert.Contains(Items(agent, "dashboard"), d => d.Contains("Workload by team", StringComparison.Ordinal) && d.Contains("Time to acknowledge", StringComparison.Ordinal));
        Assert.DoesNotContain(Items(requester, "dashboard"), d => d.Contains("Workload by team", StringComparison.Ordinal));
        Assert.Contains("Ticket: Labels", Items(requester, "hidden"));
        Assert.Empty(Items(agent, "hidden"));
        Assert.Empty(Items(requester, "rights"));
        Assert.Contains("Export lists and figures", Items(personas.Single(p => p.Role == "core.tickets.admin"), "rights"));
        Assert.Throws<KernelException>(() => Workspaces.Personas("nope"));
    }

    [Fact]
    public async Task A_new_admin_creates_a_ticketing_workspace_that_starts_empty_with_labeled_previews_and_drills_into_real_tickets()
    {
        var home = await f.NewTenantAsync("wsc", "1.6.0");
        var person = home.Users["quinn"];
        var created = await Workspaces.CreateAsync(person, "Quinn", new CreateWorkspaceRequest("Riverside IT", "ticketing", new Dictionary<string, string> { ["service_desk"] = "Riverside Support" }), default);
        Assert.Matches("^riverside-it-[a-z0-9]{5}$", created.Slug);

        var admin = await EnterAsync(person, created.TenantId);
        var app = await f.Get<ExperienceService>().AppAsync(admin, default);
        Assert.Contains(app.Dashboards!, d => d.View == "core.tickets.dashboard");
        Assert.Contains(app.Actions, a => a == "permissions.admin");
        var teams = await f.Get<IdentityService>().GroupsAsync(admin, default);
        Assert.Contains(teams, g => g.Name == "Riverside Support");

        // Empty: labeled previews, no drill, no record data.
        var metrics = f.Get<MetricService>();
        var empty = await metrics.DashboardAsync(admin, "core.tickets.dashboard", "UTC", default);
        Assert.True(empty.Empty);
        Assert.All(empty.Metrics, m => Assert.True(m.Preview && m.Drill is null && m.Buckets.All(b => b.Drill is null)));
        Assert.Equal(0, (await f.Get<RecordService>().QueryAsync(admin, new RecordQuery { Entity = Ticket, PageSize = 1 }, default)).Total);

        // The first ticket turns previews into real figures that drill into exactly that ticket.
        await f.Get<RecordService>().CreateAsync(admin, Ticket, new JsonObject { ["title"] = "Printer offline", ["requester"] = person.ToString(), ["impact"] = "low" }, null, null, default);
        var real = await metrics.DashboardAsync(admin, "core.tickets.dashboard", "UTC", default);
        Assert.False(real.Empty);
        var open = real.Metrics.Single(m => m.Id == "core.tickets.metric.open");
        Assert.Equal((false, 1L), (open.Preview, open.Count));
        var listed = await f.Get<RecordService>().QueryAsync(admin, new RecordQuery { Entity = Ticket, Filters = open.Drill!.Filters }, default);
        Assert.Equal("Printer offline", Assert.Single(listed.Items).Data["title"]!.GetValue<string>());

        // Isolation: the person's original workspace does not see the new one's ticket.
        var before = await f.Get<RecordService>().QueryAsync(f.As(home, "agent"), new RecordQuery { Entity = Ticket, Filters = ["record.title == 'Printer offline'"] }, default);
        Assert.Equal(0, before.Total);
    }

    [Fact]
    public async Task Creation_is_limited_per_person_validated_and_can_be_turned_off()
    {
        var home = await f.NewTenantAsync("wsl", "1.6.0");
        var person = home.Users["riley"];
        var limited = Service(enabled: true, limit: 1);
        var answers = new Dictionary<string, string> { ["service_desk"] = "Desk" };

        var missing = await Assert.ThrowsAsync<KernelException>(() => limited.CreateAsync(person, "Riley", new CreateWorkspaceRequest("Team A", "ticketing", null), default));
        Assert.Equal("missing_answer", missing.Code);
        var unknown = await Assert.ThrowsAsync<KernelException>(() => limited.CreateAsync(person, "Riley", new CreateWorkspaceRequest("Team A", "crm", answers), default));
        Assert.Equal("unknown_starter", unknown.Code);

        await limited.CreateAsync(person, "Riley", new CreateWorkspaceRequest("Team A", "ticketing", answers), default);
        var over = await Assert.ThrowsAsync<KernelException>(() => limited.CreateAsync(person, "Riley", new CreateWorkspaceRequest("Team B", "ticketing", answers), default));
        Assert.Equal(("workspace_limit", 409), (over.Code, over.Status));

        var off = Service(enabled: false, limit: 5);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => off.CreateAsync(person, "Riley", new CreateWorkspaceRequest("Team C", "ticketing", answers), default))).Status);
        Assert.False(f.Get<WorkspaceService>().Policy.Enabled);

        // Only enabled people: the database refuses any other principal, e.g. an AI agent's.
        var agent = await f.Get<IdentityService>().EnsureAgentAsync(home.Users["admin"], home.TenantId, default);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Workspaces.CreateAsync(agent, "Agent", new CreateWorkspaceRequest("Agent space", "ticketing", answers), default))).Status);

        // Names lose control and bidi-override characters.
        var spoof = await Service(true, 5).CreateAsync(home.Users["jordan"], "Jordan", new CreateWorkspaceRequest("Acme\u202e moc.evil", "ticketing", answers), default);
        Assert.Equal("Acme moc.evil", spoof.Name);
    }

    [Fact]
    public async Task The_privileged_functions_fail_closed_and_only_the_creator_activates()
    {
        var home = await f.NewTenantAsync("wsf", "1.6.0");
        var person = home.Users["riley"];
        var db = f.Get<Database>();
        await Assert.ThrowsAnyAsync<Exception>(() => db.AnonymousAsync(x => x.Database.SqlQuery<bool>($"SELECT kernel.create_workspace({Guid.NewGuid()}, {"w-nulllimit"}, {"Null limit"}, {person}, {(int?)null}) AS \"Value\"").SingleAsync(), default));
        await Assert.ThrowsAnyAsync<Exception>(() => db.AnonymousAsync(x => x.Database.SqlQuery<bool>($"SELECT kernel.create_workspace({Guid.NewGuid()}, {"w-nullcreator"}, {"Null creator"}, {(Guid?)null}, {5}) AS \"Value\"").SingleAsync(), default));

        var created = await Workspaces.CreateAsync(person, "Riley", new CreateWorkspaceRequest("Activated", "ticketing", new Dictionary<string, string> { ["service_desk"] = "Desk" }), default);
        var other = await db.AnonymousAsync(x => x.Database.SqlQuery<bool>($"SELECT kernel.activate_workspace({created.TenantId}, {home.Users["jordan"]}) AS \"Value\"").SingleAsync(), default);
        Assert.False(other);
        Assert.Contains(await f.Get<IdentityService>().TenantsForAsync(person, default), t => t.TenantId == created.TenantId);
    }

    [Fact]
    public async Task Parallel_creations_never_exceed_the_per_person_limit()
    {
        var home = await f.NewTenantAsync("wsp", "1.6.0");
        var limited = Service(enabled: true, limit: 1);
        var answers = new Dictionary<string, string> { ["service_desk"] = "Desk" };
        var attempts = await Task.WhenAll(Enumerable.Range(0, 3).Select(async i =>
        {
            try
            {
                await limited.CreateAsync(home.Users["riley"], "Riley", new CreateWorkspaceRequest($"Parallel {i}", "ticketing", answers), default);
                return "created";
            }
            catch (KernelException ex)
            {
                return ex.Code;
            }
        }));
        Assert.Equal(1, attempts.Count(a => a == "created"));
        Assert.All(attempts.Where(a => a != "created"), a => Assert.Equal("workspace_limit", a));
        Assert.Single(await f.Get<IdentityService>().TenantsForAsync(home.Users["riley"], default), t => t.Name.StartsWith("Parallel", StringComparison.Ordinal));
    }
}
