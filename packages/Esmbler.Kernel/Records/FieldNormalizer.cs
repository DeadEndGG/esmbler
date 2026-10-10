using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Esmbler.Expressions;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Records;

/// <summary>Sanitizes rich text to the allowed tag set (no scripts, styles, iframes, event handlers). Port; adapter in infrastructure.</summary>
public interface IRichTextSanitizer
{
    string Sanitize(string html);
}

/// <summary>
/// Record contract: converts an input value to the stored representation for its field type and checks
/// type-level constraints (REQ-REC-005, REQ-RULE-001). Rule-driven checks (required, read-only, options)
/// happen afterwards in the rules phase.
/// Stored representations: text → string; integer/decimal → JSON number (decimal rounded to scale);
/// date → "YYYY-MM-DD" (local calendar date); date_time → UTC ISO-8601 "yyyy-MM-ddTHH:mm:ss.fffZ";
/// select/status/priority → option ID; multi_select → array of option IDs; toggle → bool; references → uuid string.
/// </summary>
public sealed partial class FieldNormalizer(IRichTextSanitizer sanitizer)
{
    public (JsonNode? Value, FieldError? Error) Normalize(FieldSpec field, JsonNode? input, SpecCatalog config)
    {
        if (input is null || (input is JsonValue v0 && v0.GetValueKind() == JsonValueKind.Null))
        {
            return field.Type == FieldTypes.Toggle ? (JsonValue.Create(false), null) : (null, null);
        }

        try
        {
            return field.Type switch
            {
                FieldTypes.ShortText or FieldTypes.LongText => Text(field, input, sanitize: false),
                FieldTypes.RichText => Text(field, input, sanitize: true),
                FieldTypes.Email => Email(field, input),
                FieldTypes.Integer => Number(field, input, integer: true),
                FieldTypes.Decimal => Number(field, input, integer: false),
                FieldTypes.Date => Date(field, input),
                FieldTypes.DateTime => DateTimeValue(field, input),
                FieldTypes.Toggle => Bool(field, input),
                FieldTypes.SingleSelect or FieldTypes.Status or FieldTypes.Priority => Choice(field, input, config),
                FieldTypes.MultiSelect => MultiChoice(field, input, config),
                FieldTypes.Lookup or FieldTypes.User or FieldTypes.Group => Reference(field, input),
                FieldTypes.AutoNumber => (null, Error(field, "read_only", $"{field.Label} is assigned automatically.")),
                _ => (null, Error(field, "unknown_type", $"{field.Label} has an unsupported type.")),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            return (null, Error(field, "invalid_type", $"{field.Label} has a value of the wrong type.", Hint(field)));
        }
    }

    private (JsonNode?, FieldError?) Text(FieldSpec field, JsonNode input, bool sanitize)
    {
        var s = input.GetValue<string>().Trim();
        if (sanitize)
        {
            s = sanitizer.Sanitize(s).Trim();
        }

        if (s.Length == 0)
        {
            return (null, null);
        }

        var max = field.MaxLength ?? (field.Type switch { FieldTypes.LongText => 4000, FieldTypes.RichText => 50000, _ => 255 });
        if (s.Length > max)
        {
            return (null, Error(field, "too_long", $"{field.Label} can be at most {max} characters (it has {s.Length}).", $"Shorten it to {max} characters."));
        }

        if (field.MinLength is { } min && s.Length < min)
        {
            return (null, Error(field, "too_short", $"{field.Label} needs at least {min} characters."));
        }

        if (field.Pattern is { } pattern && !Regex.IsMatch(s, pattern, RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50)))
        {
            return (null, Error(field, "pattern", field.PatternMessage ?? $"{field.Label} is not in the expected format."));
        }

        return (JsonValue.Create(s), null);
    }

    private static (JsonNode?, FieldError?) Email(FieldSpec field, JsonNode input)
    {
        var s = input.GetValue<string>().Trim();
        if (s.Length == 0)
        {
            return (null, null);
        }

        if (s.Length > (field.MaxLength ?? 254) || !EmailRegex().IsMatch(s))
        {
            return (null, Error(field, "invalid_email", $"{field.Label} must be an email address like name@example.com."));
        }

        var at = s.LastIndexOf('@');
        return (JsonValue.Create(s[..at] + "@" + s[(at + 1)..].ToLowerInvariant()), null);
    }

    private static (JsonNode?, FieldError?) Number(FieldSpec field, JsonNode input, bool integer)
    {
        decimal value;
        if (input is JsonValue jv && jv.GetValueKind() == JsonValueKind.String)
        {
            var s = jv.GetValue<string>().Trim();
            if (s.Length == 0)
            {
                return (null, null);
            }

            if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            {
                return (null, Error(field, "not_a_number", $"{field.Label} must be a number.", integer ? "e.g. 3" : "e.g. 2.5"));
            }
        }
        else
        {
            value = input.GetValue<decimal>();
        }

        if (integer && value != decimal.Truncate(value))
        {
            return (null, Error(field, "not_an_integer", $"{field.Label} must be a whole number."));
        }

        if (!integer)
        {
            value = Math.Round(value, field.Scale ?? 2, MidpointRounding.AwayFromZero);
        }

        if (field.Min is { } min && value < min)
        {
            return (null, Error(field, "below_min", $"{field.Label} must be at least {min}."));
        }

        if (field.Max is { } max && value > max)
        {
            return (null, Error(field, "above_max", $"{field.Label} must be at most {max}."));
        }

        return (integer ? JsonValue.Create((long)value) : JsonValue.Create(value), null);
    }

    private static (JsonNode?, FieldError?) Date(FieldSpec field, JsonNode input)
    {
        var s = input.GetValue<string>().Trim();
        if (s.Length == 0)
        {
            return (null, null);
        }

        return DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? (JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), null)
            : (null, Error(field, "invalid_date", $"{field.Label} must be a date like 2026-10-31."));
    }

    private static (JsonNode?, FieldError?) DateTimeValue(FieldSpec field, JsonNode input)
    {
        var s = input.GetValue<string>().Trim();
        if (s.Length == 0)
        {
            return (null, null);
        }

        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) || !HasZone(s))
        {
            return (null, Error(field, "invalid_datetime", $"{field.Label} must be a date and time with a time zone, like 2026-10-31T14:00:00Z."));
        }

        return (JsonValue.Create(FormatInstant(dto)), null);

        static bool HasZone(string text) => text.EndsWith('Z') || text.EndsWith('z') || ZoneSuffix().IsMatch(text);
    }

    public static string FormatInstant(DateTimeOffset dto) => dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static (JsonNode?, FieldError?) Bool(FieldSpec field, JsonNode input) =>
        input is JsonValue jv && jv.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? (JsonValue.Create(jv.GetValue<bool>()), null)
            : (null, Error(field, "invalid_type", $"{field.Label} must be true or false."));

    private static (JsonNode?, FieldError?) Choice(FieldSpec field, JsonNode input, SpecCatalog config)
    {
        var id = input.GetValue<string>();
        if (id.Length == 0)
        {
            return (null, null);
        }

        var list = config.FindChoiceList(field.ChoiceList!);
        var option = list?.Find(id);
        if (option is null)
        {
            var visible = list?.Options.Where(o => !o.Hidden).Select(o => o.Id) ?? [];
            var suggestion = Expression.Suggest(id, visible);
            return (null, Error(field, "invalid_option", $"'{id}' is not an option of {field.Label}." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"), $"Use one of: {string.Join(", ", visible)}."));
        }

        return (JsonValue.Create(id), null);
    }

    private static (JsonNode?, FieldError?) MultiChoice(FieldSpec field, JsonNode input, SpecCatalog config)
    {
        if (input is not JsonArray array)
        {
            return (null, Error(field, "invalid_type", $"{field.Label} must be a list of options."));
        }

        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            var (value, error) = Choice(field, item!, config);
            if (error is not null)
            {
                return (null, error);
            }

            if (value is not null && seen.Add(value.GetValue<string>()))
            {
                result.Add(value);
            }
        }

        if (field.MaxItems is { } maxItems && result.Count > maxItems)
        {
            return (null, Error(field, "too_many", $"{field.Label} allows at most {maxItems} options."));
        }

        return (result.Count == 0 ? null : result, null);
    }

    private static (JsonNode?, FieldError?) Reference(FieldSpec field, JsonNode input)
    {
        var s = input.GetValue<string>().Trim();
        if (s.Length == 0)
        {
            return (null, null);
        }

        return Guid.TryParse(s, out var id)
            ? (JsonValue.Create(id.ToString()), null)
            : (null, Error(field, "invalid_reference", $"{field.Label} must reference a {(field.Type == FieldTypes.Lookup ? "record" : field.Type)} by ID."));
    }

    private static string Hint(FieldSpec field) => field.Type switch
    {
        FieldTypes.Integer or FieldTypes.Decimal => "Send a number.",
        FieldTypes.Toggle => "Send true or false.",
        FieldTypes.MultiSelect => "Send a list of option IDs.",
        _ => "Send text.",
    };

    private static FieldError Error(FieldSpec field, string code, string message, string? suggestion = null) => new(field.Key, code, message, suggestion);

    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]+\.[^@\s]{2,}$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"[+-]\d{2}:?\d{2}$")]
    private static partial Regex ZoneSuffix();
}
