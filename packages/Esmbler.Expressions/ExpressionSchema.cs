namespace Esmbler.Expressions;

/// <summary>Static types known to the checker. <see cref="Any"/> disables type checks for a value.</summary>
public enum ValueType
{
    Any,
    Null,
    Bool,
    Number,
    String,
    List,
    Map,
}

/// <summary>
/// Describes the variables an expression may reference. A variable with <see cref="VariableSchema.Fields"/>
/// is a map whose keys are checked (e.g. <c>record.category</c>); unknown keys are compile errors with suggestions.
/// </summary>
public sealed class ExpressionSchema
{
    public Dictionary<string, VariableSchema> Variables { get; } = new(StringComparer.Ordinal);

    public ExpressionSchema WithVariable(string name, VariableSchema schema)
    {
        Variables[name] = schema;
        return this;
    }
}

public sealed record VariableSchema(ValueType Type, IReadOnlyDictionary<string, ValueType>? Fields = null)
{
    public static VariableSchema AnyMap { get; } = new(ValueType.Map);

    public static VariableSchema Record(IReadOnlyDictionary<string, ValueType> fields) => new(ValueType.Map, fields);
}
