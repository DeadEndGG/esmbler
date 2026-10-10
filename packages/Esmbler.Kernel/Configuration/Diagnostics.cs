namespace Esmbler.Kernel.Configuration;

public static class Severity
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>
/// An actionable configuration problem: what is wrong (<see cref="Message"/>), where (<see cref="Target"/>),
/// and a valid alternative (<see cref="Suggestion"/>), so the AI builder can self-correct.
/// </summary>
public sealed record Diagnostic(string Severity, string Code, string Message, string? Target = null, string? Suggestion = null, int? OpIndex = null)
{
    public static Diagnostic Error(string code, string message, string? target = null, string? suggestion = null) =>
        new(Configuration.Severity.Error, code, message, target, suggestion);

    public static Diagnostic Warning(string code, string message, string? target = null, string? suggestion = null) =>
        new(Configuration.Severity.Warning, code, message, target, suggestion);

    public bool IsError => Severity == Configuration.Severity.Error;
}

public static class OpStatus
{
    public const string Applied = "applied";

    /// <summary>The base already has the requested value; the operation is a no-op.</summary>
    public const string Redundant = "redundant";

    /// <summary>The base changed the same property since the operation was authored.</summary>
    public const string Conflict = "conflict";

    /// <summary>The operation could not be applied (missing target, namespace violation, ...).</summary>
    public const string Failed = "failed";

    /// <summary>A later element.reset undid the operation; it no longer applies.</summary>
    public const string Reset = "reset";
}

public sealed record OpOutcome(int Index, string Op, string Summary, string Status, IReadOnlyList<Diagnostic> Diagnostics);
