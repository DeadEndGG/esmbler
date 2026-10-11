namespace Esmbler.Kernel.Infrastructure.Services;

/// <summary>CSV cells for exports people open in spreadsheets (audit log, explorer results, figures).</summary>
public static class CsvText
{
    /// <summary>
    /// One cell: quoted when it holds a comma, quote or line break; prefixed with a quote when a spreadsheet would run it
    /// as a formula (starts with = + - @ | %, also after leading spaces, tab or return).
    /// </summary>
    public static string Cell(string value)
    {
        var start = value.TrimStart(' ', '\t', '\r', '\n');
        var v = start.Length > 0 && start[0] is '=' or '+' or '-' or '@' or '|' or '%' || (value.Length > 0 && value[0] is '\t' or '\r') ? "'" + value : value;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    public static string Row(IEnumerable<string> cells) => string.Join(',', cells.Select(Cell));
}
