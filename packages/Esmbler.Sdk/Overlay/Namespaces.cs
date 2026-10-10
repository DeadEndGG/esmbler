using System.Text.RegularExpressions;

namespace Esmbler.Sdk.Overlay;

/// <summary>
/// Identifier namespaces (master spec: core.*, publisher.*, tenant__*). Base field keys never contain "__";
/// tenant-created identifiers always start with "tenant__"; package contributions use "&lt;publisher&gt;__".
/// </summary>
public static partial class Namespaces
{
    public const string TenantPrefix = "tenant__";

    public static bool IsTenantOwned(string id) =>
        id.StartsWith(TenantPrefix, StringComparison.Ordinal) || id.Contains("." + TenantPrefix, StringComparison.Ordinal) || id.Contains("/" + TenantPrefix, StringComparison.Ordinal);

    public static bool IsValidFieldKey(string key) => FieldKeyRegex().IsMatch(key);

    public static bool IsValidId(string id) => IdRegex().IsMatch(id);

    /// <summary>Turns a label into a tenant-namespaced key: "Vendor name" → "tenant__vendor_name".</summary>
    public static string TenantKeyFromLabel(string label)
    {
        var slug = SlugRegex().Replace(label.Trim().ToLowerInvariant(), "_").Trim('_');
        if (slug.Length == 0 || !char.IsLetter(slug[0]))
        {
            slug = "f_" + slug;
        }

        return TenantPrefix + (slug.Length > 48 ? slug[..48] : slug);
    }

    public static string NewTenantNodeId() => TenantPrefix + "n_" + Guid.NewGuid().ToString("N")[..10];

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex FieldKeyRegex();

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
