using System.Security.Cryptography;
using System.Text;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Metrics;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Modules;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Services;

/// <summary>A question the creation flow asks because it has no sensible default.</summary>
public sealed record StarterQuestion(string Id, string Label, string? Help, string? Example);

/// <summary>One kind of connected surface a starter generates (forms, explorer, dashboard, navigation, permissions).</summary>
public sealed record StarterSurface(string Kind, string Label, IReadOnlyList<string> Items);

public sealed record StarterView(string Id, string Name, string Description, string Module, string ModuleName, string Version, IReadOnlyList<string> Keywords, IReadOnlyList<StarterQuestion> Questions, IReadOnlyList<StarterSurface> Surfaces);

/// <summary>What one role sees in a workspace made from a starter (REQ-WS-001 step 5): navigation, records, hidden fields, figures and rights.</summary>
public sealed record PersonaPreview(string Role, string Label, string? Description, IReadOnlyList<StarterSurface> Surfaces);

public sealed record CreateWorkspaceRequest(string? Name, string? Starter, IReadOnlyDictionary<string, string>? Answers);

public sealed record CreatedWorkspace(Guid TenantId, string Slug, string Name);

/// <summary>Who may create workspaces and how many each (bound from platform options).</summary>
public sealed record WorkspaceCreationPolicy(bool Enabled, int MaxPerPerson);

/// <summary>
/// Guided workspace creation (REQ-WS-001, G2-M9b). A starter is a module release plus the teams it needs; the module
/// already connects forms, detail pages, explorer, dashboard, navigation and permissions, so creating a workspace is
/// one step: provision the tenant, install the release, create the teams named in the answers, and make the creator its
/// administrator. Only questions without a default are asked.
/// </summary>
public sealed class WorkspaceService(ModuleCatalog catalog, ProvisioningService provisioning, Database database, WorkspaceCreationPolicy policy, ConfigCompiler compiler)
{
    private const int MaxNameLength = 60;

    public WorkspaceCreationPolicy Policy => policy;

    public IReadOnlyList<StarterView> Starters() =>
        [.. catalog.Modules.SelectMany(m => m.Starters.Select(s => View(m, s)))];

    public async Task<CreatedWorkspace> CreateAsync(Guid principalId, string displayName, CreateWorkspaceRequest request, CancellationToken ct)
    {
        if (!policy.Enabled)
        {
            throw KernelException.Forbidden("Creating workspaces is turned off on this platform.", suggestion: "Ask a platform operator to create the workspace.");
        }

        var name = Clean(request.Name);
        if (name.Length is < 2 or > MaxNameLength)
        {
            throw new KernelException("invalid_name", $"Give the workspace a name of 2 to {MaxNameLength} characters.", 400, "name", "For example \"Riverside IT\".");
        }

        var (module, starter) = catalog.Modules.SelectMany(m => m.Starters.Select(s => (m, s))).FirstOrDefault(x => x.s.Id == request.Starter);
        if (starter is null)
        {
            var asked = request.Starter is { Length: > 40 } tooLong ? tooLong[..40] + "…" : request.Starter;
            throw new KernelException("unknown_starter", $"There is no starter '{asked}'.", 400, "starter", $"Choose one of: {string.Join(", ", Starters().Select(s => s.Id))}.");
        }

        var groups = new List<GroupSeed>();
        foreach (var group in starter.Groups)
        {
            var groupName = group.Name ?? group.Key;
            if (group.Question is not null)
            {
                var answer = Clean(request.Answers?.GetValueOrDefault(group.Key));
                if (string.IsNullOrEmpty(answer) || answer.Length > MaxNameLength)
                {
                    throw new KernelException("missing_answer", $"Answer \"{group.Question}\" (1 to {MaxNameLength} characters).", 400, group.Key, group.Example is null ? null : $"For example \"{group.Example}\".");
                }

                groupName = answer;
            }

            groups.Add(new GroupSeed(group.Key, groupName, group.Roles));
        }

        var creator = new UserSeed("creator", string.Empty, displayName, string.Empty, [PlatformModule.TenantAdminRole], [.. starter.Groups.Where(g => g.CreatorJoins).Select(g => g.Key)], principalId);
        var seed = new TenantSeed(Slug(name), name, [creator], groups, [(module.Id, module.Releases[^1].Version)], SampleData: false, CreatedBy: principalId, CreationLimit: policy.MaxPerPerson);
        var tenant = await provisioning.ProvisionAsync(seed, ct);

        var prod = tenant.Environments["prod"];
        await database.InTenantAsync(tenant.TenantId, prod, principalId, async db =>
        {
            var ctx = new RequestContext { TenantId = tenant.TenantId, EnvironmentId = prod, PrincipalId = principalId, Channel = "api" };
            AuditWriter.Add(db, ctx, "workspace.create", "tenant", tenant.TenantId.ToString(), null, new { starter = starter.Id, module = module.Id, teams = groups.Select(g => g.Key) }, environmentScoped: false);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

        // Only now does the workspace appear in the switcher; a run that failed earlier stays 'provisioning' and hidden.
        await database.AnonymousAsync(db => db.Database.SqlQuery<bool>($"SELECT kernel.activate_workspace({tenant.TenantId}, {principalId}) AS \"Value\"").SingleAsync(ct), ct);
        return new CreatedWorkspace(tenant.TenantId, seed.Slug, name);
    }

    /// <summary>
    /// Previews a starter as each of its roles would see it (REQ-WS-001 step 5), from the same configuration a new
    /// workspace gets and the same permission model (<see cref="EffectivePermissions"/>, <see cref="MetricService.Restricted"/>).
    /// Nothing is created and no records are read; the server decides what each role sees, never the page.
    /// </summary>
    public IReadOnlyList<PersonaPreview> Personas(string starterId)
    {
        var (module, _) = catalog.Modules.SelectMany(m => m.Starters.Select(s => (Module: m, Starter: s))).FirstOrDefault(x => x.Starter.Id == starterId);
        if (module is null)
        {
            throw KernelException.NotFound($"Starter '{starterId}'");
        }

        var compiled = compiler.Compile([catalog.Latest(PlatformModule.ModuleId), module.Releases[^1]], []);
        if (!compiled.Succeeded)
        {
            throw new KernelException("starter_invalid", "This starter's configuration does not compile.", 500);
        }

        var config = compiled.Config;
        var release = module.Releases[^1];
        return [.. release.Roles.Select(role => Persona(config, role))];
    }

    private static PersonaPreview Persona(EffectiveConfig config, RoleSpec role)
    {
        var principal = Guid.NewGuid();
        var facts = new AuthorizationFacts { PrincipalId = principal, Roles = new Dictionary<string, IReadOnlyList<string>> { [role.Id] = ["direct"] }, Groups = new HashSet<Guid>() };
        var context = new RequestContext { TenantId = Guid.Empty, EnvironmentId = Guid.Empty, PrincipalId = principal, Channel = "ui" };
        var perms = new EffectivePermissions(config, facts, context);
        var readable = config.Entities.Where(e => perms.Entity(e.Id).Read != RecordScopes.None).ToList();
        var listed = readable.Where(e => config.Views.Any(v => v.Kind == ViewKinds.List && v.Entity == e.Id)).ToList();
        var dashboards = config.Views.Where(v => v.Kind == ViewKinds.Dashboard && perms.Entity(v.Entity).Read != RecordScopes.None)
            .Select(v => (View: v, Figures: v.Layout.Descendants().Where(n => !n.Hidden).Select(n => n.StringProp("metric")).OfType<string>().Distinct(StringComparer.Ordinal)
                .Select(config.FindMetric).OfType<MetricSpec>()
                .Where(m => config.FindEntity(m.Entity) is { } e && !MetricService.Restricted(config, perms, e, m)).Select(m => m.Label).ToList()))
            .Where(d => d.Figures.Count > 0).ToList();
        static string Scope(string scope) => scope switch { RecordScopes.All => "all", RecordScopes.Related => "their own", _ => "no" };
        var records = readable.Select(e =>
        {
            var access = perms.Entity(e.Id);
            var can = new List<string> { $"sees {Scope(access.Read)} {e.PluralLabel.ToLowerInvariant()}" };
            if (access.Create)
            {
                can.Add("creates them");
            }

            if (access.Update != RecordScopes.None)
            {
                can.Add($"edits {Scope(access.Update)}");
            }

            return $"{e.PluralLabel}: {string.Join(", ", can)}";
        }).ToList();
        var hidden = readable.SelectMany(e => e.Fields.Where(f => !f.Hidden && perms.FieldAccess(e, f.Key) == FieldAccessLevels.Hidden).Select(f => $"{e.Label}: {f.Label}")).ToList();
        var rights = perms.Actions.Order(StringComparer.Ordinal).Select(a => a switch
        {
            PlatformActions.ConfigEdit => "Customize forms, lists and dashboards (draft)",
            PlatformActions.ConfigPublish => "Publish configuration changes",
            PlatformActions.PermissionsAdmin => "Administer people and permissions",
            PlatformActions.AuditRead => "Read the audit log",
            PlatformActions.RecordsExport => "Export lists and figures",
            PlatformActions.AlertsRespond => "Respond to every alert",
            PlatformActions.AlertsManage => "Manage alerts and maintenance windows",
            PlatformActions.NotificationsManage => "Manage notification channels",
            _ => a,
        }).ToList();
        List<StarterSurface> surfaces =
        [
            new("navigation", "Navigation", [.. dashboards.Select(d => d.View.Label).Concat(listed.Select(e => e.PluralLabel))]),
            new("records", "Records", records),
            new("dashboard", "Dashboard figures", [.. dashboards.Select(d => $"{d.View.Label}: {string.Join(", ", d.Figures)}")]),
            new("hidden", "Fields hidden from them", hidden),
            new("rights", "Other rights", rights),
        ];
        return new PersonaPreview(role.Id, role.Label, role.Description, [.. surfaces.Where(s => s.Items.Count > 0)]);
    }

    private static StarterView View(IModuleDefinition module, StarterSpec starter)
    {
        var release = module.Releases[^1];
        var dashboards = release.Views.Where(v => v.Kind == ViewKinds.Dashboard).ToList();
        var listed = release.Entities.Where(e => release.Views.Any(v => v.Kind == ViewKinds.List && v.Entity == e.Id)).ToList();
        var metricLabels = dashboards.SelectMany(d => d.Layout.Descendants()).Select(n => n.StringProp("metric")).OfType<string>().Distinct()
            .Select(id => release.Metrics.FirstOrDefault(m => m.Id == id)?.Label).OfType<string>().ToList();
        List<StarterSurface> surfaces =
        [
            new("forms", "Forms and detail pages", [.. release.Entities.Where(e => release.Views.Any(v => v.Entity == e.Id && v.Kind is ViewKinds.Record or ViewKinds.Form)).Select(e => e.Label)]),
            new("explorer", "Explorers", [.. listed.Select(e => $"{e.PluralLabel}: search, filters, sorting, columns, grouping, saved views")]),
            new("dashboard", "Dashboards", [.. dashboards.Select(d => $"{d.Label}: {string.Join(", ", metricLabels)}")]),
            new("navigation", "Navigation", [.. dashboards.Select(d => d.Label).Concat(listed.Select(e => e.PluralLabel))]),
            new("permissions", "Permissions", [.. release.Roles.Select(r => r.Description is null ? r.Label : $"{r.Label}: {r.Description}")]),
        ];
        if (release.CatalogItems.Count > 0)
        {
            surfaces.Add(new("catalog", "Service catalog", [.. release.CatalogItems.Select(i => i.Label)]));
        }

        var questions = starter.Groups.Where(g => g.Question is not null).Select(g => new StarterQuestion(g.Key, g.Question!, g.Help, g.Example)).ToList();
        return new StarterView(starter.Id, starter.Name, starter.Description, module.Id, module.Name, release.Version, starter.Keywords, questions, [.. surfaces.Where(s => s.Items.Count > 0)]);
    }

    /// <summary>Trims and drops control and format characters (e.g. bidi overrides) that could spoof names in the switcher.</summary>
    private static string Clean(string? value) =>
        new string([.. (value ?? string.Empty).Where(c => char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format))]).Trim();

    /// <summary>A URL-safe, unguessable-enough slug from the name: "Riverside IT" becomes "riverside-it-k3x9q".</summary>
    private static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }

        var core = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        core = core.Length == 0 || !char.IsAsciiLetter(core[0]) ? "w-" + core : core;
        core = core[..Math.Min(30, core.Length)].TrimEnd('-');
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var suffix = new string([.. RandomNumberGenerator.GetItems<char>(alphabet, 5)]);
        return $"{core}-{suffix}";
    }
}
