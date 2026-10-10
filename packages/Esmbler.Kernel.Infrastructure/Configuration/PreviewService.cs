using System.Text.Json.Nodes;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Records;
using Esmbler.Kernel.Tenancy;
using Esmbler.Platform.Rules;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Configuration;

public sealed record DraftViewSpec(ViewClientSpec Spec, Guid? DraftId, int DraftVersion, IReadOnlyList<string> ChangedTargets, IReadOnlyList<DraftStep> Steps, bool IsDraft);

public sealed record CustomizationView(string Element, string Label, string Kind);

public sealed record SimulationResult(FieldStatesView States, IReadOnlyList<string> MatchedRules, IReadOnlyList<FieldError> Violations, IReadOnlyList<string> Explanations);

/// <summary>
/// Draft previews (REQ-LIVE-001): the page as the draft would render it for this viewer, with the elements each
/// step touched, a synthetic sample record, and rule simulation. Previews never write records, send notifications,
/// or call external systems (REQ-AISAFE-002).
/// </summary>
public sealed class PreviewService(Database database, ConfigStore configs, DraftService drafts, AccessService access)
{
    public Task<DraftViewSpec> ViewAsync(RequestContext ctx, string viewId, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (config, perms, draft, compiled) = await ResolveAsync(db, ctx, ct);
            var spec = ExperienceService.ForView(config, viewId, perms, compiled?.Published.Revision ?? 0);
            if (compiled is null)
            {
                return new DraftViewSpec(spec, null, 0, [], [], false);
            }

            var state = await drafts.StateAsync(db, ctx, draft!, ct);
            var changed = compiled.Commands.Where(c => !c.Undone).SelectMany(c => c.Targets).Distinct(StringComparer.Ordinal).ToList();
            return new DraftViewSpec(spec, draft!.Id, draft.Version, changed, state.Steps, true);
        }, ct);

    /// <summary>A synthetic record shaped by the draft's entity definition (never real data).</summary>
    public Task<RecordView> SampleAsync(RequestContext ctx, string entityId, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (config, perms, _, _) = await ResolveAsync(db, ctx, ct);
            var entity = config.FindEntity(entityId) ?? throw KernelException.NotFound($"Entity '{entityId}'");
            var group = await db.Groups.OrderBy(g => g.Key).Select(g => new { g.Id, g.Name }).FirstOrDefaultAsync(ct);
            var data = new JsonObject();
            var display = new Dictionary<string, string>(StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            foreach (var field in entity.Fields.Where(f => !Sdk.Overlay.Namespaces.IsTenantOwned(f.Key) || f.Default is not null))
            {
                JsonNode? value = field.Type switch
                {
                    FieldTypes.ShortText when field.Key == entity.TitleField => "Sample: printer on floor 3 shows a paper jam",
                    FieldTypes.ShortText => field.Default?.DeepClone(),
                    FieldTypes.RichText or FieldTypes.LongText => field.Key == "description" ? "<p>This is sample content so you can see how the page looks. Real records are not shown in draft mode.</p>" : null,
                    FieldTypes.AutoNumber => $"{field.Prefix}-000000",
                    FieldTypes.Status => config.WorkflowFor(entity.Id)?.InitialState,
                    FieldTypes.SingleSelect or FieldTypes.Priority => field.Default?.DeepClone() ?? (field.ChoiceList is { } cl ? config.FindChoiceList(cl)?.Options.FirstOrDefault(o => !o.Hidden)?.Id : null),
                    FieldTypes.Integer => field.Default?.DeepClone() ?? (field.Min is { } m ? (long)m : 0),
                    FieldTypes.Toggle => field.Default?.DeepClone() ?? false,
                    FieldTypes.DateTime when field.System => FieldNormalizer.FormatInstant(now),
                    FieldTypes.Email => "sample.person@example.com",
                    FieldTypes.User => ctx.PrincipalId.ToString(),
                    FieldTypes.Group => group?.Id.ToString(),
                    _ => field.Default?.DeepClone(),
                };
                if (value is not null)
                {
                    data[field.Key] = value;
                }

                if (field.Type == FieldTypes.User)
                {
                    display[field.Key] = "Sample person";
                }
                else if (field.Type == FieldTypes.Group && group is not null)
                {
                    display[field.Key] = group.Name;
                }
            }

            var fieldAccess = perms.FieldAccessMap(entity);
            var states = RuleEvaluator.Evaluate(config, entity, perms.Variables(entity, data, null, FieldNormalizer.FormatInstant(now)));
            var wf = config.WorkflowFor(entity.Id);
            return new RecordView
            {
                Id = Guid.Empty,
                Entity = entity.Id,
                Number = data[entity.Fields.Find(f => f.Type == FieldTypes.AutoNumber)?.Key ?? string.Empty]?.GetValue<string>(),
                Revision = 0,
                ConfigRevision = 0,
                Data = new JsonObject(data.Where(kv => fieldAccess.GetValueOrDefault(kv.Key) != FieldAccessLevels.Hidden).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))),
                Display = display,
                Access = new RecordAccessView(true, false, fieldAccess),
                Transitions = wf is null ? [] : Platform.Workflow.WorkflowHook.Available(wf, data, perms.Roles).Select(t => new TransitionView(t.Id, t.Label, t.Tone, t.RequiredFields)).ToList(),
                FieldStates = new FieldStatesView(states.Required.ToList(), states.ReadOnly.ToList(), states.Hidden.ToList(), states.Options, states.Messages.Select(m => new RuleMessageView(m.RuleId, m.Tone, m.Text, m.Field)).ToList()),
                CreatedAt = now,
                UpdatedAt = now,
            };
        }, ct);

    /// <summary>"What happens if category = access?" Evaluates rules against hypothetical values. No side effects.</summary>
    public Task<SimulationResult> SimulateAsync(RequestContext ctx, string entityId, JsonObject data, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (config, perms, _, _) = await ResolveAsync(db, ctx, ct);
            var entity = config.FindEntity(entityId) ?? throw KernelException.NotFound($"Entity '{entityId}'");
            var vars = perms.Variables(entity, data, null, FieldNormalizer.FormatInstant(DateTimeOffset.UtcNow));
            var states = RuleEvaluator.Evaluate(config, entity, vars);
            var violations = RuleEvaluator.Validate(config, entity, perms.Variables(entity, data, null, null)).ToList();
            foreach (var field in entity.Fields.Where(f => (f.Required || states.Required.Contains(f.Key)) && !f.System && f.Type != FieldTypes.AutoNumber && data[f.Key] is null))
            {
                violations.Add(new FieldError(field.Key, "required", $"{field.Label} is required."));
            }

            var explanations = states.Matched.Select(id =>
            {
                var rule = config.FindRule(id)!;
                var effects = string.Join(", ", rule.Actions.Select(a => a.Type == RuleActionTypes.Message ? $"shows \"{a.Message}\"" : $"{a.Type.Replace('_', ' ')} {entity.FindField(a.Field ?? string.Empty)?.Label ?? a.Field}"));
                return $"{rule.Description ?? rule.Id}: when {rule.When ?? "always"} → {effects} (applies on every form and the API).";
            }).ToList();
            return new SimulationResult(
                new FieldStatesView(states.Required.ToList(), states.ReadOnly.ToList(), states.Hidden.ToList(), states.Options, states.Messages.Select(m => new RuleMessageView(m.RuleId, m.Tone, m.Text, m.Field)).ToList()),
                states.Matched,
                violations,
                explanations);
        }, ct);

    /// <summary>The configuration a builder is working on: the open draft if any, else the published one.</summary>
    public Task<(EffectiveConfig Config, EffectivePermissions Perms, int? DraftVersion, int PublishedRevision)> WorkingConfigAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (config, perms, draft, compiled) = await ResolveAsync(db, ctx, ct);
            var published = compiled?.Published.Revision ?? (await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct)).Revision;
            return (config, perms, draft?.Version, published);
        }, ct);

    /// <summary>Elements this workspace customized, with plain labels (REQ-CFG-011). Builders only.</summary>
    public async Task<IReadOnlyList<CustomizationView>> CustomizationsAsync(RequestContext ctx, CancellationToken ct)
    {
        var (config, _, _, _) = await WorkingConfigAsync(ctx, ct);
        return config.Customized.Select(key => new CustomizationView(key, Describe(config, key), config.Provenance.GetValueOrDefault(key, Composer.TenantLayer) == Composer.TenantLayer ? "added" : "changed")).ToList();
    }

    private static string Describe(EffectiveConfig config, string key)
    {
        if (key.Split('/') is [var entityId, var fieldKey] && config.FindEntity(entityId) is { } entity)
        {
            return entity.FindField(fieldKey) is { } f ? $"{entity.Label} field \"{f.Label}\"" : $"{entity.Label} field {fieldKey}";
        }

        if (config.Rules.Find(r => r.Id == key) is { } rule)
        {
            return $"Rule \"{rule.Description ?? rule.Id}\"";
        }

        if (config.FindChoiceList(key) is { } list)
        {
            return $"Choice list \"{list.Label}\"";
        }

        foreach (var view in config.Views)
        {
            if (view.Layout.Find(key) is { } node)
            {
                var title = node.StringProp("title") ?? node.StringProp("text") ?? node.StringProp("field");
                return $"{view.Label}: {node.Type.Split('.').Last().Replace('_', ' ')}{(title is null ? string.Empty : $" \"{title}\"")}";
            }
        }

        return key;
    }

    private async Task<(EffectiveConfig Config, EffectivePermissions Perms, DraftRow? Draft, CompiledDraft? Compiled)> ResolveAsync(KernelDbContext db, RequestContext ctx, CancellationToken ct)
    {
        var published = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var publishedPerms = await access.ForAsync(db, ctx, published.Config, ct); // also validates the agent, if any
        var userPerms = ctx.AgentId is null ? publishedPerms : await access.ForAsync(db, ctx with { AgentId = null, Scopes = null }, published.Config, ct);
        if (!userPerms.Can(PlatformActions.ConfigEdit) || !ctx.HasScope(AgentScopes.ConfigRead))
        {
            throw KernelException.Forbidden("Draft previews and configuration discovery are available to builders.");
        }

        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.Status == "open", ct);
        if (draft is null)
        {
            return (published.Config, publishedPerms, null, null);
        }

        var compiled = await drafts.CompileAsync(db, ctx, draft, ct);
        var config = compiled.Result.Config;
        var perms = await access.ForAsync(db, ctx, config, ct);
        return (config, perms, draft, compiled);
    }
}
