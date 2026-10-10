using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Records;
using Esmbler.Sdk.Specs;

namespace Esmbler.Platform.Assignment;

/// <summary>Resolves a tenant group key (e.g. "service_desk") to its ID within the current tenant.</summary>
public interface IGroupDirectory
{
    Task<Guid?> FindByKeyAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// Default assignment strategy (extension point core.tickets.strategy.assignment): on create, the first enabled
/// assignment rule whose condition matches sets the entity's first group access field, unless it is already set.
/// </summary>
public sealed class AssignmentHook(IGroupDirectory groups) : IRecordWriteHook
{
    public int Order => 200;

    public async ValueTask ApplyAsync(RecordWriteContext ctx, CancellationToken cancellationToken)
    {
        if (ctx.Operation != WriteOperations.Create)
        {
            return;
        }

        var groupField = ctx.Entity.AccessFields
            .Select(a => ctx.Entity.FindField(a.Field))
            .FirstOrDefault(f => f?.Type == FieldTypes.Group);
        if (groupField is null || ctx.Data[groupField.Key] is not null)
        {
            return;
        }

        var vars = ctx.Variables();
        foreach (var rule in ctx.Config.AssignmentRules.Where(r => r.Entity == ctx.Entity.Id && r.Enabled).OrderBy(r => r.Order).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            bool matched;
            try
            {
                matched = Expression.Compile(rule.When, EntitySchema.For(ctx.Entity)).EvaluateBool(vars);
            }
            catch (ExpressionException)
            {
                continue;
            }

            if (!matched || rule.Group is null)
            {
                continue;
            }

            if (await groups.FindByKeyAsync(rule.Group, cancellationToken) is { } groupId)
            {
                ctx.SystemSet(groupField.Key, JsonValue.Create(groupId.ToString()));
                ctx.Messages.Add(new RuleMessage(rule.Id, "info", $"Assigned to group '{rule.Group}' by {rule.Description ?? rule.Id}.", groupField.Key));
                return;
            }
        }
    }
}
