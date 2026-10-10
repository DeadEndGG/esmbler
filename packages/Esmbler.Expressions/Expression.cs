namespace Esmbler.Expressions;

/// <summary>A parsed and checked expression, safe to cache and evaluate concurrently.</summary>
public sealed class CompiledExpression
{
    internal CompiledExpression(string source, Node root, IReadOnlySet<string> references)
    {
        Source = source;
        Root = root;
        References = references;
    }

    public string Source { get; }

    public Node Root { get; }

    /// <summary>Variable members referenced, e.g. <c>record.category</c>. Used for dependency graphs.</summary>
    public IReadOnlySet<string> References { get; }

    public object? Evaluate(IReadOnlyDictionary<string, object?> variables) => Evaluator.Eval(Root, variables);

    /// <summary>Evaluates to a boolean; any non-boolean result is an error.</summary>
    public bool EvaluateBool(IReadOnlyDictionary<string, object?> variables) =>
        Evaluate(variables) switch
        {
            bool b => b,
            var other => throw new ExpressionException("expr.not_boolean", $"Expression must produce true/false but produced {Evaluator.TypeName(other)}."),
        };
}

public static class Expression
{
    /// <summary>Parses and statically checks <paramref name="source"/>. Throws <see cref="ExpressionException"/> with actionable messages.</summary>
    public static CompiledExpression Compile(string source, ExpressionSchema? schema = null, ExpressionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= ExpressionLimits.Default;
        var root = Parser.Parse(source, limits);
        var checker = Checker.Run(root, schema, limits);
        return new CompiledExpression(source, root, checker.References);
    }

    public static bool ValueEquals(object? left, object? right) => Evaluator.ValueEquals(left, right);

    public static string? Suggest(string name, IEnumerable<string> candidates) => Checker.Suggest(name, candidates);
}
