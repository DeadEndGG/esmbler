using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Esmbler.Kernel.Authorization;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Infrastructure.Notifications;

/// <summary>What one recipient may see while a template renders.</summary>
public sealed record RenderScope(EffectivePermissions? Permissions, bool External, string? RecipientName)
{
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

    public static (string Subject, string Body) Render(NotificationTemplateSpec template, string channel, EntitySpec? entity, JsonObject? record, string? number, JsonObject eventPayload, bool publicEvent, string? link, RenderScope scope)
    {
        var variant = template.Variants.GetValueOrDefault(channel);
        var subject = Fill(variant?.Subject ?? template.Subject, template, entity, record, number, eventPayload, publicEvent, link, scope);
        var body = Fill(variant?.Body ?? template.Body, template, entity, record, number, eventPayload, publicEvent, link, scope);
        var bodyLimit = channel == NotificationChannels.Sms ? MaxSms : MaxBody;
        return (Truncate(OneLine(subject), MaxSubject), Truncate(body, bodyLimit));
    }

    private static string Fill(string text, NotificationTemplateSpec template, EntitySpec? entity, JsonObject? record, string? number, JsonObject payload, bool publicEvent, string? link, RenderScope scope) =>
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

            if (name == "record.number")
            {
                return number ?? string.Empty;
            }

            if (name.StartsWith("event.", StringComparison.Ordinal))
            {
                // External recipients only get values of public events (ADR 0017 sensitivity).
                return scope.External && !publicEvent ? string.Empty : Text(payload[name["event.".Length..]]);
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
}
