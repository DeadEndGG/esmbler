using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Esmbler.Kernel.Authorization;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Infrastructure.Notifications;

/// <summary>What one recipient may see while a template renders.</summary>
public sealed record RenderScope(EffectivePermissions? Permissions, bool External, string? RecipientName)
{
    /// <summary>
    /// Text of the public reply an <c>activity.added</c> event is about, for <c>{{activity.body}}</c> (ADR 0023). Set only
    /// for public replies that are not redacted; internal work notes never get here.
    /// </summary>
    public string? Reply { get; init; }

    /// <summary>For previews: a field renders only if the person previewing may also see it.</summary>
    public EffectivePermissions? Viewer { get; init; }
}

/// <summary>
/// Renders a template for one recipient and channel (REQ-NOT-002, ADR 0021). Record fields render only when the recipient
/// may read them; external recipients see only the template's external fields and only public events' payload values.
/// Output is plain text; nothing is interpreted as markup.
/// </summary>
public static partial class NotificationRenderer
{
    public const int MaxSubject = 200;
    public const int MaxBody = 4000;
    public const int MaxSms = 480;

    public static (string Subject, string Body) Render(NotificationTemplateSpec template, string channel, EntitySpec? entity, JsonObject? record, string? number, JsonObject eventPayload, string sensitivity, string? link, RenderScope scope)
    {
        var variant = template.Variants.GetValueOrDefault(channel);
        var subject = Fill(variant?.Subject ?? template.Subject, template, entity, record, number, eventPayload, sensitivity, link, scope);
        var body = Fill(variant?.Body ?? template.Body, template, entity, record, number, eventPayload, sensitivity, link, scope);
        var bodyLimit = channel == NotificationChannels.Sms ? MaxSms : MaxBody;
        return (Truncate(OneLine(subject), MaxSubject), Truncate(body, bodyLimit));
    }

    private static string Fill(string text, NotificationTemplateSpec template, EntitySpec? entity, JsonObject? record, string? number, JsonObject payload, string sensitivity, string? link, RenderScope scope) =>
        Placeholder().Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (name == "link")
            {
                return link ?? string.Empty;
            }

            if (name == "recipient.name")
            {
                return scope.RecipientName ?? string.Empty;
            }

            if (name == "activity.body")
            {
                // People outside the workspace get no conversation text.
                return scope.External ? string.Empty : scope.Reply ?? string.Empty;
            }

            if (name == "record.number")
            {
                // External recipients see the number only if the template lists the auto-number field as external.
                if (scope.External && (entity?.Fields.Find(f => f.Type == FieldTypes.AutoNumber)?.Key is not { } numberField || !template.ExternalFields.Contains(numberField)))
                {
                    return string.Empty;
                }

                return number ?? string.Empty;
            }

            if (name.StartsWith("event.", StringComparison.Ordinal))
            {
                // Restricted payloads may carry hidden fields: never text (the validator refuses such templates too).
                // External recipients only get values of public events (ADR 0017 sensitivity).
                return sensitivity == EventSensitivities.Restricted || (scope.External && sensitivity != EventSensitivities.Public) ? string.Empty : Text(payload[name["event.".Length..]]);
            }

            if (name.StartsWith("record.", StringComparison.Ordinal) && entity is not null && record is not null)
            {
                var key = name["record.".Length..];
                var visible = scope.External
                    ? template.ExternalFields.Contains(key)
                    : scope.Permissions is { } perms && perms.FieldAccess(entity, key) != FieldAccessLevels.Hidden;
                visible &= scope.Viewer is null || scope.Viewer.FieldAccess(entity, key) != FieldAccessLevels.Hidden;
                return visible ? Text(record[key]) : string.Empty;
            }

            return string.Empty;
        });

    /// <summary>Plain text of sanitized rich text (paragraphs and line breaks kept, tags dropped, entities decoded).</summary>
    public static string PlainText(string html)
    {
        var text = BlockEnd().Replace(html, "\n");
        text = Tag().Replace(text, string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        return BlankLines().Replace(text.Replace("\r", string.Empty, StringComparison.Ordinal), "\n\n").Trim();
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join(", ", a.Select(Text)),
        _ => node.ToJsonString(),
    };

    private static string OneLine(string s) => s.ReplaceLineEndings(" ").Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    [GeneratedRegex(@"\{\{\s*([a-z][a-z0-9_.]*)\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|h[1-6]|blockquote|pre|tr)>", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex BlockEnd();

    [GeneratedRegex("<[^>]*>", RegexOptions.NonBacktracking)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\n{3,}", RegexOptions.NonBacktracking)]
    private static partial Regex BlankLines();
}
