using System.Globalization;

namespace Esmbler.Sdk.Modules;

/// <summary>Minimal semantic version (MAJOR.MINOR.PATCH[-pre]) with caret/tilde/exact range checks.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? Pre = null) : IComparable<SemVer>
{
    public static SemVer Parse(string text)
    {
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? text[..dash] : text;
        var parts = core.Split('.');
        if (parts.Length != 3)
        {
            throw new FormatException($"'{text}' is not a semantic version (expected MAJOR.MINOR.PATCH).");
        }

        return new SemVer(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture),
            dash >= 0 ? text[(dash + 1)..] : null);
    }

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0)
        {
            c = Minor.CompareTo(other.Minor);
        }

        if (c == 0)
        {
            c = Patch.CompareTo(other.Patch);
        }

        if (c == 0)
        {
            c = (Pre, other.Pre) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                _ => string.CompareOrdinal(Pre, other.Pre),
            };
        }

        return c;
    }

    /// <summary>Supports "^1.2.3", "~1.2.3", "1.2.3", and "*".</summary>
    public bool Satisfies(string range)
    {
        range = range.Trim();
        if (range == "*")
        {
            return true;
        }

        if (range.StartsWith('^'))
        {
            var min = Parse(range[1..]);
            return CompareTo(min) >= 0 && (min.Major > 0 ? Major == min.Major : Minor == min.Minor && Major == 0);
        }

        if (range.StartsWith('~'))
        {
            var min = Parse(range[1..]);
            return CompareTo(min) >= 0 && Major == min.Major && Minor == min.Minor;
        }

        return CompareTo(Parse(range)) == 0;
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;

    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;

    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre is null ? string.Empty : "-" + Pre);
}
