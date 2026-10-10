namespace Esmbler.Kernel;

/// <summary>
/// Domain error with a stable code and HTTP-like status. Rendered as RFC 9457 problem details by the API and as
/// structured tool errors by MCP. Messages are in the user's language with a suggested fix where possible.
/// </summary>
public class KernelException : Exception
{
    public KernelException(string code, string message, int status = 400, string? target = null, string? suggestion = null, object? details = null)
        : base(message)
    {
        Code = code;
        Status = status;
        Target = target;
        Suggestion = suggestion;
        Details = details;
    }

    public KernelException()
        : this("error", "Error")
    {
    }

    public KernelException(string message)
        : this("error", message)
    {
    }

    public KernelException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "error";
        Status = 500;
    }

    public string Code { get; }

    public int Status { get; }

    public string? Target { get; }

    public string? Suggestion { get; }

    public object? Details { get; }

    public static KernelException NotFound(string what, string? target = null) => new("not_found", $"{what} was not found or you do not have access to it.", 404, target);

    public static KernelException Forbidden(string message, string? target = null, string? suggestion = null) => new("forbidden", message, 403, target, suggestion);

    public static KernelException Conflict(string code, string message, object? details = null, string? suggestion = null) => new(code, message, 409, null, suggestion, details);
}

public sealed record FieldError(string Field, string Code, string Message, string? Suggestion = null);

/// <summary>Validation failure listing every field problem at once (UI shows them inline).</summary>
public sealed class ValidationFailedException(IReadOnlyList<FieldError> errors)
    : KernelException("validation_failed", errors.Count == 1 ? errors[0].Message : $"{errors.Count} fields need attention.", 422, details: errors)
{
    public IReadOnlyList<FieldError> Errors { get; } = errors;
}
