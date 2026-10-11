using Esmbler.Expressions;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

/// <summary>Mail intake checks (ADR 0023, REQ-BIZ-003).</summary>
public sealed partial class ConfigValidator
{
    private static void ValidateMailIntakes(SpecCatalog config, List<Diagnostic> d)
    {
        foreach (var intake in config.MailIntakes)
        {
            var entity = config.FindEntity(intake.Entity);
            if (entity is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Mail intake '{intake.Label}' uses unknown entity '{intake.Entity}'.", intake.Id, Expression.Suggest(intake.Entity, config.Entities.Select(e => e.Id))));
                continue;
            }

            void Field(string? key, string role, params string[] types)
            {
                if (key is null)
                {
                    return;
                }

                var field = entity.FindField(key);
                if (field is null || field.Deprecated)
                {
                    d.Add(Diagnostic.Error("config.mail_bad_field", $"Mail intake '{intake.Label}': {entity.Label} has no field '{key}' for the {role}.", intake.Id, Expression.Suggest(key, entity.Fields.Where(f => types.Contains(f.Type)).Select(f => f.Key))));
                }
                else if (!types.Contains(field.Type))
                {
                    d.Add(Diagnostic.Error("config.mail_bad_field", $"Mail intake '{intake.Label}': the {role} goes into a {string.Join(" or ", types)} field, but '{key}' is {field.Type}.", intake.Id, "Pick a field of the right type."));
                }
            }

            Field(intake.TitleField, "subject", FieldTypes.ShortText, FieldTypes.LongText);
            Field(intake.BodyField, "message text", FieldTypes.LongText, FieldTypes.RichText);
            Field(intake.RequesterField, "sender", FieldTypes.User);
            Field(intake.AddressField, "sender's address", FieldTypes.Email);
            foreach (var key in intake.Set.Keys)
            {
                if (entity.FindField(key) is not { Deprecated: false } field)
                {
                    d.Add(Diagnostic.Error("config.mail_bad_field", $"Mail intake '{intake.Label}' sets unknown field '{key}'.", intake.Id, Expression.Suggest(key, entity.Fields.Select(f => f.Key))));
                }
                else if (field.Type is FieldTypes.User or FieldTypes.Group or FieldTypes.AutoNumber or FieldTypes.Status || field.System || key == intake.RequesterField)
                {
                    d.Add(Diagnostic.Error("config.mail_bad_field", $"Mail intake '{intake.Label}' cannot set '{key}' to a constant: people, groups, numbers, status and system fields are set by the platform or the workflow.", intake.Id, "Set choice, text or toggle fields only."));
                }
            }

            var workflow = config.Workflows.Find(w => w.Entity == entity.Id);
            if (intake.ReopenTransition is { } transitionId && workflow?.FindTransition(transitionId) is null)
            {
                d.Add(Diagnostic.Error("config.not_found", $"Mail intake '{intake.Label}' reopens with unknown transition '{transitionId}'.", intake.Id, Expression.Suggest(transitionId, workflow?.Transitions.Select(t => t.Id) ?? [])));
            }

            if (intake.ReopenFrom.Count > 0 && intake.ReopenTransition is null)
            {
                d.Add(Diagnostic.Error("config.mail_reopen", $"Mail intake '{intake.Label}' lists states to reopen from but no reopen transition.", intake.Id, "Name the transition a reply runs, or clear the states."));
            }

            foreach (var state in intake.ReopenFrom.Concat(intake.FollowUpFrom))
            {
                if (workflow?.FindState(state) is null)
                {
                    d.Add(Diagnostic.Error("config.not_found", $"Mail intake '{intake.Label}' uses unknown state '{state}'.", intake.Id, Expression.Suggest(state, workflow?.States.Select(s => s.Id) ?? [])));
                }
            }

            if (intake.ReopenFrom.Intersect(intake.FollowUpFrom).FirstOrDefault() is { } both)
            {
                d.Add(Diagnostic.Error("config.mail_reopen", $"Mail intake '{intake.Label}': state '{both}' cannot both reopen and start a follow-up.", intake.Id, "Keep the state in one of the two lists."));
            }
        }
    }
}
