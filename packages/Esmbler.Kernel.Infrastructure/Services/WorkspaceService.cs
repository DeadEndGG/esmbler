using System.Security.Cryptography;
using System.Text;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
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
public sealed class WorkspaceService(ModuleCatalog catalog, ProvisioningService provisioning, Database database, WorkspaceCreationPolicy policy)
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
