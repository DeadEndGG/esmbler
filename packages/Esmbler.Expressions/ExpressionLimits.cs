namespace Esmbler.Expressions;

/// <summary>Static cost bounds. Expressions exceeding them are rejected at compile time.</summary>
public sealed record ExpressionLimits(int MaxLength = 2000, int MaxDepth = 32, int MaxNodes = 256)
{
    public static ExpressionLimits Default { get; } = new();
}
