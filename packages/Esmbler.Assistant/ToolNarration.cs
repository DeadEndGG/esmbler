using System.Text.Json.Nodes;

namespace Esmbler.Assistant;

/// <summary>Plain-language progress lines for tool calls ("Reading the Tickets fields…"), shown in the chat panel.</summary>
public static class ToolNarration
{
    public static string Describe(string tool, JsonObject? args)
    {
        string? Arg(string name) => args?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return tool switch
        {
            "open_draft" => "Opening your draft…",
            "describe_entity" => $"Reading the fields of {Friendly(Arg("entity"))}…",
            "list_entities" => "Looking at the record types…",
            "get_view" => $"Reading the layout of {Friendly(Arg("view"))}…",
            "list_elements" or "describe_element" => "Checking which elements I can use…",
            "get_draft" => "Checking the draft…",
            "get_customizations" => "Looking at what this workspace has customized…",
            "add_field" => $"Adding the field \"{Arg("label")}\"…",
            "update_field" => $"Updating {Arg("field")}…",
            "hide_field" => $"Hiding {Arg("field")}…",
            "add_choice_option" => $"Adding the option \"{Arg("label")}\"…",
            "add_card" => $"Adding the card \"{Arg("title")}\"…",
            "insert_node" => $"Adding a {Friendly(Arg("type"))}…",
            "update_node" or "move_node" or "remove_node" => "Rearranging the page…",
            "add_rule" => $"Adding a rule: {Arg("description")}…",
            "set_rule_enabled" or "remove_rule" => "Updating a rule…",
            "undo_step" => "Undoing a step…",
            "validate_draft" => "Validating the draft…",
            "diff_draft" => "Comparing the draft with what's published…",
            "simulate_rule" => "Trying the rules with sample values…",
            "request_publish" => "Asking a person to publish…",
            "sample_records" => "Looking at a few records…",
            "explain_access" => "Checking who can see what…",
            "preview_url" => "Getting the preview link…",
            "discard_draft" => "Discarding the draft…",
            _ => $"Running {tool}…",
        };
    }

    private static string Friendly(string? id) => id switch
    {
        null => "the page",
        "core.tickets.ticket" => "Tickets",
        "core.tickets.record" => "the ticket page",
        "core.tickets.list" => "the ticket list",
        "core.tickets.quick_create" => "the new-ticket form",
        _ when id.StartsWith("display.", StringComparison.Ordinal) => id["display.".Length..].Replace('_', ' '),
        _ => id,
    };
}
