using Esmbler.Expressions;
using Esmbler.Sdk.Specs;
using ExprType = Esmbler.Expressions.ValueType;

namespace Esmbler.Kernel.Configuration;

/// <summary>Builds the typed expression schema (record/prior/user) for an entity.</summary>
public static class EntitySchema
{
    public static ExprType ExpressionType(string fieldType) => fieldType switch
    {
        FieldTypes.Integer or FieldTypes.Decimal => ExprType.Number,
        FieldTypes.Toggle => ExprType.Bool,
        FieldTypes.MultiSelect => ExprType.List,
        _ => ExprType.String,
    };

    /// <summary>Schema for a fulfillment task condition: the request's fields as <c>record</c> and its answers as <c>variables</c>.</summary>
    public static ExpressionSchema ForRequest(EntitySpec requestEntity, CatalogItemSpec item)
    {
        // Task conditions run in the fulfillment worker, where no person is acting: only the request and its answers.
        var fields = requestEntity.Fields.ToDictionary(f => f.Key, f => ExpressionType(f.Type), StringComparer.Ordinal);
        fields["id"] = ExprType.String;
        var variables = item.Variables.ToDictionary(v => v.Key, v => ExpressionType(v.Type), StringComparer.Ordinal);
        return new ExpressionSchema()
            .WithVariable("record", VariableSchema.Record(fields))
            .WithVariable("now", new VariableSchema(ExprType.String))
            .WithVariable("variables", VariableSchema.Record(variables));
    }

    public static ExpressionSchema For(EntitySpec entity)
    {
        var fields = entity.Fields.ToDictionary(f => f.Key, f => ExpressionType(f.Type), StringComparer.Ordinal);
        fields["id"] = ExprType.String;
        return new ExpressionSchema()
            .WithVariable("record", VariableSchema.Record(fields))
            .WithVariable("prior", VariableSchema.Record(fields))
            .WithVariable("now", new VariableSchema(ExprType.String))
            .WithVariable("user", VariableSchema.Record(new Dictionary<string, ExprType>
            {
                ["id"] = ExprType.String,
                ["roles"] = ExprType.List,
                ["groups"] = ExprType.List,
            }));
    }
}
