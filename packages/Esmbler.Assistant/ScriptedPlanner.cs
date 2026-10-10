using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Esmbler.Assistant;

/// <summary>
/// Deterministic planner for common builder requests (fields, conditional rules, callouts, cards, relabels,
/// options, undo, publish requests). Needs no API key, so CI, demos and air-gapped installs get a working
/// assistant; it calls exactly the same MCP tools as the model-driven planner. Unknown requests get a question
/// with examples instead of a guess.
/// </summary>
public sealed partial class ScriptedPlanner : IPlanner
{
    public string Name => "scripted";

    public async Task<string> RunAsync(AssistantRequest request, IToolGateway tools, IAssistantSink sink, AssistantBudget budget, CancellationToken ct)
    {
        var session = new Session(request, tools, sink, ct);
        var clauses = SplitClauses(request.Text);
        var done = new List<string>();
        foreach (var clause in clauses)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await HandleAsync(session, clause);
            if (outcome is null)
            {
                if (done.Count == 0)
                {
                    await session.EmitAsync(AssistantEvent.Types.Question, "I can add fields, rules, callouts, and cards, rename fields, add options, undo steps, and ask for a publish. What would you like to change?", options: Examples);
                    return "question";
                }

                await session.EmitAsync(AssistantEvent.Types.Message, $"I didn't understand \"{clause}\", so I left it out.");
                continue;
            }

            if (outcome.StartsWith('!'))
            {
                await session.EmitAsync(AssistantEvent.Types.Error, outcome[1..]);
                return "error";
            }

            done.Add(outcome);
        }

        var summary = done.Count == 1 ? done[0] : string.Join(" ", done.Select((d, i) => $"{i + 1}. {d}"));
        var tail = session.Changed ? " The changes are in your draft and visible on the page now; publish when you're happy." : string.Empty;
        await session.EmitAsync(AssistantEvent.Types.Done, summary + tail);
        return "done";
    }

    internal static readonly string[] Examples =
    [
        "Add a required \"Customer impact\" field to tickets and put it in the Details card",
        "When priority is P1, require an impact description",
        "Show a warning callout when a ticket is unassigned",
        "Rename Category to Request type",
    ];

    private static List<string> SplitClauses(string text)
    {
        var parts = ClauseSplit().Split(text.Trim()).Select(p => p.Trim().TrimEnd('.', '!', ';').Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? [text.Trim()] : parts;
    }

    private static async Task<string?> HandleAsync(Session s, string clause)
    {
        // 'Add a required "Customer impact" date field' reads as 'Add a required date field called "Customer impact"'.
        clause = LabelFirst().Replace(clause, "${type}field called ${label}", 1);
        Match m;
        if ((m = AddFieldPattern().Match(clause)).Success)
        {
            return await AddFieldAsync(s, m);
        }

        if ((m = CalloutPattern().Match(clause)).Success)
        {
            return await CalloutAsync(s, m);
        }

        if ((m = ConditionalPattern().Match(clause)).Success || (m = ConditionalTrailingPattern().Match(clause)).Success)
        {
            return await ConditionalAsync(s, m.Groups["verb"].Value, m.Groups["target"].Value, m.Groups["cond"].Value, m.Groups["neg"].Success);
        }

        if ((m = AddColumnPattern().Match(clause)).Success)
        {
            return await AddColumnAsync(s, m.Groups["field"].Value);
        }

        if ((m = AddCardPattern().Match(clause)).Success)
        {
            return await AddCardAsync(s, m);
        }

        if ((m = MakeRequiredPattern().Match(clause)).Success)
        {
            return await UpdateFieldAsync(s, m.Groups["field"].Value, new JsonObject { ["required"] = true }, f => $"{f} is now required.");
        }

        if ((m = MinLengthPattern().Match(clause)).Success)
        {
            return await MinLengthAsync(s, m);
        }

        if ((m = HidePattern().Match(clause)).Success)
        {
            var field = await s.ResolveFieldAsync(m.Groups["field"].Value);
            return field is null ? await s.UnknownFieldAsync(m.Groups["field"].Value) : await s.BuildAsync("hide_field", new JsonObject { ["entity"] = s.Entity, ["field"] = field.Key, ["hidden"] = !m.Groups["show"].Success }, $"{(m.Groups["show"].Success ? "Showed" : "Hid")} {field.Label}. Existing data is kept.");
        }

        if ((m = RenamePattern().Match(clause)).Success)
        {
            var to = Unquote(m.Groups["to"].Value);
            return await UpdateFieldAsync(s, m.Groups["field"].Value, new JsonObject { ["label"] = to }, f => $"Renamed {f} to {to}.");
        }

        if ((m = AddOptionPattern().Match(clause)).Success)
        {
            return await AddOptionAsync(s, m);
        }

        if (UndoPattern().IsMatch(clause))
        {
            return await UndoAsync(s);
        }

        if (PublishPattern().IsMatch(clause))
        {
            return await RequestPublishAsync(s);
        }

        if (DiffPattern().IsMatch(clause))
        {
            return await DiffAsync(s);
        }

        return null;
    }

    private static async Task<string> AddFieldAsync(Session s, Match m)
    {
        var label = Unquote(m.Groups["label"].Value.Trim());
        if (label.Length == 0)
        {
            return "!Tell me what to call the field, e.g. Add a \"Vendor\" field.";
        }

        var options = m.Groups["opts"].Success ? SplitList(m.Groups["opts"].Value) : [];
        var type = MapType(m.Groups["type"].Value.Trim(), label, options.Count > 0);
        var args = new JsonObject
        {
            ["entity"] = s.Entity,
            ["label"] = Capitalize(label),
            ["type"] = type,
            ["required"] = m.Groups["req"].Value.Trim().ToLowerInvariant() is "required" or "mandatory",
        };
        if (options.Count > 0)
        {
            args["choices"] = new JsonArray(options.Select(o => (JsonNode)JsonValue.Create(Capitalize(o))).ToArray());
        }

        if (type is "single_select" or "multi_select" && options.Count == 0)
        {
            return $"!A {type.Replace('_', ' ')} field needs options. Try: Add a dropdown field \"{label}\" with options Low, Medium, High.";
        }

        var card = await s.TargetCardAsync(m.Groups["card"].Success ? m.Groups["card"].Value : null);
        if (card is not null)
        {
            args["view"] = s.RecordView;
            args["place_in_view_node"] = card.Id;
            var last = card.LastChild;
            if (last is not null)
            {
                args["after"] = last.Id;
            }
        }

        var where = card is null ? string.Empty : $" and placed it in the {card.Title} card";
        var req = args["required"]!.GetValue<bool>() ? "required " : string.Empty;
        return await s.BuildAsync("add_field", args, $"Added a {req}{TypeLabel(type)} field \"{Capitalize(label)}\"{where}.", invalidates: true);
    }

    private static async Task<string> ConditionalAsync(Session s, string verb, string targetText, string condText, bool negate)
    {
        var cond = await ParseConditionAsync(s, condText);
        if (cond is null)
        {
            return $"!I couldn't turn \"{condText}\" into a condition. Try a field and a value, e.g. \"priority is P1\" or \"assignee is empty\".";
        }

        var expression = negate ? $"!({cond.Value.Expression})" : cond.Value.Expression;
        var action = verb.ToLowerInvariant() switch
        {
            "hide" => "hide",
            "show" => "show",
            "lock" => "read_only",
            _ => "require",
        };
        var target = await s.ResolveFieldAsync(targetText);
        var created = string.Empty;
        if (target is null)
        {
            if (action != "require")
            {
                return await s.UnknownFieldAsync(targetText);
            }

            // "require an impact description" for a field that doesn't exist yet: create it next to the condition's field.
            var label = Capitalize(StripArticles(targetText));
            var card = await s.CardContainingAsync(cond.Value.Field) ?? await s.TargetCardAsync(null);
            var add = new JsonObject { ["entity"] = s.Entity, ["label"] = label, ["type"] = MapType(string.Empty, label, false) };
            if (card is not null)
            {
                add["view"] = s.RecordView;
                add["place_in_view_node"] = card.Id;
                if (card.LastChild is { } last)
                {
                    add["after"] = last.Id;
                }
            }

            var result = await s.BuildAsync("add_field", add, $"Added a field \"{label}\"{(card is null ? string.Empty : $" in the {card.Title} card")}.", invalidates: true);
            if (result.StartsWith('!'))
            {
                return result;
            }

            created = result + " ";
            target = await s.ResolveFieldAsync(label);
            if (target is null)
            {
                return "!The new field didn't show up in the draft; please try again.";
            }
        }

        var verbText = action switch
        {
            "hide" => "hidden",
            "show" => "shown",
            "read_only" => "read-only",
            _ => "required",
        };
        var description = $"{target.Label} is {verbText} when {cond.Value.Phrase}";
        var args = new JsonObject
        {
            ["entity"] = s.Entity,
            ["description"] = description,
            ["when"] = expression,
            ["actions"] = new JsonArray(new JsonObject { ["type"] = action, ["field"] = target.Key }),
        };
        var rule = await s.BuildAsync("add_rule", args, $"Added a rule: {description}. It applies on every save (forms, API and AI), not only on this page.", invalidates: true);
        return rule.StartsWith('!') ? rule : created + rule;
    }

    private static async Task<string> CalloutAsync(Session s, Match m)
    {
        var cond = await ParseConditionAsync(s, m.Groups["cond"].Value);
        if (cond is null)
        {
            return $"!I couldn't turn \"{m.Groups["cond"].Value}\" into a condition. Try e.g. \"when the ticket is unassigned\".";
        }

        var tone = m.Groups["tone"].Value.ToLowerInvariant() switch
        {
            "critical" or "error" or "danger" => "critical",
            "success" => "success",
            "info" or "information" or "note" => "info",
            _ => "warning",
        };
        var text = m.Groups["text"].Success ? Unquote(m.Groups["text"].Value) : CalloutText(cond.Value);
        var view = await s.ViewAsync();
        if (view is null)
        {
            return $"!I couldn't read the record page layout: {s.ViewError}";
        }

        var selected = await s.SelectedContainerAsync();
        var header = selected ?? view.FindFirst(n => n.Type == "layout.column");
        if (header is null)
        {
            return "!The record page has no column to put the callout in.";
        }

        var anchor = header.Children.LastOrDefault(c => c.Type is "display.callout" or "display.heading" or "display.badge");
        var args = new JsonObject
        {
            ["view"] = s.RecordView,
            ["parent"] = header.Id,
            ["type"] = "display.callout",
            ["props"] = new JsonObject { ["tone"] = tone, ["text"] = text, ["visibleWhen"] = cond.Value.Expression },
        };
        if (anchor is not null)
        {
            args["after"] = anchor.Id;
        }

        return await s.BuildAsync("insert_node", args, $"Added a {tone} callout \"{text}\" that shows when {cond.Value.Phrase}.", invalidates: true);
    }

    private static async Task<string> AddCardAsync(Session s, Match m)
    {
        var title = Capitalize(Unquote(m.Groups["title"].Value));
        var fieldTexts = SplitList(m.Groups["fields"].Value);
        var keys = new List<string>();
        foreach (var text in fieldTexts)
        {
            var f = await s.ResolveFieldAsync(text);
            if (f is null)
            {
                return await s.UnknownFieldAsync(text);
            }

            keys.Add(f.Key);
        }

        var view = await s.ViewAsync();
        var column = await s.SelectedContainerAsync() ?? view?.Find("core.tickets.record.c_side") ?? view?.FindFirst(n => n.Type == "layout.column");
        if (column is null)
        {
            return "!I couldn't find a column to add the card to.";
        }

        var args = new JsonObject
        {
            ["view"] = s.RecordView,
            ["parent"] = column.Id,
            ["title"] = title,
            ["fields"] = new JsonArray(keys.Select(k => (JsonNode)JsonValue.Create(k)).ToArray()),
        };
        if (column.LastChild is { } last)
        {
            args["after"] = last.Id;
        }

        return await s.BuildAsync("add_card", args, $"Added a \"{title}\" card with {string.Join(", ", fieldTexts.Select(Capitalize))}.", invalidates: true);
    }

    private static async Task<string> AddColumnAsync(Session s, string fieldText)
    {
        var field = await s.ResolveFieldAsync(fieldText);
        if (field is null)
        {
            return await s.UnknownFieldAsync(fieldText);
        }

        var list = await s.ViewAsync(s.ListView);
        var table = list?.FindFirst(n => n.Type == "collection.data_table");
        if (table is null)
        {
            return "!I couldn't find the list's table.";
        }

        if (table.Children.Any(c => c.Field == field.Key))
        {
            return $"The list already shows {field.Label}.";
        }

        var args = new JsonObject
        {
            ["view"] = s.ListView,
            ["parent"] = table.Id,
            ["type"] = "collection.column",
            ["props"] = new JsonObject { ["field"] = field.Key },
        };
        if (table.LastChild is { } last)
        {
            args["after"] = last.Id;
        }

        return await s.BuildAsync("insert_node", args, $"Added a {field.Label} column to the list.", invalidates: true);
    }

    private static async Task<string> UpdateFieldAsync(Session s, string fieldText, JsonObject changes, Func<string, string> summary)
    {
        var field = await s.ResolveFieldAsync(fieldText);
        if (field is null)
        {
            return await s.UnknownFieldAsync(fieldText);
        }

        changes["entity"] = s.Entity;
        changes["field"] = field.Key;
        return await s.BuildAsync("update_field", changes, summary(field.Label), invalidates: true);
    }

    private static async Task<string> MinLengthAsync(Session s, Match m)
    {
        var field = await s.ResolveFieldAsync(m.Groups["field"].Value);
        if (field is null)
        {
            return await s.UnknownFieldAsync(m.Groups["field"].Value);
        }

        var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var args = new JsonObject
        {
            ["entity"] = s.Entity,
            ["kind"] = "validation",
            ["description"] = $"{field.Label} needs at least {n} characters",
            ["assert"] = $"!has(record.{field.Key}) || record.{field.Key}.size() >= {n}",
            ["message"] = $"{field.Label} needs at least {n} characters.",
            ["field"] = field.Key,
        };
        return await s.BuildAsync("add_rule", args, $"Added a validation rule: {field.Label} needs at least {n} characters. Existing records are only checked when they're next saved.");
    }

    private static async Task<string> AddOptionAsync(Session s, Match m)
    {
        var field = await s.ResolveFieldAsync(m.Groups["field"].Value);
        if (field is null)
        {
            return await s.UnknownFieldAsync(m.Groups["field"].Value);
        }

        if (field.ChoiceList is null)
        {
            return $"!{field.Label} isn't a choice field, so it has no options.";
        }

        var results = new List<string>();
        foreach (var label in SplitList(m.Groups["opt"].Value))
        {
            var r = await s.BuildAsync("add_choice_option", new JsonObject { ["choice_list"] = field.ChoiceList, ["label"] = Capitalize(label) }, $"Added \"{Capitalize(label)}\" to {field.Label}.", invalidates: true);
            if (r.StartsWith('!'))
            {
                return r;
            }

            results.Add(r);
        }

        return string.Join(" ", results);
    }

    private static async Task<string> UndoAsync(Session s)
    {
        var draft = await s.CallAsync("get_draft", []);
        var last = draft.Json?["steps"]?.AsArray().LastOrDefault(st => st?["undone"]?.GetValue<bool>() != true);
        if (last is null)
        {
            return "There is nothing to undo.";
        }

        var seq = last["seq"]!.GetValue<int>();
        return await s.BuildAsync("undo_step", new JsonObject { ["step"] = seq }, $"Undid step {seq}: {last["summary"]?.GetValue<string>()}.", invalidates: true);
    }

    private static async Task<string> RequestPublishAsync(Session s)
    {
        var validation = await s.CallAsync("validate_draft", []);
        var errors = (validation.Json?["diagnostics"]?.AsArray() ?? []).Where(d => d?["severity"]?.GetValue<string>() == "error").ToList();
        if (errors.Count > 0)
        {
            return "!The draft has errors to fix before it can be published: " + string.Join(" ", errors.Select(e => e?["message"]?.GetValue<string>()));
        }

        var r = await s.CallAsync("request_publish", []);
        if (r.IsError)
        {
            return "!" + r.ErrorMessage;
        }

        return "I asked for a publish. A builder reviews the exact changes and publishes with one click; I can't publish myself.";
    }

    private static async Task<string> DiffAsync(Session s)
    {
        var diff = await s.CallAsync("diff_draft", []);
        var changes = diff.Json?["changes"]?.AsArray() ?? [];
        if (changes.Count == 0)
        {
            return "The draft has no changes yet.";
        }

        return "The draft changes: " + string.Join("; ", changes.Select(c => $"{c?["change"]?.GetValue<string>()} {c?["kind"]?.GetValue<string>()} {c?["label"]?.GetValue<string>()}")) + ".";
    }

    private readonly record struct Condition(string Expression, string Phrase, string? Field);

    private static async Task<Condition?> ParseConditionAsync(Session s, string text)
    {
        var t = ConditionPrefix().Replace(text.Trim().TrimEnd('.'), string.Empty).Trim();
        if (UnassignedPattern().IsMatch(t))
        {
            var assignee = await s.ResolveFieldAsync("assignee");
            return assignee is null ? null : new Condition($"!has(record.{assignee.Key})", "the ticket is unassigned", assignee.Key);
        }

        Match m;
        if ((m = EmptyPattern().Match(t)).Success)
        {
            var f = await s.ResolveFieldAsync(m.Groups["field"].Value);
            if (f is null)
            {
                return null;
            }

            var empty = m.Groups["state"].Value.ToLowerInvariant() is "empty" or "blank" or "missing" or "not set";
            return new Condition(empty ? $"!has(record.{f.Key})" : $"has(record.{f.Key})", $"{f.Label} is {(empty ? "empty" : "set")}", f.Key);
        }

        if ((m = ComparePattern().Match(t)).Success)
        {
            var f = await s.ResolveFieldAsync(m.Groups["field"].Value);
            if (f is null)
            {
                return null;
            }

            var op = m.Groups["op"].Value.ToLowerInvariant() switch
            {
                var o when o.Contains("above", StringComparison.Ordinal) || o.Contains("over", StringComparison.Ordinal) || o.Contains("greater", StringComparison.Ordinal) || o.Contains("more", StringComparison.Ordinal) => ">",
                var o when o.Contains("below", StringComparison.Ordinal) || o.Contains("under", StringComparison.Ordinal) || o.Contains("less", StringComparison.Ordinal) => "<",
                _ => "==",
            };
            var n = m.Groups["n"].Value;
            return new Condition($"has(record.{f.Key}) && record.{f.Key} {op} {n}", $"{f.Label} {op} {n}", f.Key);
        }

        if ((m = EqualsPattern().Match(t)).Success)
        {
            var f = await s.ResolveFieldAsync(m.Groups["field"].Value);
            if (f is null)
            {
                return null;
            }

            var negated = m.Groups["not"].Success;
            var values = SplitList(m.Groups["value"].Value);
            var literals = new List<string>();
            var labels = new List<string>();
            foreach (var v in values)
            {
                var (literal, label) = Literal(f, v);
                if (literal is null)
                {
                    return null;
                }

                literals.Add(literal);
                labels.Add(label);
            }

            var expr = literals.Count == 1 ? $"record.{f.Key} {(negated ? "!=" : "==")} {literals[0]}" : $"{(negated ? "!" : string.Empty)}(record.{f.Key} in [{string.Join(", ", literals)}])";
            return new Condition(expr, $"{f.Label} is {(negated ? "not " : string.Empty)}{string.Join(" or ", labels)}", f.Key);
        }

        return null;
    }

    private static (string? Literal, string Label) Literal(FieldInfo f, string value)
    {
        var v = Unquote(value.Trim());
        if (f.Options.Count > 0)
        {
            var match = f.Options.FirstOrDefault(o => string.Equals(o.Id, v, StringComparison.OrdinalIgnoreCase) || string.Equals(o.Label, v, StringComparison.OrdinalIgnoreCase))
                ?? f.Options.FirstOrDefault(o => o.Label.StartsWith(v, StringComparison.OrdinalIgnoreCase) || Normalize(o.Label).Split(' ').Contains(Normalize(v)))
                ?? f.Options.FirstOrDefault(o => Normalize(o.Label).Contains(Normalize(v), StringComparison.Ordinal));
            return match is null ? (null, v) : ($"'{match.Id}'", match.Label);
        }

        if (f.Type == "toggle")
        {
            var yes = v.ToLowerInvariant() is "yes" or "true" or "on" or "checked";
            return (yes ? "true" : "false", yes ? "yes" : "no");
        }

        if (f.Type is "integer" or "decimal" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return (v, v);
        }

        return ($"'{v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}'", v);
    }

    private static string CalloutText(Condition c) =>
        c.Phrase == "the ticket is unassigned" ? "This ticket is unassigned. Pick an assignee so it gets worked on." : $"Heads up: {c.Phrase}.";

    private static string MapType(string word, string label, bool hasOptions)
    {
        var w = word.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal);
        if (hasOptions)
        {
            return w.Contains("multi", StringComparison.Ordinal) || w.Contains("tag", StringComparison.Ordinal) ? "multi_select" : "single_select";
        }

        var t = w switch
        {
            "" => null,
            "text" or "short text" or "single line" or "string" => "short_text",
            "long text" or "paragraph" or "multi line" or "multiline" or "notes" => "long_text",
            "rich text" => "rich_text",
            "number" or "integer" or "whole number" or "count" or "numeric" => "integer",
            "decimal" or "amount" or "currency" or "money" => "decimal",
            "date" => "date",
            "date time" or "datetime" or "timestamp" or "date and time" => "date_time",
            "toggle" or "checkbox" or "yes/no" or "yes no" or "boolean" or "flag" => "toggle",
            "dropdown" or "select" or "choice" or "picklist" or "single select" => "single_select",
            "multi select" or "multiselect" or "tags" => "multi_select",
            "email" => "email",
            "user" or "person" or "people" => "user",
            "group" or "team" => "group",
            _ => null,
        };
        if (t is not null)
        {
            return t;
        }

        var l = label.ToLowerInvariant();
        return l switch
        {
            _ when l.Contains("email", StringComparison.Ordinal) => "email",
            _ when l.EndsWith(" date", StringComparison.Ordinal) || l.StartsWith("date", StringComparison.Ordinal) || l.EndsWith(" on", StringComparison.Ordinal) => "date",
            _ when l.Contains("description", StringComparison.Ordinal) || l.Contains("notes", StringComparison.Ordinal) || l.Contains("justification", StringComparison.Ordinal) || l.Contains("details", StringComparison.Ordinal) || l.Contains("reason", StringComparison.Ordinal) => "long_text",
            _ when l.Contains("count", StringComparison.Ordinal) || l.StartsWith("number of", StringComparison.Ordinal) => "integer",
            _ when l.Contains("hours", StringComparison.Ordinal) || l.Contains("cost", StringComparison.Ordinal) || l.Contains("amount", StringComparison.Ordinal) => "decimal",
            _ => "short_text",
        };
    }

    private static string TypeLabel(string type) => type switch
    {
        "short_text" => "text",
        "long_text" => "long text",
        "single_select" => "choice",
        "multi_select" => "multi-choice",
        "date_time" => "date and time",
        _ => type.Replace('_', ' '),
    };

    internal static List<string> SplitList(string text) =>
        ListSplit().Split(text).Select(p => Unquote(p.Trim())).Where(p => p.Length > 0).ToList();

    internal static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && "\"'“‘".Contains(s[0], StringComparison.Ordinal) && "\"'”’".Contains(s[^1], StringComparison.Ordinal) ? s[1..^1].Trim() : s;
    }

    internal static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    internal static string StripArticles(string s) => ArticlePattern().Replace(s.Trim(), string.Empty).Replace(" field", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

    internal static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in StripArticles(Unquote(s)).ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Similarity between a phrase and a field label or key (1 = exact).</summary>
    internal static double Score(string phrase, string label, string key)
    {
        var p = Normalize(phrase);
        var l = Normalize(label);
        var k = key.Replace("tenant__", string.Empty, StringComparison.Ordinal).Replace('_', ' ');
        if (p == l || p == k)
        {
            return 1;
        }

        var pw = p.Split(' ').Where(w => w.Length > 2 || p.Length <= 3).Select(Stem).ToHashSet();
        var lw = l.Split(' ').Concat(k.Split(' ')).Select(Stem).ToHashSet();
        if (pw.Count == 0)
        {
            return 0;
        }

        var overlap = pw.Count(lw.Contains);
        if (overlap == 0)
        {
            return 0;
        }

        // Every word of the phrase in the label ("impact" → "Customer impact") is strong evidence; extra phrase words
        // the label lacks ("impact description" → "Description") mean a different, probably new, field.
        var labelWords = l.Split(' ').Select(Stem).ToHashSet();
        return 0.9 * overlap / pw.Count * (0.5 + (0.5 * Math.Min(1.0, (double)overlap / labelWords.Count)));
    }

    private static string Stem(string w) => w.Length > 3 && w.EndsWith('s') ? w[..^1] : w;

    internal sealed record OptionInfo(string Id, string Label);

    internal sealed record FieldInfo(string Key, string Label, string Type, string? ChoiceList, IReadOnlyList<OptionInfo> Options);

    internal sealed record Node(string Id, string Type, string? Title, string? Field, IReadOnlyList<Node> Children)
    {
        public static Node From(JsonNode n) => new(
            n["id"]!.GetValue<string>(),
            n["type"]!.GetValue<string>(),
            n["props"]?["title"]?.GetValue<string>(),
            n["props"]?["field"]?.GetValue<string>(),
            n["children"]?.AsArray().Where(c => c is not null).Select(c => From(c!)).ToList() ?? []);

        public Node? LastChild => Children.Count > 0 ? Children[^1] : null;

        public Node? Find(string id) => Id == id ? this : Children.Select(c => c.Find(id)).FirstOrDefault(x => x is not null);

        public Node? FindFirst(Func<Node, bool> predicate) => predicate(this) ? this : Children.Select(c => c.FindFirst(predicate)).FirstOrDefault(x => x is not null);

        public IEnumerable<Node> All() => Children.SelectMany(c => c.All()).Prepend(this);
    }

    /// <summary>Per-request state: cached entity description, view tree, and the draft version to send.</summary>
    private sealed class Session(AssistantRequest request, IToolGateway tools, IAssistantSink sink, CancellationToken ct)
    {
        private List<FieldInfo>? fields;
        private Node? view;
        private int? version;

        public AssistantRequest Request => request;

        public string Entity => request.DefaultEntity;

        public string RecordView => request.Selection.Count > 0 ? request.Selection[0].View : request.DefaultRecordView;

        public bool Changed { get; private set; }

        public string? ViewError { get; private set; }

        public Task EmitAsync(string type, string? text, IReadOnlyList<string>? targets = null, IReadOnlyList<string>? options = null, string? tool = null, int? seq = null) =>
            sink.EmitAsync(new AssistantEvent(request.ConversationId, type, text, tool, seq, targets, options), ct);

        public async Task<ToolOutcome> CallAsync(string tool, JsonObject args)
        {
            ct.ThrowIfCancellationRequested();
            await EmitAsync(AssistantEvent.Types.Tool, ToolNarration.Describe(tool, args), tool: tool);
            return await tools.CallAsync(tool, args, ct);
        }

        public async Task<string> BuildAsync(string tool, JsonObject args, string success, bool invalidates = false)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var call = (JsonObject)args.DeepClone();
                call["expected_version"] = await VersionAsync();
                var result = await CallAsync(tool, call);
                if (result.IsError)
                {
                    var code = result.Json?["error"]?["code"]?.GetValue<string>();
                    if (code == "draft_conflict" && attempt == 0)
                    {
                        version = null;
                        continue;
                    }

                    return "!" + result.ErrorMessage + (result.Suggestion is { } sug ? " " + sug : string.Empty);
                }

                version = result.Json?["version"]?.GetValue<int>() ?? version;
                foreach (var step in result.Json?["steps"]?.AsArray() ?? [])
                {
                    var targets = step?["targets"]?.AsArray().Select(t => t!.GetValue<string>()).ToList() ?? [];
                    await EmitAsync(AssistantEvent.Types.Step, step?["summary"]?.GetValue<string>(), targets, seq: step?["step"]?.GetValue<int>());
                }

                Changed = true;
                if (invalidates)
                {
                    fields = null;
                    view = null;
                }

                return success;
            }

            return "!Someone else kept changing the draft. Please try again.";
        }

        private async Task<int> VersionAsync()
        {
            if (version is { } v)
            {
                return v;
            }

            var draft = await CallAsync("open_draft", []);
            if (draft.IsError)
            {
                throw new InvalidOperationException(draft.ErrorMessage);
            }

            version = draft.Json?["version"]?.GetValue<int>() ?? 0;
            return version.Value;
        }

        public async Task<IReadOnlyList<FieldInfo>> FieldsAsync()
        {
            if (fields is not null)
            {
                return fields;
            }

            var result = await CallAsync("describe_entity", new JsonObject { ["entity"] = Entity });
            if (result.IsError)
            {
                throw new InvalidOperationException(result.ErrorMessage);
            }

            fields = (result.Json?["fields"]?.AsArray() ?? []).Where(f => f is not null).Select(f => new FieldInfo(
                f!["key"]!.GetValue<string>(),
                f["label"]!.GetValue<string>(),
                f["type"]!.GetValue<string>(),
                f["choiceList"]?.GetValue<string>(),
                f["options"]?.AsArray().Select(o => new OptionInfo(o!["id"]!.GetValue<string>(), o["label"]!.GetValue<string>())).ToList() ?? [])).ToList();
            return fields;
        }

        public async Task<FieldInfo?> ResolveFieldAsync(string phrase)
        {
            var selectedField = request.Selection.FirstOrDefault(sel => sel.Type == "field");
            if (HerePattern().IsMatch(phrase) && selectedField is not null)
            {
                phrase = selectedField.Label ?? phrase;
            }

            var all = await FieldsAsync();
            var best = all.Select(f => (f, score: Score(phrase, f.Label, f.Key))).OrderByDescending(x => x.score).FirstOrDefault();
            return best.score >= 0.5 ? best.f : null;
        }

        public async Task<string> UnknownFieldAsync(string phrase)
        {
            var all = await FieldsAsync();
            var close = all.Select(f => (f, score: Score(phrase, f.Label, f.Key))).Where(x => x.score > 0).OrderByDescending(x => x.score).Take(3).Select(x => x.f.Label).ToList();
            return $"!I couldn't find a field called \"{StripArticles(phrase)}\".{(close.Count > 0 ? $" Did you mean {string.Join(" or ", close)}?" : string.Empty)}";
        }

        public string ListView => request.DefaultEntity.Split('.') is [var ns, var module, _] ? $"{ns}.{module}.list" : "core.tickets.list";

        public async Task<Node?> ViewAsync(string viewId)
        {
            if (viewId == RecordView)
            {
                return await ViewAsync();
            }

            var other = await CallAsync("get_view", new JsonObject { ["view"] = viewId });
            return other.IsError || other.Json?["layout"] is not { } l ? null : Node.From(l);
        }

        public async Task<Node?> ViewAsync()
        {
            if (view is not null)
            {
                return view;
            }

            var result = await CallAsync("get_view", new JsonObject { ["view"] = RecordView });
            view = result.IsError || result.Json?["layout"] is not { } layout ? null : Node.From(layout);
            ViewError = view is null ? result.ErrorMessage ?? "the page layout was empty" : null;
            return view;
        }

        public async Task<Node?> SelectedContainerAsync()
        {
            var v = await ViewAsync();
            return request.Selection.Select(sel => v?.Find(sel.Node)).FirstOrDefault(n => n is not null && n.Type is "layout.card" or "layout.column" or "layout.tab");
        }

        public async Task<Node?> TargetCardAsync(string? phrase)
        {
            var v = await ViewAsync();
            if (v is null)
            {
                return null;
            }

            if (phrase is null || HerePattern().IsMatch(phrase))
            {
                var selected = request.Selection.Select(sel => v.Find(sel.Node)).FirstOrDefault(n => n?.Type == "layout.card");
                if (selected is not null || phrase is not null)
                {
                    return selected;
                }

                return v.FindFirst(n => n.Type == "layout.card" && n.Title == "Details") ?? v.FindFirst(n => n.Type == "layout.card");
            }

            var cards = v.All().Where(n => n.Type == "layout.card" && n.Title is not null).ToList();
            var best = cards.Select(c => (c, score: Score(phrase.Replace(" card", string.Empty, StringComparison.OrdinalIgnoreCase), c.Title!, c.Id.Split('.').Last()))).OrderByDescending(x => x.score).FirstOrDefault();
            return best.score >= 0.5 ? best.c : null;
        }

        public async Task<Node?> CardContainingAsync(string? field)
        {
            if (field is null)
            {
                return null;
            }

            var v = await ViewAsync();
            return v?.All().FirstOrDefault(n => n.Type == "layout.card" && n.Children.Any(c => c.Field == field));
        }
    }

    [GeneratedRegex("""(?<label>"[^"]+"|“[^”]+”)\s+(?<type>(?:[\w/-]+\s+){0,2}?)field\b""", RegexOptions.IgnoreCase)]
    private static partial Regex LabelFirst();

    [GeneratedRegex(@"(?:\.\s+|;\s*|\s+and then\s+|\s*\n+\s*)", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseSplit();

    [GeneratedRegex(@"\s*(?:,\s*(?:and|or)\s+|,\s*|\s+and\s+|\s+or\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ListSplit();

    [GeneratedRegex(@"^(?:the|a|an)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ArticlePattern();

    [GeneratedRegex(@"\b(?:here|this|selected|it)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HerePattern();

    [GeneratedRegex("""^(?:please\s+)?(?:can you\s+)?(?:add|create)\s+(?:(?:a|an|new|another)\s+)*(?<req>(?:required|mandatory|optional)\s+)?(?<type>(?:[\w/-]+\s+){0,2}?)field\s+(?:called\s+|named\s+|for\s+)?(?<label>"[^"]+"|“[^”]+”|'[^']+'|[\w][\w\s-]*?)(?:\s+(?:to|on|for)\s+(?:the\s+)?(?:tickets?|ticket\s+(?:form|page)))?(?:\s+with\s+(?:the\s+)?(?:options|choices|values)\s+(?<opts>.+?))?(?:,?\s*(?:and\s+)?(?:put|place|show|add|display)\s+it\s+(?:in|into|on|to|under)\s+(?:the\s+)?(?<card>.+?)(?:\s+card)?|\s+(?<card>here|(?:in|to|into)\s+(?:this|the selected)\s+card))?$""", RegexOptions.IgnoreCase)]
    private static partial Regex AddFieldPattern();

    [GeneratedRegex(@"^(?:when|if|whenever)\s+(?<cond>.+?),?\s+(?:then\s+)?(?:(?<verb>require|hide|show|lock)|make)\s+(?:the\s+)?(?<target>.+?)(?:\s+(?:required|mandatory|read[- ]only))?(?:\s+field)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionalPattern();

    [GeneratedRegex(@"^(?:(?<verb>require|hide|show|lock)|make)\s+(?:the\s+)?(?<target>.+?)(?:\s+(?:required|mandatory|read[- ]only))?\s+(?:(?:when|if|whenever)|(?<neg>unless))\s+(?<cond>.+?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionalTrailingPattern();

    [GeneratedRegex("""^(?:please\s+)?(?:show|add|display|put)\s+(?:a|an)\s+(?<tone>warning|info|information|critical|success|error|danger|note)?\s*(?:callout|banner|message|notice|alert)(?:\s+(?:saying|that says|with (?:the )?text)\s+(?<text>"[^"]+"|“[^”]+”|'[^']+'))?\s+(?:when|if|whenever|for)\s+(?<cond>.+?)$""", RegexOptions.IgnoreCase)]
    private static partial Regex CalloutPattern();

    [GeneratedRegex("""^(?:add|create)\s+(?:a|an|new)?\s*(?<title>"[^"]+"|“[^”]+”|[\w][\w\s]*?)\s+card\s+(?:with|showing|containing)\s+(?<fields>.+)$""", RegexOptions.IgnoreCase)]
    private static partial Regex AddCardPattern();

    [GeneratedRegex(@"^(?:add|show)\s+(?:an?\s+|the\s+)?(?:column\s+for\s+(?:the\s+)?(?<field>.+?)|(?<field>.+?)\s+column)\s+(?:to|in|on)\s+(?:the\s+)?(?:tickets?\s+)?(?:list|table)(?:\s+view)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddColumnPattern();

    [GeneratedRegex(@"^(?:make|set)\s+(?:the\s+)?(?<field>.+?)(?:\s+field)?\s+(?:required|mandatory)(?:\s+always)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MakeRequiredPattern();

    [GeneratedRegex(@"^(?:the\s+)?(?<field>.+?)\s+(?:must|should|needs to)\s+(?:be|have)\s+at least\s+(?<n>\d+)\s+(?:characters|chars|letters)(?:\s+long)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MinLengthPattern();

    [GeneratedRegex(@"^(?:(?<hide>hide|remove)|(?<show>show|unhide))\s+(?:the\s+)?(?<field>.+?)(?:\s+field)?(?:\s+from\s+.+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex HidePattern();

    [GeneratedRegex(@"^(?:rename|relabel|change the (?:label|name) of)\s+(?:the\s+)?(?<field>.+?)(?:\s+field)?\s+(?:to|as)\s+(?<to>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RenamePattern();

    [GeneratedRegex(@"^add\s+(?:an?\s+|the\s+)?(?:options?|choices?|values?)\s+(?<opt>.+?)\s+to\s+(?:the\s+)?(?<field>.+?)(?:\s+(?:field|list|dropdown))?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddOptionPattern();

    [GeneratedRegex(@"^(?:please\s+)?undo(?:\s+(?:that|it|the last (?:change|step)|your last change))?$", RegexOptions.IgnoreCase)]
    private static partial Regex UndoPattern();

    [GeneratedRegex(@"^(?:please\s+)?(?:publish(?:\s+it|\s+this|\s+the draft)?|request (?:a )?publish|(?:i'?m |we'?re )?ready to publish|submit (?:it|the draft) for (?:review|publishing)|ship it)$", RegexOptions.IgnoreCase)]
    private static partial Regex PublishPattern();

    [GeneratedRegex(@"^(?:what (?:did you|have (?:we|you)) changed?|what changed|show (?:me )?(?:the )?(?:changes|diff)|summarize the (?:draft|changes))\??$", RegexOptions.IgnoreCase)]
    private static partial Regex DiffPattern();

    [GeneratedRegex(@"^(?:(?:a|the|it'?s?)\s+)?(?:(?:ticket|record|request)(?:'s)?\s+)?(?:is\s+)?", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionPrefix();

    [GeneratedRegex(@"^(?:is\s+)?(?:unassigned|not assigned|has no assignee|nobody is assigned|no one is assigned)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnassignedPattern();

    [GeneratedRegex(@"^(?<field>.+?)\s+(?:is|are)\s+(?<state>empty|blank|missing|not set|set|filled(?: in)?|provided)$", RegexOptions.IgnoreCase)]
    private static partial Regex EmptyPattern();

    [GeneratedRegex(@"^(?<field>.+?)\s+(?:is\s+)?(?<op>above|over|greater than|more than|below|under|less than|equals|=)\s+(?<n>-?\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ComparePattern();

    [GeneratedRegex(@"^(?<field>.+?)\s+(?:is|=|==|equals|is set to)\s+(?<not>not\s+)?(?<value>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EqualsPattern();
}
