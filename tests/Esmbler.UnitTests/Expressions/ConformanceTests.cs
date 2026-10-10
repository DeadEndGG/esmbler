using System.Text.Json;
using Esmbler.Expressions;

namespace Esmbler.UnitTests.Expressions;

/// <summary>REQ-RULE-003: server and client evaluators stay aligned via shared conformance cases.</summary>
public class ConformanceTests
{
    private static readonly JsonDocument Doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "cases.json")));

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("expr").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_matches_expected(string expr)
    {
        var c = Doc.RootElement.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("expr").GetString() == expr);
        var vars = (IReadOnlyDictionary<string, object?>)Values.FromJson(Doc.RootElement.GetProperty("vars"))!;
        var schema = new ExpressionSchema().WithVariable("record", VariableSchema.AnyMap).WithVariable("user", VariableSchema.AnyMap);

        if (c.TryGetProperty("error", out var error))
        {
            var ex = Assert.Throws<ExpressionException>(() => Expression.Compile(expr, schema).Evaluate(vars));
            Assert.Equal(error.GetString(), ex.Code);
            if (c.TryGetProperty("phase", out var phase) && phase.GetString() == "check")
            {
                Assert.Throws<ExpressionException>(() => Expression.Compile(expr, schema));
            }

            return;
        }

        var expected = Values.FromJson(c.GetProperty("expect"));
        var actual = Expression.Compile(expr, schema).Evaluate(vars);
        Assert.True(Expression.ValueEquals(expected, actual) && SameKind(expected, actual), $"{expr}: expected {Show(expected)} got {Show(actual)}");
    }

    [Fact]
    public void Unknown_field_suggests_closest_match()
    {
        var schema = new ExpressionSchema().WithVariable(
            "record",
            VariableSchema.Record(new Dictionary<string, Esmbler.Expressions.ValueType> { ["justification"] = Esmbler.Expressions.ValueType.String, ["category"] = Esmbler.Expressions.ValueType.Number }));
        var ex = Assert.Throws<ExpressionException>(() => Expression.Compile("has(record.justifcation)", schema));
        Assert.Equal("expr.unknown_field", ex.Code);
        Assert.Equal("record.justification", ex.Suggestion);
    }

    [Fact]
    public void Type_mismatch_on_typed_field_explains_fix()
    {
        var schema = new ExpressionSchema().WithVariable(
            "record",
            VariableSchema.Record(new Dictionary<string, Esmbler.Expressions.ValueType> { ["category"] = Esmbler.Expressions.ValueType.Number }));
        var ex = Assert.Throws<ExpressionException>(() => Expression.Compile("record.category == '8'", schema));
        Assert.Equal("expr.type_mismatch", ex.Code);
        Assert.Contains("'record.category' is a number", ex.Message);
    }

    [Fact]
    public void Node_budget_is_enforced()
    {
        var big = string.Join(" + ", Enumerable.Repeat("1", 300));
        var ex = Assert.Throws<ExpressionException>(() => Expression.Compile(big));
        Assert.Equal("expr.too_complex", ex.Code);
    }

    [Fact]
    public void References_are_collected()
    {
        var schema = new ExpressionSchema().WithVariable("record", VariableSchema.AnyMap);
        var compiled = Expression.Compile("record.category == 8 && has(record.justification)", schema);
        Assert.Equal(new[] { "record.category", "record.justification" }, compiled.References.Order());
    }

    private static bool SameKind(object? a, object? b) => (a, b) switch
    {
        (double, double) or (null, null) or (string, string) or (bool, bool) => true,
        (IReadOnlyList<object?>, IReadOnlyList<object?>) => true,
        _ => false,
    };

    private static string Show(object? v) => v is null ? "null" : $"{v} ({v.GetType().Name})";
}
