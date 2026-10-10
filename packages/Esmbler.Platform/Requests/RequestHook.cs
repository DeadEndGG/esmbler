using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel;
using Esmbler.Kernel.Records;
using Esmbler.Sdk.Specs;

namespace Esmbler.Platform.Requests;

/// <summary>
/// Catalog requests (ADR 0015, REQ-BIZ-004): on every write path, a request's catalog item must exist, be active
/// (on create) and target this entity, and never changes afterwards; its variables are normalized like fields of
/// their types, defaults applied, unknown keys and missing required answers rejected. Errors target
/// <c>variables.&lt;key&gt;</c> so forms can show them next to the question.
/// </summary>
public sealed class RequestHook(FieldNormalizer normalizer) : IRecordWriteHook
{
    public int Order => 150;

    public ValueTask ApplyAsync(RecordWriteContext ctx, CancellationToken cancellationToken)
    {
        if (ctx.Entity.Request is not { } binding)
        {
            return ValueTask.CompletedTask;
        }

        var itemId = ctx.Data[binding.CatalogItemField]?.GetValue<string>();
        var priorItem = ctx.Prior?[binding.CatalogItemField]?.GetValue<string>();
        if (ctx.Prior is not null && priorItem != itemId)
        {
            ctx.Errors.Add(new FieldError(binding.CatalogItemField, "read_only", "The catalog item of a request cannot change.", "Cancel this request and raise a new one."));
            return ValueTask.CompletedTask;
        }

        if (itemId is null)
        {
            return ValueTask.CompletedTask;
        }

        var item = ctx.Config.FindCatalogItem(itemId);
        if (item is null || item.RequestEntity != ctx.Entity.Id)
        {
            ctx.Errors.Add(new FieldError(binding.CatalogItemField, "unknown_catalog_item", $"'{itemId}' is not a catalog item for {ctx.Entity.PluralLabel.ToLowerInvariant()}."));
            return ValueTask.CompletedTask;
        }

        if (ctx.Prior is null && !item.Active)
        {
            ctx.Errors.Add(new FieldError(binding.CatalogItemField, "inactive_catalog_item", $"'{item.Label}' can no longer be requested.", "Choose another item from the catalog."));
            return ValueTask.CompletedTask;
        }

        var titleField = ctx.Entity.TitleField;
        if (ctx.Prior is null && ctx.Data[titleField] is null)
        {
            ctx.SystemSet(titleField, JsonValue.Create(item.Label));
        }

        if (ctx.Prior is null || ctx.Changed.Contains(binding.VariablesField))
        {
            NormalizeVariables(ctx, binding, item);
        }

        return ValueTask.CompletedTask;
    }

    private void NormalizeVariables(RecordWriteContext ctx, RequestBindingSpec binding, CatalogItemSpec item)
    {
        var input = ctx.Data[binding.VariablesField] as JsonObject ?? [];
        var output = new JsonObject();
        foreach (var (key, value) in input)
        {
            if (item.FindVariable(key) is null)
            {
                var suggestion = Expression.Suggest(key, item.Variables.Select(v => v.Key));
                ctx.Errors.Add(new FieldError($"{binding.VariablesField}.{key}", "unknown_variable", $"'{item.Label}' has no question '{key}'." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"), suggestion));
            }
        }

        foreach (var variable in item.Variables)
        {
            var field = variable.AsField();
            var target = $"{binding.VariablesField}.{variable.Key}";
            var raw = input[variable.Key];
            if (raw is null && ctx.Prior is null && variable.Default is not null)
            {
                raw = variable.Default.DeepClone();
            }

            var (normalized, error) = normalizer.Normalize(field, raw, ctx.Config);
            if (error is not null)
            {
                ctx.Errors.Add(error with { Field = target });
                continue;
            }

            if (normalized is null || (normalized is JsonArray a && a.Count == 0))
            {
                if (variable.Required)
                {
                    ctx.Errors.Add(new FieldError(target, "required", $"{variable.Label} is required."));
                }

                continue;
            }

            output[variable.Key] = normalized;
        }

        ctx.SystemSet(binding.VariablesField, output);
    }
}
