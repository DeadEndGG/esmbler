namespace Esmbler.Expressions;

/// <summary>Raised for syntax, check, or evaluation errors. <see cref="Code"/> is stable and machine readable.</summary>
public sealed class ExpressionException : Exception
{
    public ExpressionException(string code, string message, int position = -1, string? suggestion = null)
        : base(message)
    {
        Code = code;
        Position = position;
        Suggestion = suggestion;
    }

    public ExpressionException()
        : this("expr.error", "Expression error")
    {
    }

    public ExpressionException(string message)
        : this("expr.error", message)
    {
    }

    public ExpressionException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "expr.error";
        Position = -1;
    }

    public string Code { get; }

    public int Position { get; }

    public string? Suggestion { get; }
}
